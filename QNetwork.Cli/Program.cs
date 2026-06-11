using System.Diagnostics;
using System.Text;

using QNetwork.Core;

internal static class Program
{
    private sealed record RenderSegment(
        string Text,
        bool Highlight = false,
        bool Status = false);

    private static readonly TrafficSortColumn[] SortColumns =
    [
        TrafficSortColumn.Pid,
        TrafficSortColumn.ProcessName,
        TrafficSortColumn.Received,
        TrafficSortColumn.Sent,
        TrafficSortColumn.Total
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

    private static TrafficSortColumn ActiveSortColumn = TrafficSortColumn.Total;
    private static TrafficSortColumn PendingSortColumn = TrafficSortColumn.Total;
    private static int RefreshIntervalIndex = 9;
    private static bool IsSelectingSortColumn;
    private static bool IsEditingNameFilter;
    private static bool ExcludeZeroTotal = true;
    private static bool IsHoldingLastSample;
    private static string ActiveNameFilter = string.Empty;
    private static string PendingNameFilter = string.Empty;
    private static int LastRenderedLineCount;
    private static double LastSampleSeconds = 1.0;

    private static async Task<int> Main()
    {
        if (!NetworkTrafficMonitor.IsSupportedPlatform)
        {
            await Console.Error.WriteLineAsync("Dieses Programm funktioniert nur unter Windows.");
            return 1;
        }

        if (!NetworkTrafficMonitor.IsElevated)
        {
            await Console.Error.WriteLineAsync("Das Programm muss als Administrator gestartet werden.");
            return 1;
        }

        await using var monitor = new NetworkTrafficMonitor();
        using var cancellation = new CancellationTokenSource();

        try
        {
            monitor.Start();
        }
        catch (InsufficientEtwResourcesException)
        {
            await Console.Error.WriteLineAsync(
                "Windows hat keine freien ETW-Logger-Ressourcen mehr.");
            await Console.Error.WriteLineAsync(
                "Schliesse andere Tracing-Tools oder starte Windows neu, falls alte ETW-Sessions haengen.");
            await Console.Error.WriteLineAsync(
                "Dieses Programm raeumt eigene alte Sessions beim Start automatisch auf.");
            return 1;
        }

        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
            monitor.Stop();
        };

        SafeSetCursorVisible(false);
        var currentRows = new List<TrafficRow>();
        var sampleTimer = Stopwatch.StartNew();
        RenderCurrentTraffic(currentRows);

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
                            currentRows,
                            ExcludeZeroTotal))
                    {
                        LastSampleSeconds = sampleSeconds;
                        currentRows = sampledRows;
                        IsHoldingLastSample = false;
                    }
                    else
                    {
                        IsHoldingLastSample = true;
                    }

                    RenderCurrentTraffic(currentRows);
                }
                else if (inputChanged)
                {
                    RenderCurrentTraffic(currentRows);
                }

                await Task.Delay(25, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit through Ctrl+C.
        }
        finally
        {
            SafeSetCursorVisible(true);
            await monitor.StopAsync();
        }

        return 0;
    }

    private static void RenderCurrentTraffic(IReadOnlyCollection<TrafficRow> rows)
    {
        List<TrafficRow> sortedRows = TrafficRows.Sort(
                TrafficRows.Filter(rows, ExcludeZeroTotal, ActiveNameFilter),
                ActiveSortColumn)
            .Take(20)
            .ToList();

        var lines = new List<IReadOnlyList<RenderSegment>>
        {
            Line("QNetwork Monitor"),
            Line(string.Empty),
            HeaderLine(),
            Line(new string('-', 88))
        };

        foreach (TrafficRow row in sortedRows)
        {
            double received = row.Received / 1024.0 / LastSampleSeconds;
            double sent = row.Sent / 1024.0 / LastSampleSeconds;

            lines.Add(Line(
                $"{row.Pid,7} " +
                $"{Shorten(row.ProcessName, 30),-30} " +
                $"{received,16:N1} " +
                $"{sent,16:N1} " +
                $"{received + sent,14:N1}"));
        }

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

    private static bool HandleKeyboardInput()
    {
        if (Console.IsInputRedirected)
            return false;

        bool changed = false;

        while (Console.KeyAvailable)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.E &&
                ((key.Modifiers & ConsoleModifiers.Control) != 0 ||
                    key.Modifiers == 0))
            {
                ExcludeZeroTotal = !ExcludeZeroTotal;
                changed = true;
                continue;
            }

            if (IsEditingNameFilter)
            {
                changed |= HandleNameFilterInput(key);
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

    private static IReadOnlyList<RenderSegment> HeaderLine()
    {
        return
        [
            new RenderSegment($"{"PID",7}", IsHighlightedHeader(TrafficSortColumn.Pid)),
            new RenderSegment(" "),
            new RenderSegment($"{"Programm",-30}", IsHighlightedHeader(TrafficSortColumn.ProcessName)),
            new RenderSegment(" "),
            new RenderSegment($"{"Download KiB/s",16}", IsHighlightedHeader(TrafficSortColumn.Received)),
            new RenderSegment(" "),
            new RenderSegment($"{"Upload KiB/s",16}", IsHighlightedHeader(TrafficSortColumn.Sent)),
            new RenderSegment(" "),
            new RenderSegment($"{"Total KiB/s",14}", IsHighlightedHeader(TrafficSortColumn.Total))
        ];
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
            TrafficSortColumn.ProcessName => "Programm",
            TrafficSortColumn.Received => "Download",
            TrafficSortColumn.Sent => "Upload",
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

        return $"{DateTime.Now:HH:mm:ss} | Sort: {GetSortColumnLabel(ActiveSortColumn)} | Refresh: {FormatRefreshInterval()} | Zero: {(ExcludeZeroTotal ? "hidden" : "shown")} | Sample: {(IsHoldingLastSample ? "last active" : "live")} | {filterStatus} | S sort | E zero | F search | F3 clear | Up/Down refresh | Ctrl+C exit";
    }

    private static IReadOnlyList<RenderSegment> StatusLine(string text)
    {
        return [new RenderSegment(text, Status: true)];
    }

    private static void RenderLines(IReadOnlyList<IReadOnlyList<RenderSegment>> lines)
    {
        int width = Console.IsOutputRedirected
            ? 120
            : Math.Max(1, Console.BufferWidth - 1);

        int lineCount = Math.Max(lines.Count, LastRenderedLineCount);
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

        if (!Console.IsOutputRedirected)
            Console.SetCursorPosition(0, 0);

        if (Console.IsOutputRedirected)
        {
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
