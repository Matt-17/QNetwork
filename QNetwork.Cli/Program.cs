using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using QNetwork.Core;

internal static class Program
{
    private sealed record RenderSegment(
        string Text,
        bool Highlight = false,
        bool Status = false);

    private sealed record UnitOption(string Label, double Divisor);

    private sealed record RateSnapshot(
        int Pid,
        string ProcessName,
        int Count,
        double Download,
        double Upload,
        double TotalDownload,
        double TotalUpload,
        double PeakDownload,
        double PeakUpload);

    private static readonly TrafficSortColumn[] SortColumns =
    [
        TrafficSortColumn.Pid,
        TrafficSortColumn.ProcessName,
        TrafficSortColumn.Received,
        TrafficSortColumn.Sent,
        TrafficSortColumn.Total,
        TrafficSortColumn.TotalReceived,
        TrafficSortColumn.TotalSent
    ];

    private static readonly TimeSpan[] RefreshIntervals =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(600),
        TimeSpan.FromMilliseconds(700),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(900),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    private static readonly UnitOption[] UnitOptions =
    [
        new("B/s", 1.0),
        new("KiB/s", 1024.0),
        new("MiB/s", 1024.0 * 1024.0)
    ];

    private static readonly Dictionary<string, (double Down, double Up)> PeaksByKey =
        new(StringComparer.OrdinalIgnoreCase);

    private static TrafficSortColumn ActiveSortColumn = TrafficSortColumn.Total;
    private static TrafficSortColumn PendingSortColumn = TrafficSortColumn.Total;
    private static int RefreshIntervalIndex = 9;
    private static int UnitIndex = 1;
    private static bool IsSelectingSortColumn;
    private static bool IsEditingNameFilter;
    private static bool ExcludeZeroTotal = true;
    private static bool IsHoldingLastSample;
    private static bool GroupByName;
    private static string ActiveNameFilter = string.Empty;
    private static string PendingNameFilter = string.Empty;
    private static string StatusMessage = string.Empty;
    private static int LastRenderedLineCount;
    private static double LastSampleSeconds = 1.0;
    private static IReadOnlyCollection<TrafficRow> CurrentRows = [];

    private static async Task<int> Main(string[] args)
    {
        bool elevate = args.Contains("--elevate", StringComparer.OrdinalIgnoreCase);
        bool once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);

        if (!NetworkTrafficMonitor.IsSupportedPlatform)
        {
            await Console.Error.WriteLineAsync("QNetwork only works on Windows.");
            return 1;
        }

        if (!NetworkTrafficMonitor.IsElevated)
        {
            if (elevate && TryRestartElevated(args.Where(arg =>
                    !arg.Equals("--elevate", StringComparison.OrdinalIgnoreCase))))
            {
                return 0;
            }

            await Console.Error.WriteLineAsync(
                "QNetwork must be run as administrator (ETW kernel tracing requires elevation).");
            await Console.Error.WriteLineAsync(
                "Run QNetwork.Cli with --elevate or right-click the executable and choose 'Run as administrator'.");
            return 1;
        }

        return once
            ? await RunOnce(json).ConfigureAwait(false)
            : await RunInteractive().ConfigureAwait(false);
    }

    private static async Task<int> RunOnce(bool json)
    {
        await using var monitor = new NetworkTrafficMonitor();

        try
        {
            monitor.Start();
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            List<TrafficRow> rows = monitor.ReadCurrentTraffic();
            LastSampleSeconds = 1.0;
            CurrentRows = rows;

            IReadOnlyList<RateSnapshot> snapshots = BuildSnapshots(rows);
            if (json)
            {
                var payload = new
                {
                    unit = CurrentUnit.Label,
                    sampledAt = DateTimeOffset.Now,
                    rows = snapshots
                };
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
            else
            {
                foreach (RateSnapshot row in snapshots.Take(20))
                {
                    Console.WriteLine(
                        $"{row.Pid,7} {Shorten(row.ProcessName, 30),-30} {row.Download,12:N1} {row.Upload,12:N1} {row.Download + row.Upload,12:N1} {CurrentUnit.Label}");
                }
            }

            return 0;
        }
        catch (InsufficientEtwResourcesException)
        {
            await Console.Error.WriteLineAsync(
                "Windows has no free ETW logger resources. Close tracing tools or restart Windows.");
            return 1;
        }
        finally
        {
            await monitor.StopAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> RunInteractive()
    {
        await using var monitor = new NetworkTrafficMonitor();
        using var cancellation = new CancellationTokenSource();

        try
        {
            monitor.Start();
        }
        catch (InsufficientEtwResourcesException)
        {
            await Console.Error.WriteLineAsync("Windows has no free ETW logger resources.");
            await Console.Error.WriteLineAsync(
                "Close other tracing tools or restart Windows if old ETW sessions are stuck.");
            await Console.Error.WriteLineAsync(
                "This program automatically cleans up its own old sessions on startup.");
            return 1;
        }

        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
            monitor.Stop();
        };

        SafeSetCursorVisible(false);
        var sampleTimer = Stopwatch.StartNew();
        RenderCurrentTraffic(CurrentRows);

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                bool inputChanged = HandleKeyboardInput();

                if (sampleTimer.Elapsed >= RefreshIntervals[RefreshIntervalIndex])
                {
                    double sampleSeconds = Math.Max(sampleTimer.Elapsed.TotalSeconds, 0.001);
                    sampleTimer.Restart();
                    List<TrafficRow> sampledRows = monitor.ReadCurrentTraffic();

                    if (TrafficRows.ShouldReplaceDisplayedRows(
                            sampledRows,
                            CurrentRows,
                            ExcludeZeroTotal))
                    {
                        LastSampleSeconds = sampleSeconds;
                        CurrentRows = sampledRows;
                        IsHoldingLastSample = false;
                    }
                    else
                    {
                        IsHoldingLastSample = true;
                    }

                    RenderCurrentTraffic(CurrentRows);
                }
                else if (inputChanged)
                {
                    RenderCurrentTraffic(CurrentRows);
                }

                await Task.Delay(25, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit through Ctrl+C.
        }
        finally
        {
            SafeSetCursorVisible(true);
            await monitor.StopAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private static UnitOption CurrentUnit => UnitOptions[UnitIndex];

    private static void RenderCurrentTraffic(IReadOnlyCollection<TrafficRow> rows)
    {
        int termWidth = Console.IsOutputRedirected ? 160 : Math.Max(1, Console.BufferWidth - 1);
        bool showTotals = termWidth >= 120;
        bool showPeaks = termWidth >= 148;

        List<string> dataLines = BuildSnapshots(rows)
            .Take(20)
            .Select(row => FormatSnapshotLine(row, showTotals, showPeaks))
            .ToList();

        var lines = new List<IReadOnlyList<RenderSegment>>
        {
            Line("QNetwork Monitor" + (GroupByName ? " [grouped]" : "")),
            Line(string.Empty),
            HeaderLine(showTotals, showPeaks),
            Line(new string('-', showPeaks ? 156 : showTotals ? 132 : 88))
        };

        foreach (string dataLine in dataLines)
            lines.Add(Line(dataLine));

        lines.Add(Line(string.Empty));

        if (IsEditingNameFilter)
            lines.Add(Line($"Search: {PendingNameFilter}_"));
        else if (!string.IsNullOrWhiteSpace(ActiveNameFilter))
            lines.Add(Line($"Search: {ActiveNameFilter}"));
        else
            lines.Add(Line(string.Empty));

        lines.Add(StatusLine(GetStatusText()));

        RenderLines(lines);
    }

    private static IReadOnlyList<RateSnapshot> BuildSnapshots(
        IReadOnlyCollection<TrafficRow> rows)
    {
        UnitOption unit = CurrentUnit;

        if (GroupByName)
        {
            return TrafficRows.SortGrouped(
                    TrafficRows.GroupByProcessName(
                        TrafficRows.Filter(rows, ExcludeZeroTotal, ActiveNameFilter)),
                    ActiveSortColumn)
                .Select(g =>
                {
                    double down = g.Received / unit.Divisor / LastSampleSeconds;
                    double up = g.Sent / unit.Divisor / LastSampleSeconds;
                    (double peakDown, double peakUp) = UpdatePeak(g.ProcessName, down, up);

                    return new RateSnapshot(
                        0,
                        g.ProcessName,
                        g.Count,
                        down,
                        up,
                        g.TotalReceived / unit.Divisor,
                        g.TotalSent / unit.Divisor,
                        peakDown,
                        peakUp);
                })
                .ToList();
        }

        return TrafficRows.Sort(
                TrafficRows.Filter(rows, ExcludeZeroTotal, ActiveNameFilter),
                ActiveSortColumn)
            .Select(row =>
            {
                double down = row.Received / unit.Divisor / LastSampleSeconds;
                double up = row.Sent / unit.Divisor / LastSampleSeconds;
                (double peakDown, double peakUp) = UpdatePeak(row.Pid.ToString(CultureInfo.InvariantCulture), down, up);

                return new RateSnapshot(
                    row.Pid,
                    row.ProcessName,
                    1,
                    down,
                    up,
                    row.TotalReceived / unit.Divisor,
                    row.TotalSent / unit.Divisor,
                    peakDown,
                    peakUp);
            })
            .ToList();
    }

    private static string FormatSnapshotLine(
        RateSnapshot row,
        bool showTotals,
        bool showPeaks)
    {
        string process = GroupByName
            ? $"{Shorten(row.ProcessName, 28),-28} {row.Count,3}"
            : $"{Shorten(row.ProcessName, 30),-30}";

        string line = $"{(GroupByName ? "(group)" : row.Pid.ToString(CultureInfo.InvariantCulture)),7} " +
            $"{process} " +
            $"{row.Download,14:N1} " +
            $"{row.Upload,14:N1} " +
            $"{row.Download + row.Upload,12:N1}";

        if (showTotals)
            line += $"   {row.TotalDownload,10:N2}  {row.TotalUpload,10:N2}";

        if (showPeaks)
            line += $"  {row.PeakDownload,10:N1}  {row.PeakUpload,10:N1}";

        return line;
    }

    private static (double down, double up) UpdatePeak(string key, double down, double up)
    {
        if (!PeaksByKey.TryGetValue(key, out var current))
            current = (0, 0);

        current = (Math.Max(current.Down, down), Math.Max(current.Up, up));
        PeaksByKey[key] = current;
        return current;
    }

    private static bool HandleKeyboardInput()
    {
        if (Console.IsInputRedirected)
            return false;

        bool changed = false;

        while (Console.KeyAvailable)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            // While editing the name filter, all printable keys belong to the
            // filter text, so handle that mode before any shortcut keys.
            if (IsEditingNameFilter)
            {
                changed |= HandleNameFilterInput(key);
                continue;
            }

            if (key.Key == ConsoleKey.E &&
                ((key.Modifiers & ConsoleModifiers.Control) != 0 ||
                    key.Modifiers == 0))
            {
                ExcludeZeroTotal = !ExcludeZeroTotal;
                changed = true;
                continue;
            }

            if (key.Key == ConsoleKey.F3)
            {
                changed |= ClearNameFilter();
                continue;
            }

            if (key.Key == ConsoleKey.F &&
                ((key.Modifiers & ConsoleModifiers.Control) != 0 ||
                    key.Modifiers == 0))
            {
                PendingNameFilter = ActiveNameFilter;
                IsEditingNameFilter = true;
                IsSelectingSortColumn = false;
                changed = true;
                continue;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                changed |= ChangeRefreshInterval(-1);
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                changed |= ChangeRefreshInterval(1);
                continue;
            }

            if (key.Key == ConsoleKey.U &&
                ((key.Modifiers & ConsoleModifiers.Control) != 0 ||
                    key.Modifiers == 0))
            {
                UnitIndex = (UnitIndex + 1) % UnitOptions.Length;
                changed = true;
                continue;
            }

            if (key.Key == ConsoleKey.X &&
                ((key.Modifiers & ConsoleModifiers.Control) != 0 ||
                    key.Modifiers == 0))
            {
                ExportCurrentCsv();
                changed = true;
                continue;
            }

            if (key.Key == ConsoleKey.G &&
                ((key.Modifiers & ConsoleModifiers.Control) != 0 ||
                    key.Modifiers == 0))
            {
                GroupByName = !GroupByName;
                PeaksByKey.Clear();
                changed = true;
                continue;
            }

            if (key.Key == ConsoleKey.S &&
                ((key.Modifiers & ConsoleModifiers.Control) != 0 ||
                    key.Modifiers == 0))
            {
                PendingSortColumn = ActiveSortColumn;
                IsSelectingSortColumn = true;
                changed = true;
                continue;
            }

            if (!IsSelectingSortColumn)
                continue;

            switch (key.Key)
            {
                case ConsoleKey.LeftArrow:
                    PendingSortColumn = MoveSortColumn(-1);
                    changed = true;
                    break;
                case ConsoleKey.RightArrow:
                    PendingSortColumn = MoveSortColumn(1);
                    changed = true;
                    break;
                case ConsoleKey.Enter:
                    ActiveSortColumn = PendingSortColumn;
                    IsSelectingSortColumn = false;
                    changed = true;
                    break;
                case ConsoleKey.Escape:
                    IsSelectingSortColumn = false;
                    changed = true;
                    break;
            }
        }

        return changed;
    }

    private static bool HandleNameFilterInput(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.F3 ||
            (key.Key == ConsoleKey.F &&
                (key.Modifiers & ConsoleModifiers.Control) != 0))
        {
            return ClearNameFilter();
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                ActiveNameFilter = PendingNameFilter.Trim();
                PendingNameFilter = ActiveNameFilter;
                IsEditingNameFilter = false;
                return true;
            case ConsoleKey.Escape:
                PendingNameFilter = ActiveNameFilter;
                IsEditingNameFilter = false;
                return true;
            case ConsoleKey.Backspace:
                if (PendingNameFilter.Length == 0)
                    return false;

                PendingNameFilter = PendingNameFilter[..^1];
                return true;
        }

        if (!char.IsControl(key.KeyChar))
        {
            PendingNameFilter += key.KeyChar;
            return true;
        }

        return false;
    }

    private static bool ClearNameFilter()
    {
        bool changed = ActiveNameFilter.Length > 0 ||
            PendingNameFilter.Length > 0 ||
            IsEditingNameFilter;

        ActiveNameFilter = string.Empty;
        PendingNameFilter = string.Empty;
        IsEditingNameFilter = false;

        return changed;
    }

    private static bool ChangeRefreshInterval(int offset)
    {
        int newIndex = Math.Clamp(
            RefreshIntervalIndex + offset,
            0,
            RefreshIntervals.Length - 1);

        if (newIndex == RefreshIntervalIndex)
            return false;

        RefreshIntervalIndex = newIndex;
        return true;
    }

    private static TrafficSortColumn MoveSortColumn(int offset)
    {
        int currentIndex = Array.IndexOf(SortColumns, PendingSortColumn);
        int nextIndex = (currentIndex + offset + SortColumns.Length) % SortColumns.Length;
        return SortColumns[nextIndex];
    }

    private static IReadOnlyList<RenderSegment> HeaderLine(bool showTotals, bool showPeaks)
    {
        string unit = CurrentUnit.Label;
        var segments = new List<RenderSegment>
        {
            new($"{"PID",7}", IsHighlightedHeader(TrafficSortColumn.Pid)),
            new(" "),
            new(GroupByName ? $"{"Process",-28} {"N",-3}" : $"{"Process",-30}", IsHighlightedHeader(TrafficSortColumn.ProcessName)),
            new(" "),
            new($"{$"Download {unit}",14}", IsHighlightedHeader(TrafficSortColumn.Received)),
            new(" "),
            new($"{$"Upload {unit}",14}", IsHighlightedHeader(TrafficSortColumn.Sent)),
            new(" "),
            new($"{$"Total {unit}",12}", IsHighlightedHeader(TrafficSortColumn.Total))
        };

        if (showTotals)
        {
            segments.Add(new("   "));
            segments.Add(new($"{"Session DL",10}", IsHighlightedHeader(TrafficSortColumn.TotalReceived)));
            segments.Add(new("  "));
            segments.Add(new($"{"Session UL",10}", IsHighlightedHeader(TrafficSortColumn.TotalSent)));
        }

        if (showPeaks)
        {
            segments.Add(new("  "));
            segments.Add(new($"{"Peak DL",10}"));
            segments.Add(new("  "));
            segments.Add(new($"{"Peak UL",10}"));
        }

        return segments;
    }

    private static bool IsHighlightedHeader(TrafficSortColumn column)
    {
        return IsSelectingSortColumn && PendingSortColumn == column;
    }

    private static IReadOnlyList<RenderSegment> Line(string text)
    {
        return [new RenderSegment(text)];
    }

    private static string GetSortColumnLabel(TrafficSortColumn column)
    {
        return column switch
        {
            TrafficSortColumn.Pid => "PID",
            TrafficSortColumn.ProcessName => "Process",
            TrafficSortColumn.Received => "Download",
            TrafficSortColumn.Sent => "Upload",
            TrafficSortColumn.TotalReceived => "Session DL",
            TrafficSortColumn.TotalSent => "Session UL",
            _ => "Total"
        };
    }

    private static string FormatRefreshInterval()
    {
        TimeSpan interval = RefreshIntervals[RefreshIntervalIndex];

        return interval.TotalMilliseconds < 1000
            ? $"{interval.TotalMilliseconds:N0} ms"
            : $"{interval.TotalSeconds:N1} s";
    }

    private static string GetStatusText()
    {
        if (IsEditingNameFilter)
            return "Search mode | Type process name | Enter apply | Esc cancel | F3 clear";

        if (IsSelectingSortColumn)
            return "Sort mode | Left/Right choose column | Enter apply | Esc cancel";

        string filterStatus = string.IsNullOrWhiteSpace(ActiveNameFilter)
            ? "Search: none"
            : $"Search: {ActiveNameFilter}";

        string status = $"{DateTime.Now:HH:mm:ss} | Sort: {GetSortColumnLabel(ActiveSortColumn)} | Refresh: {FormatRefreshInterval()} | Unit: {CurrentUnit.Label} | Zero: {(ExcludeZeroTotal ? "hidden" : "shown")} | Group: {(GroupByName ? "on" : "off")} | Sample: {(IsHoldingLastSample ? "last active" : "live")} | {filterStatus} | S sort | E zero | G group | F search | F3 clear | U unit | X export | Ctrl+C exit";
        return string.IsNullOrWhiteSpace(StatusMessage)
            ? status
            : $"{StatusMessage} | {status}";
    }

    private static IReadOnlyList<RenderSegment> StatusLine(string text)
    {
        return [new RenderSegment(text, Status: true)];
    }

    private static void ExportCurrentCsv()
    {
        string fileName = $"QNetwork_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        var sb = new StringBuilder();
        string unit = CurrentUnit.Label;
        sb.AppendLine($"PID,Process,Count,\"Download ({unit})\",\"Upload ({unit})\",\"Total ({unit})\",\"Session DL\",\"Session UL\",\"Peak DL ({unit})\",\"Peak UL ({unit})\"");

        // "F2" instead of "N2": group separators would break the CSV columns.
        static string Csv(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

        foreach (RateSnapshot row in BuildSnapshots(CurrentRows))
        {
            sb.Append(row.Pid).Append(',');
            sb.Append('"').Append(row.ProcessName.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"').Append(',');
            sb.Append(row.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Csv(row.Download)).Append(',');
            sb.Append(Csv(row.Upload)).Append(',');
            sb.Append(Csv(row.Download + row.Upload)).Append(',');
            sb.Append(Csv(row.TotalDownload)).Append(',');
            sb.Append(Csv(row.TotalUpload)).Append(',');
            sb.Append(Csv(row.PeakDownload)).Append(',');
            sb.AppendLine(Csv(row.PeakUpload));
        }

        File.WriteAllText(fileName, sb.ToString(), Encoding.UTF8);
        StatusMessage = $"Exported {fileName}";
    }

    private static bool TryRestartElevated(IEnumerable<string> args)
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return false;

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            string[] commandLineArgs = Environment.GetCommandLineArgs();
            IEnumerable<string> relaunchArgs = args;
            if (Path.GetFileNameWithoutExtension(executablePath)
                    .Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                commandLineArgs.Length > 0 &&
                commandLineArgs[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                relaunchArgs = [commandLineArgs[0], .. args];
            }

            startInfo.Arguments = string.Join(" ", relaunchArgs.Select(QuoteArgument));
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteArgument(string argument)
    {
        return "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static void RenderLines(IReadOnlyList<IReadOnlyList<RenderSegment>> lines)
    {
        int width = Console.IsOutputRedirected
            ? 160
            : Math.Max(1, Console.BufferWidth - 1);

        int lineCount = Math.Max(lines.Count, LastRenderedLineCount);

        if (!Console.IsOutputRedirected)
            Console.SetCursorPosition(0, 0);

        if (Console.IsOutputRedirected)
        {
            var output = new StringBuilder();
            for (int i = 0; i < lineCount; i++)
            {
                string line = i < lines.Count
                    ? string.Concat(lines[i].Select(segment => segment.Text))
                    : string.Empty;

                if (line.Length > width)
                    line = line[..width];

                output.Append(line.PadRight(width));

                if (i < lineCount - 1)
                    output.AppendLine();
            }

            Console.Write(output);
        }
        else
        {
            WriteLinesWithHighlights(lines, lineCount, width);
        }

        LastRenderedLineCount = lines.Count;
    }

    private static void WriteLinesWithHighlights(
        IReadOnlyList<IReadOnlyList<RenderSegment>> lines,
        int lineCount,
        int width)
    {
        ConsoleColor originalForeground = Console.ForegroundColor;
        ConsoleColor originalBackground = Console.BackgroundColor;

        try
        {
            for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
            {
                int written = 0;
                IReadOnlyList<RenderSegment> segments = lineIndex < lines.Count
                    ? lines[lineIndex]
                    : [];

                foreach (RenderSegment segment in segments)
                {
                    if (written >= width)
                        break;

                    string text = segment.Text;

                    if (written + text.Length > width)
                        text = text[..(width - written)];

                    if (segment.Status)
                    {
                        Console.ForegroundColor = ConsoleColor.Black;
                        Console.BackgroundColor = ConsoleColor.Gray;
                    }
                    else if (segment.Highlight)
                    {
                        Console.ForegroundColor = ConsoleColor.Black;
                        Console.BackgroundColor = ConsoleColor.White;
                    }
                    else
                    {
                        Console.ForegroundColor = originalForeground;
                        Console.BackgroundColor = originalBackground;
                    }

                    Console.Write(text);
                    written += text.Length;
                }

                Console.ForegroundColor = originalForeground;
                Console.BackgroundColor = originalBackground;
                Console.Write(new string(' ', width - written));

                if (lineIndex < lineCount - 1)
                    Console.WriteLine();
            }
        }
        finally
        {
            Console.ForegroundColor = originalForeground;
            Console.BackgroundColor = originalBackground;
        }
    }

    private static void SafeSetCursorVisible(bool visible)
    {
        if (Console.IsOutputRedirected)
            return;

        try
        {
            Console.CursorVisible = visible;
        }
        catch (IOException)
        {
        }
    }

    private static string Shorten(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
            return value;

        return value[..(maximumLength - 3)] + "...";
    }
}
