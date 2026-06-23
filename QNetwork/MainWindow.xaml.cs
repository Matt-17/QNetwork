using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

using QNetwork.Core;

using Forms = System.Windows.Forms;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using WpfApplication = System.Windows.Application;

namespace QNetwork;

public partial class MainWindow : Window
{
    private sealed record UnitOption(string Label, double Divisor);

    private sealed record KeyRate(string Key, string ProcessName, double DownloadBps, double UploadBps)
    {
        public double TotalBps => DownloadBps + UploadBps;
    }

    private sealed class AlertState
    {
        public DateTime? FirstAbove { get; set; }
        public DateTime LastNotification { get; set; } = DateTime.MinValue;
    }

    public sealed class TrafficRowView : INotifyPropertyChanged
    {
        private string processName = string.Empty;
        private double downloadPerSecond;
        private double uploadPerSecond;
        private double totalDownload;
        private double totalUpload;
        private double peakDownload;
        private double peakUpload;
        private int count;
        private string? executablePath;
        private BitmapSource? icon;

        public event PropertyChangedEventHandler? PropertyChanged;

        public required string Key { get; init; }
        public required int Pid { get; init; }

        public string ProcessName
        {
            get => processName;
            set => SetField(ref processName, value);
        }

        public double DownloadPerSecond
        {
            get => downloadPerSecond;
            set
            {
                if (SetField(ref downloadPerSecond, value))
                    OnPropertyChanged(nameof(TotalPerSecond));
            }
        }

        public double UploadPerSecond
        {
            get => uploadPerSecond;
            set
            {
                if (SetField(ref uploadPerSecond, value))
                    OnPropertyChanged(nameof(TotalPerSecond));
            }
        }

        public double TotalPerSecond => downloadPerSecond + uploadPerSecond;

        public double TotalDownload
        {
            get => totalDownload;
            set => SetField(ref totalDownload, value);
        }

        public double TotalUpload
        {
            get => totalUpload;
            set => SetField(ref totalUpload, value);
        }

        public double PeakDownload
        {
            get => peakDownload;
            set => SetField(ref peakDownload, value);
        }

        public double PeakUpload
        {
            get => peakUpload;
            set => SetField(ref peakUpload, value);
        }

        public int Count
        {
            get => count;
            set => SetField(ref count, value);
        }

        public string? ExecutablePath
        {
            get => executablePath;
            set => SetField(ref executablePath, value);
        }

        public BitmapSource? Icon
        {
            get => icon;
            set => SetField(ref icon, value);
        }

        private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static readonly UnitOption[] UnitOptions =
    [
        new("B/s", 1.0),
        new("KiB/s", 1024.0),
        new("MiB/s", 1024.0 * 1024.0)
    ];

    private static readonly string[] ThresholdUnits = ["KiB/s", "MiB/s", "GiB/s"];

    private const int HistoryCapacity = 60;
    private const double ChartAreaOpacity = 0.4;
    private const double ChartLineThickness = 1.5;
    private const double ChartLineBackingThickness = 3.5;
    private const double DefaultDetailsColWidth = 260;
    // Time constant (seconds) for per-second rate smoothing; larger is smoother.
    // The per-sample EMA weight is derived from this and the actual elapsed time,
    // so the smoothing stays consistent even if a tick is slightly late.
    private const double RateSmoothingSeconds = 3.0;
    private static readonly Color ChartDownloadColor = Colors.CornflowerBlue;
    private static readonly Color ChartUploadColor = Color.FromRgb(0xDC, 0x26, 0x26);
    private static readonly Color ChartLineBackingColor = Color.FromArgb(210, 0xFF, 0xFF, 0xFF);
    private static readonly TimeSpan ConnectionRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AlertHoldDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(1);

    private readonly ObservableCollection<TrafficRowView> rows = [];
    private readonly Dictionary<string, TrafficRowView> rowViewsByKey = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ConnectionRow> connections = [];
    private readonly DispatcherTimer timer = new();
    private readonly NetworkTrafficMonitor monitor = new();
    private readonly ICollectionView rowsView;
    private readonly DateTime startedAt = DateTime.Now;
    private readonly object lifecycleLock = new();

    // Peaks and history are stored in bytes/s so they survive unit changes.
    private readonly Dictionary<string, (double PeakDown, double PeakUp)> peaksByKey = new(
        StringComparer.OrdinalIgnoreCase);
    // Smoothed per-second rates (bytes/s) per key so the display eases between
    // samples; updated once per sample and read again when rendering rows.
    private readonly Dictionary<string, (double Down, double Up)> smoothedRatesByKey = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly List<TrafficSample> historyAll = new(HistoryCapacity + 1);
    private readonly Dictionary<string, List<TrafficSample>> historyByKey = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AlertState> alertStates = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string?> exePathByPid = [];
    private readonly Dictionary<int, ProcessInfo> processInfoByPid = [];
    private readonly QNetworkSettings settings;

    private Forms.NotifyIcon? trayIcon;
    private Forms.ToolStripMenuItem? trayPauseMenuItem;
    private Forms.ToolStripMenuItem? traySummaryMenuItem;
    private List<TrafficRow> currentRows = [];
    private List<KeyRate> lastRates = [];
    private double totalDownloadBps;
    private double totalUploadBps;
    private DateTime lastSampleAt = DateTime.Now;
    private DateTime lastConnectionRefresh = DateTime.MinValue;
    private string sortMember = nameof(TrafficRowView.TotalPerSecond);
    private ListSortDirection sortDirection = ListSortDirection.Descending;
    private double lastSampleSeconds = 1.0;
    private double detailsColWidth = DefaultDetailsColWidth;
    private UnitOption currentUnit = UnitOptions[1];
    private string? selectedAdapterName;
    private double alertThresholdBytesPerSecond;
    private bool alertFired;
    private bool isInitialized;
    private bool isPaused;
    private bool isStarted;
    private bool minimizeToTray;
    private string? selectedProcessKey;

    public MainWindow()
    {
        settings = QNetworkSettings.Load();
        ApplySettingsBeforeInit();

        Rows = rows;
        rowsView = CollectionViewSource.GetDefaultView(rows);
        InitializeComponent();
        DataContext = this;
        ApplySort();

        UnitCombo.ItemsSource = UnitOptions;
        UnitCombo.DisplayMemberPath = nameof(UnitOption.Label);
        UnitCombo.SelectedIndex = 1;

        ThresholdUnitCombo.ItemsSource = ThresholdUnits;
        ThresholdUnitCombo.SelectedIndex = 0;

        ConnectionsGrid.ItemsSource = connections;

        PopulateAdapterCombo();

        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += Timer_Tick;

        InitTrayIcon();
        ApplySettingsAfterInit();

        isInitialized = true;
        monitor.SetLocalAddressFilter(GetAdapterAddresses(selectedAdapterName));
        UpdateColumnHeaders();
    }

    public ObservableCollection<TrafficRowView> Rows { get; }

    private void ApplySettingsBeforeInit()
    {
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
    }

    private void ApplySettingsAfterInit()
    {
        HideIdleCheckBox.IsChecked = settings.HideIdle;
        HideIdleMenuItem.IsChecked = settings.HideIdle;
        GroupByNameMenuItem.IsChecked = settings.GroupByProcessName;

        if (!string.IsNullOrEmpty(settings.SearchText))
            SearchBox.Text = settings.SearchText;

        sortMember = settings.SortMember;
        sortDirection = settings.SortDirection == "Ascending"
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;
        ApplySort();

        ShowTotalDownloadMenuItem.IsChecked = settings.ShowTotalDownload;
        ShowTotalUploadMenuItem.IsChecked = settings.ShowTotalUpload;
        ShowPeakDownloadMenuItem.IsChecked = settings.ShowPeakDownload;
        ShowPeakUploadMenuItem.IsChecked = settings.ShowPeakUpload;
        ShowIconsMenuItem.IsChecked = settings.ShowIcons;
        MinimizeToTrayMenuItem.IsChecked = settings.MinimizeToTray;
        minimizeToTray = settings.MinimizeToTray;

        UpdateOptionalColumnVisibility();
        UpdateGroupingColumnVisibility();

        ShowChartMenuItem.IsChecked = settings.ShowChart;
        ApplyChartVisibility();

        ShowDetailsPanelCheckBox.IsChecked = settings.ShowDetailsPanel;
        ApplyDetailsPanelVisibility();

        // Threshold: the saved value is in the saved unit, exactly as entered.
        int thresholdUnitIndex = Array.IndexOf(ThresholdUnits, settings.AlertThresholdUnit);
        ThresholdUnitCombo.SelectedIndex = thresholdUnitIndex >= 0 ? thresholdUnitIndex : 1;
        ThresholdBox.Text = settings.AlertThresholdValue > 0
            ? settings.AlertThresholdValue.ToString("0.###", CultureInfo.CurrentCulture)
            : "0";
        UpdateAlertThreshold();

        // Adapter
        if (!string.IsNullOrEmpty(settings.SelectedAdapterName))
        {
            for (int i = 0; i < AdapterCombo.Items.Count; i++)
            {
                if (AdapterCombo.Items[i] is string name &&
                    name == settings.SelectedAdapterName)
                {
                    AdapterCombo.SelectedIndex = i;
                    selectedAdapterName = name;
                    break;
                }
            }
        }

        // Unit: the SelectionChanged handler is suppressed during init, so apply
        // the unit directly as well.
        int unitIndex = Array.FindIndex(UnitOptions, u => u.Label == settings.Unit);
        UnitCombo.SelectedIndex = Math.Max(0, unitIndex);
        currentUnit = UnitOptions[Math.Max(0, unitIndex)];
        UpdateUnitMenuChecks();
    }

    private void SaveSettings()
    {
        settings.WindowWidth = ActualWidth;
        settings.WindowHeight = ActualHeight;
        settings.WindowLeft = Left;
        settings.WindowTop = Top;
        settings.HideIdle = HideIdleCheckBox.IsChecked == true;
        settings.GroupByProcessName = GroupByNameMenuItem.IsChecked;
        settings.SearchText = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text;
        settings.SortMember = sortMember;
        settings.SortDirection = sortDirection == ListSortDirection.Ascending ? "Ascending" : "Descending";
        settings.Unit = currentUnit.Label;
        settings.ShowTotalDownload = ShowTotalDownloadMenuItem.IsChecked;
        settings.ShowTotalUpload = ShowTotalUploadMenuItem.IsChecked;
        settings.ShowPeakDownload = ShowPeakDownloadMenuItem.IsChecked;
        settings.ShowPeakUpload = ShowPeakUploadMenuItem.IsChecked;
        settings.ShowIcons = ShowIconsMenuItem.IsChecked;
        settings.ShowChart = ShowChartMenuItem.IsChecked;
        settings.ShowDetailsPanel = ShowDetailsPanelCheckBox.IsChecked == true;
        settings.MinimizeToTray = minimizeToTray;
        settings.AlertThresholdValue = alertThresholdBytesPerSecond / GetThresholdDivisor();
        settings.AlertThresholdUnit = ThresholdUnitCombo.SelectedItem as string ?? "MiB/s";
        settings.SelectedAdapterName = selectedAdapterName;
        settings.Save();
    }

    private void InitTrayIcon()
    {
        trayIcon = new Forms.NotifyIcon
        {
            Text = "QNetwork",
            Visible = false
        };

        try
        {
            string exePath = Environment.ProcessPath ?? string.Empty;
            if (File.Exists(exePath))
                trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch { }

        var contextMenu = new Forms.ContextMenuStrip();
        traySummaryMenuItem = new Forms.ToolStripMenuItem("No traffic sample yet")
        {
            Enabled = false
        };
        trayPauseMenuItem = new Forms.ToolStripMenuItem("Pause", null, (_, _) =>
        {
            Dispatcher.Invoke(() => SetPaused(!isPaused));
        });
        contextMenu.Items.Add(traySummaryMenuItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add("Open QNetwork", null, (_, _) => ShowFromTray());
        contextMenu.Items.Add(trayPauseMenuItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) =>
        {
            minimizeToTray = false;
            trayIcon.Visible = false;
            WpfApplication.Current.Shutdown();
        });

        trayIcon.ContextMenuStrip = contextMenu;
        trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void PopulateAdapterCombo()
    {
        AdapterCombo.Items.Clear();
        AdapterCombo.Items.Add("All adapters");

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                    continue;
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                AdapterCombo.Items.Add(nic.Name);
            }
        }
        catch { }

        AdapterCombo.SelectedIndex = 0;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!NetworkTrafficMonitor.IsElevated)
        {
            StatusText.Text = "Administrator rights are required for ETW kernel tracing.";

            MessageBoxResult result = MessageBox.Show(
                this,
                "QNetwork needs administrator rights to monitor per-process network traffic.\n\nRestart as administrator?",
                "Administrator rights required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes && TryRestartElevated())
            {
                minimizeToTray = false;
                Close();
                return;
            }

            PauseButton.IsEnabled = false;
            PauseMenuItem.IsEnabled = false;
            return;
        }

        try
        {
            monitor.Start();
            isStarted = true;
            timer.Start();
            PauseMenuItem.IsChecked = false;
            StatusText.Text = "Monitoring network traffic.";
        }
        catch (InsufficientEtwResourcesException)
        {
            StatusText.Text =
                "Windows has no free ETW logger resources. Close tracing tools or restart Windows.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not start monitor: {ex.Message}";
        }
    }

    private static bool TryRestartElevated()
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

            if (System.IO.Path.GetFileNameWithoutExtension(executablePath)
                    .Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                commandLineArgs.Length > 0 &&
                commandLineArgs[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Arguments = string.Join(
                    " ",
                    commandLineArgs.Select(QuoteArgument));
            }

            Process.Start(startInfo);

            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string QuoteArgument(string argument)
    {
        return "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (minimizeToTray && trayIcon is not null)
        {
            e.Cancel = true;
            Hide();
            trayIcon.Visible = true;
            trayIcon.ShowBalloonTip(
                2000, "QNetwork", "Running in the background.", Forms.ToolTipIcon.Info);
            return;
        }

        SaveSettings();
        timer.Stop();
        trayIcon?.Dispose();

        lock (lifecycleLock)
        {
            if (!isStarted)
                return;
            isStarted = false;
        }

        monitor.Dispose();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (minimizeToTray && WindowState == WindowState.Minimized && trayIcon is not null)
        {
            Hide();
            trayIcon.Visible = true;
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        UpdateStatusOnly();

        if (isPaused)
        {
            MaybeRefreshConnections();
            return;
        }

        DateTime sampleAt = DateTime.Now;
        double sampleSeconds = Math.Max((sampleAt - lastSampleAt).TotalSeconds, 0.001);
        lastSampleAt = sampleAt;

        List<TrafficRow> sampledRows = monitor.ReadCurrentTraffic();
        bool hideIdle = HideIdleCheckBox.IsChecked == true;

        if (TrafficRows.ShouldReplaceDisplayedRows(sampledRows, currentRows, hideIdle))
        {
            lastSampleSeconds = sampleSeconds;
            currentRows = sampledRows;
            RecordSample(sampledRows, sampleSeconds);
            RenderRows();
            RedrawHistoryChart();
            CheckAlertThreshold();
            UpdateTrayInfo();
        }

        MaybeRefreshConnections();
    }

    /// <summary>
    /// Records a fresh traffic sample: per-key rates, peaks, and history,
    /// all in bytes/s and independent of filtering or the display unit.
    /// </summary>
    private void RecordSample(IReadOnlyCollection<TrafficRow> sampledRows, double sampleSeconds)
    {
        bool groupByName = GroupByNameMenuItem.IsChecked;
        var rawRates = new List<KeyRate>(sampledRows.Count);

        if (groupByName)
        {
            foreach (GroupedTrafficRow group in TrafficRows.GroupByProcessName(sampledRows))
            {
                rawRates.Add(new KeyRate(
                    group.ProcessName,
                    group.ProcessName,
                    group.Received / sampleSeconds,
                    group.Sent / sampleSeconds));
            }
        }
        else
        {
            foreach (TrafficRow row in sampledRows)
            {
                rawRates.Add(new KeyRate(
                    row.Pid.ToString(CultureInfo.InvariantCulture),
                    row.ProcessName,
                    row.Received / sampleSeconds,
                    row.Sent / sampleSeconds));
            }
        }

        DateTime now = DateTime.Now;
        double totalDown = 0, totalUp = 0;
        var rates = new List<KeyRate>(rawRates.Count);

        // EMA weight for this sample's elapsed time; same for every key this tick.
        double alpha = 1.0 - Math.Exp(-sampleSeconds / RateSmoothingSeconds);

        foreach (KeyRate raw in rawRates)
        {
            (double down, double up) = SmoothRate(raw.Key, raw.DownloadBps, raw.UploadBps, alpha);
            rates.Add(raw with { DownloadBps = down, UploadBps = up });

            totalDown += down;
            totalUp += up;
            UpdatePeak(raw.Key, down, up);
            AddToHistory(raw.Key, now, down, up);
        }

        totalDownloadBps = totalDown;
        totalUploadBps = totalUp;
        AddToAllHistory(now, totalDown, totalUp);
        lastRates = rates;
    }

    /// <summary>
    /// Projects the current sample into the grid using the active filter, grouping,
    /// and unit. Existing row views are updated in place so the DataGrid keeps its
    /// selection and scroll position.
    /// </summary>
    private void RenderRows()
    {
        if (!isInitialized)
            return;

        bool hideIdle = HideIdleCheckBox.IsChecked == true;
        string searchText = SearchBox.Text;
        UnitOption unit = currentUnit;
        bool groupByName = GroupByNameMenuItem.IsChecked;
        bool showIcons = ShowIconsMenuItem.IsChecked;
        double seconds = lastSampleSeconds;

        // Prefer the smoothed rate recorded for this key; fall back to the raw
        // sample rate when no smoothed value exists yet (e.g. the tick right after
        // a grouping toggle, before the next sample rebuilds the smoothed keys).
        (double Down, double Up) DisplayRate(string key, long received, long sent) =>
            smoothedRatesByKey.TryGetValue(key, out var rate)
                ? rate
                : (received / seconds, sent / seconds);

        IEnumerable<TrafficRow> filtered = TrafficRows.Filter(currentRows, hideIdle, searchText);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Upsert(
            string key, int pid, string name, double downBps, double upBps,
            long totalReceived, long totalSent, int count)
        {
            seenKeys.Add(key);
            (double peakDown, double peakUp) = peaksByKey.TryGetValue(key, out var peak)
                ? peak
                : (0.0, 0.0);

            if (!rowViewsByKey.TryGetValue(key, out TrafficRowView? view))
            {
                view = new TrafficRowView { Key = key, Pid = pid };
                rowViewsByKey[key] = view;
                rows.Add(view);
            }

            view.ProcessName = name;
            view.DownloadPerSecond = downBps / unit.Divisor;
            view.UploadPerSecond = upBps / unit.Divisor;
            view.TotalDownload = totalReceived / unit.Divisor;
            view.TotalUpload = totalSent / unit.Divisor;
            view.PeakDownload = peakDown / unit.Divisor;
            view.PeakUpload = peakUp / unit.Divisor;
            view.Count = count;

            if (showIcons && view.Icon is null && pid > 0)
            {
                string? exePath = GetExecutablePathCached(pid);
                view.ExecutablePath = exePath;
                view.Icon = ProcessIconCache.GetIcon(exePath);
            }
        }

        if (groupByName)
        {
            foreach (GroupedTrafficRow group in TrafficRows.GroupByProcessName(filtered))
            {
                (double down, double up) = DisplayRate(group.ProcessName, group.Received, group.Sent);
                Upsert(
                    group.ProcessName, 0, group.ProcessName,
                    down, up,
                    group.TotalReceived, group.TotalSent, group.Count);
            }
        }
        else
        {
            foreach (TrafficRow row in filtered)
            {
                string key = row.Pid.ToString(CultureInfo.InvariantCulture);
                (double down, double up) = DisplayRate(key, row.Received, row.Sent);
                Upsert(
                    key, row.Pid, row.ProcessName,
                    down, up,
                    row.TotalReceived, row.TotalSent, 1);
            }
        }

        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (seenKeys.Contains(rows[i].Key))
                continue;

            rowViewsByKey.Remove(rows[i].Key);
            rows.RemoveAt(i);
        }

        rowsView.Refresh();

        UpdateMetrics();

        if (ShowDetailsPanelCheckBox.IsChecked == true)
            UpdateDetailsForSelectedRow();
    }

    private void ClearTrafficState()
    {
        currentRows = [];
        lastRates = [];
        totalDownloadBps = 0;
        totalUploadBps = 0;
        rows.Clear();
        rowViewsByKey.Clear();
        peaksByKey.Clear();
        smoothedRatesByKey.Clear();
        historyAll.Clear();
        historyByKey.Clear();
        alertStates.Clear();
        selectedProcessKey = null;
    }

    /// <summary>
    /// Eases a key's per-second rates with an exponential moving average so the
    /// display doesn't jump between samples. <paramref name="alpha"/> is the blend
    /// weight for the newest sample (0..1; smaller is smoother). Updates the stored
    /// state and returns the smoothed rate.
    /// </summary>
    private (double Down, double Up) SmoothRate(
        string key, double downBps, double upBps, double alpha)
    {
        if (smoothedRatesByKey.TryGetValue(key, out var previous))
        {
            downBps = previous.Down + alpha * (downBps - previous.Down);
            upBps = previous.Up + alpha * (upBps - previous.Up);
        }

        var smoothed = (downBps, upBps);
        smoothedRatesByKey[key] = smoothed;
        return smoothed;
    }

    private void UpdatePeak(string key, double downBps, double upBps)
    {
        if (!peaksByKey.TryGetValue(key, out var current))
            current = (0, 0);

        peaksByKey[key] = (
            Math.Max(current.PeakDown, downBps),
            Math.Max(current.PeakUp, upBps));
    }

    private void AddToHistory(string key, DateTime timestamp, double downBps, double upBps)
    {
        if (!historyByKey.TryGetValue(key, out var list))
        {
            list = new List<TrafficSample>(HistoryCapacity + 1);
            historyByKey[key] = list;
        }

        list.Add(new TrafficSample(timestamp, downBps, upBps));
        if (list.Count > HistoryCapacity)
            list.RemoveAt(0);
    }

    private void AddToAllHistory(DateTime timestamp, double downBps, double upBps)
    {
        historyAll.Add(new TrafficSample(timestamp, downBps, upBps));
        if (historyAll.Count > HistoryCapacity)
            historyAll.RemoveAt(0);
    }

    private string? GetExecutablePathCached(int pid)
    {
        if (exePathByPid.TryGetValue(pid, out string? cached))
            return cached;

        string? path = TryGetExecutablePath(pid);
        exePathByPid[pid] = path;
        return path;
    }

    private ProcessInfo GetProcessInfoCached(int pid)
    {
        if (processInfoByPid.TryGetValue(pid, out ProcessInfo? cached))
            return cached;

        ProcessInfo info = ProcessInfoResolver.Resolve(pid);
        processInfoByPid[pid] = info;
        return info;
    }

    private static string? TryGetExecutablePath(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.MainModule?.FileName;
        }
        catch { return null; }
    }

    private void UpdateMetrics()
    {
        double divisor = currentUnit.Divisor;
        DownloadText.Text = $"{totalDownloadBps / divisor:N1} {currentUnit.Label}";
        UploadText.Text = $"{totalUploadBps / divisor:N1} {currentUnit.Label}";
        ProcessCountText.Text = rows.Count.ToString("N0");

        KeyRate? top = lastRates.MaxBy(rate => rate.TotalBps);
        TopProcessText.Text = top is null || top.TotalBps <= 0
            ? "none"
            : $"{top.ProcessName} {FormatBps(top.TotalBps)}";
    }

    private void UpdateStatusOnly()
    {
        if (!isInitialized)
            return;

        UptimeText.Text = (DateTime.Now - startedAt).ToString(@"hh\:mm\:ss");

        string filterText = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? "no search"
            : $"search '{SearchBox.Text}'";

        string adapterText = selectedAdapterName is null
            ? "all adapters"
            : selectedAdapterName;

        string pausedText = isPaused ? "PAUSED | " : string.Empty;

        StatusText.Text =
            $"{pausedText}{filterText} | unit {currentUnit.Label} | adapter {adapterText}";
    }

    private void CheckAlertThreshold()
    {
        if (alertThresholdBytesPerSecond <= 0)
        {
            AlertStatusText.Text = string.Empty;
            alertFired = false;
            alertStates.Clear();
            return;
        }

        DateTime now = DateTime.Now;
        KeyRate? highest = lastRates.MaxBy(rate => rate.TotalBps);

        if (highest is null)
        {
            AlertStatusText.Text = string.Empty;
            return;
        }

        if (highest.TotalBps >= alertThresholdBytesPerSecond)
        {
            if (!alertStates.TryGetValue(highest.Key, out AlertState? state))
            {
                state = new AlertState();
                alertStates[highest.Key] = state;
            }

            state.FirstAbove ??= now;

            if (now - state.FirstAbove.Value < AlertHoldDuration)
                return;

            AlertStatusText.Text =
                $"ALERT: {highest.ProcessName} {FormatBps(highest.TotalBps)} exceeds {FormatBps(alertThresholdBytesPerSecond)}";

            if (!alertFired || now - state.LastNotification >= AlertCooldown)
            {
                alertFired = true;
                state.LastNotification = now;
                trayIcon?.ShowBalloonTip(
                    3000,
                    "QNetwork Threshold Alert",
                    $"{highest.ProcessName} reached {FormatBps(highest.TotalBps)}",
                    Forms.ToolTipIcon.Warning);
            }
        }
        else
        {
            AlertStatusText.Text = string.Empty;
            alertFired = false;
            if (alertStates.TryGetValue(highest.Key, out AlertState? state))
                state.FirstAbove = null;
        }
    }

    private void UpdateTrayInfo()
    {
        if (trayIcon is null) return;
        string text = $"QNetwork\nDown: {FormatBps(totalDownloadBps)}\nUp: {FormatBps(totalUploadBps)}";
        // NotifyIcon.Text max 64 chars
        trayIcon.Text = text.Length > 63 ? text[..63] : text;
        if (traySummaryMenuItem is not null)
            traySummaryMenuItem.Text = $"Down {FormatBps(totalDownloadBps)} | Up {FormatBps(totalUploadBps)}";
    }

    private void RedrawHistoryChart()
    {
        if (ShowChartMenuItem.IsChecked != true)
            return;

        double w = HistoryCanvas.ActualWidth;
        double h = HistoryCanvas.ActualHeight;
        if (w < 2 || h < 2)
            return;

        HistoryCanvas.Children.Clear();

        List<TrafficSample> history =
            selectedProcessKey is not null && historyByKey.TryGetValue(selectedProcessKey, out var pHistory)
                ? pHistory
                : historyAll;

        if (history.Count < 2)
            return;

        double maxBps = Math.Max(1.0, history.Max(s => s.DownloadBytesPerSecond + s.UploadBytesPerSecond));
        double xStep = w / (HistoryCapacity - 1);
        double xOffset = w - (history.Count - 1) * xStep;

        List<Point> downPoints = history.Select((s, i) => new Point(
            xOffset + i * xStep,
            h - (s.DownloadBytesPerSecond / maxBps) * h)).ToList();

        List<Point> upPoints = history.Select((s, i) => new Point(
            xOffset + i * xStep,
            h - (s.UploadBytesPerSecond / maxBps) * h)).ToList();

        // Midline grid hint
        var midline = new Line
        {
            X1 = 0,
            X2 = w,
            Y1 = h / 2,
            Y2 = h / 2,
            Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
            StrokeThickness = 1,
            StrokeDashArray = [3, 3]
        };
        HistoryCanvas.Children.Add(midline);

        DrawArea(downPoints, h, ChartDownloadColor);
        DrawArea(upPoints, h, ChartUploadColor);
        DrawCurve(downPoints, new SolidColorBrush(ChartLineBackingColor), ChartLineBackingThickness);
        DrawCurve(upPoints, new SolidColorBrush(ChartLineBackingColor), ChartLineBackingThickness);
        DrawCurve(downPoints, new SolidColorBrush(ChartDownloadColor), ChartLineThickness);
        DrawCurve(upPoints, new SolidColorBrush(ChartUploadColor), ChartLineThickness);

        // Legend, top-right so it does not collide with the axis label
        AddChartLabel("▬ Download", ChartDownloadColor, w - 160, 4);
        AddChartLabel("▬ Upload", ChartUploadColor, w - 70, 4);

        // Y axis labels
        AddChartLabel(FormatBps(maxBps), Colors.Gray, 2, 0);
        AddChartLabel(FormatBps(maxBps / 2), Colors.Gray, 2, h / 2 - 12);
    }

    private void DrawArea(IReadOnlyList<Point> points, double height, Color fill)
    {
        if (points.Count < 2)
            return;

        var figure = new PathFigure { StartPoint = points[0] };
        foreach ((Point c1, Point c2, Point end) in ComputeBezierSegments(points))
            figure.Segments.Add(new BezierSegment(c1, c2, end, false));

        // Close the shape down to the baseline so the fill sits under the curve.
        figure.Segments.Add(new LineSegment(new Point(points[^1].X, height), false));
        figure.Segments.Add(new LineSegment(new Point(points[0].X, height), false));
        figure.IsClosed = true;

        var path = new System.Windows.Shapes.Path
        {
            Fill = new SolidColorBrush(fill),
            Opacity = ChartAreaOpacity,
            Data = new PathGeometry { Figures = { figure } }
        };
        HistoryCanvas.Children.Add(path);
    }

    private void DrawCurve(IReadOnlyList<Point> points, Brush stroke, double thickness)
    {
        if (points.Count < 2)
            return;

        var figure = new PathFigure { StartPoint = points[0], IsFilled = false };
        foreach ((Point c1, Point c2, Point end) in ComputeBezierSegments(points))
            figure.Segments.Add(new BezierSegment(c1, c2, end, true));

        var path = new System.Windows.Shapes.Path
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            Data = new PathGeometry { Figures = { figure } }
        };
        HistoryCanvas.Children.Add(path);
    }

    /// <summary>
    /// Converts a polyline into cubic Bézier segments using a Catmull-Rom spline,
    /// so the chart curves smoothly through every sample point. X stays uniformly
    /// spaced, so the curve never loops back on itself.
    /// </summary>
    private static List<(Point C1, Point C2, Point End)> ComputeBezierSegments(
        IReadOnlyList<Point> points)
    {
        var segments = new List<(Point, Point, Point)>(points.Count - 1);

        for (int i = 0; i < points.Count - 1; i++)
        {
            Point p0 = points[Math.Max(i - 1, 0)];
            Point p1 = points[i];
            Point p2 = points[i + 1];
            Point p3 = points[Math.Min(i + 2, points.Count - 1)];

            var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
            segments.Add((c1, c2, p2));
        }

        return segments;
    }

    private void AddChartLabel(string text, Color color, double x, double y)
    {
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = text,
            FontSize = 9,
            Foreground = new SolidColorBrush(color)
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        HistoryCanvas.Children.Add(tb);
    }

    private static string FormatBps(double bps)
    {
        if (bps >= 1024 * 1024) return $"{bps / (1024 * 1024):N1} MiB/s";
        if (bps >= 1024) return $"{bps / 1024:N1} KiB/s";
        return $"{bps:N0} B/s";
    }

    private void HistoryCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!isInitialized) return;
        RedrawHistoryChart();
    }

    private void ApplySort()
    {
        rowsView.SortDescriptions.Clear();
        rowsView.SortDescriptions.Add(new SortDescription(sortMember, sortDirection));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!isInitialized) return;
        RenderRows();
    }

    private void FilterControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized) return;
        HideIdleMenuItem.IsChecked = HideIdleCheckBox.IsChecked == true;
        RenderRows();
    }

    private void HideIdleMenuItem_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized) return;
        HideIdleCheckBox.IsChecked = HideIdleMenuItem.IsChecked;
        RenderRows();
    }

    private void GroupByNameMenuItem_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized) return;
        UpdateGroupingColumnVisibility();

        // Keys change between PID and process name, so the keyed state must reset.
        rows.Clear();
        rowViewsByKey.Clear();
        peaksByKey.Clear();
        smoothedRatesByKey.Clear();
        historyByKey.Clear();
        alertStates.Clear();
        selectedProcessKey = null;

        RenderRows();
    }

    private void UpdateGroupingColumnVisibility()
    {
        CountColumn.Visibility = GroupByNameMenuItem.IsChecked
            ? Visibility.Visible
            : Visibility.Collapsed;
        PidColumn.Visibility = GroupByNameMenuItem.IsChecked
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!isInitialized) return;
        if (UnitCombo.SelectedItem is not UnitOption option) return;
        currentUnit = option;
        UpdateUnitMenuChecks();
        UpdateColumnHeaders();
        RenderRows();
        UpdateStatusOnly();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        SetPaused(!isPaused);
    }

    private void PauseMenuItem_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized) return;
        SetPaused(PauseMenuItem.IsChecked);
    }

    private void SetPaused(bool paused)
    {
        isPaused = paused;
        PauseButton.Content = isPaused ? "Resume" : "Pause";
        if (trayPauseMenuItem is not null)
            trayPauseMenuItem.Text = isPaused ? "Resume" : "Pause";
        if (PauseMenuItem.IsChecked != isPaused)
            PauseMenuItem.IsChecked = isPaused;

        // Skip the paused gap in the next rate computation.
        if (!isPaused)
            lastSampleAt = DateTime.Now;

        UpdateStatusOnly();
    }

    private void ColumnVisibilityMenuItem_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized) return;
        UpdateOptionalColumnVisibility();
        RenderRows();
    }

    private void UpdateOptionalColumnVisibility()
    {
        TotalDownloadColumn.Visibility = ShowTotalDownloadMenuItem.IsChecked
            ? Visibility.Visible : Visibility.Collapsed;
        TotalUploadColumn.Visibility = ShowTotalUploadMenuItem.IsChecked
            ? Visibility.Visible : Visibility.Collapsed;
        PeakDownloadColumn.Visibility = ShowPeakDownloadMenuItem.IsChecked
            ? Visibility.Visible : Visibility.Collapsed;
        PeakUploadColumn.Visibility = ShowPeakUploadMenuItem.IsChecked
            ? Visibility.Visible : Visibility.Collapsed;
        IconColumn.Visibility = ShowIconsMenuItem.IsChecked
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDetailsPanelCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized) return;
        ApplyDetailsPanelVisibility();
    }

    private void ApplyDetailsPanelVisibility()
    {
        bool show = ShowDetailsPanelCheckBox.IsChecked == true;
        if (!show && DetailsCol.Width.Value > 0)
            detailsColWidth = DetailsCol.Width.Value;

        DetailsSplitterCol.Width = show ? new GridLength(4) : new GridLength(0);
        DetailsCol.Width = show ? new GridLength(detailsColWidth) : new GridLength(0);
        if (show)
            UpdateDetailsForSelectedRow();
    }

    private void TrafficGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!isInitialized) return;

        if (TrafficGrid.SelectedItem is TrafficRowView selected)
        {
            selectedProcessKey = selected.Key;
            ChartTitleText.Text = $"Traffic history: {selected.ProcessName} (last 60 s)";
        }
        else
        {
            selectedProcessKey = null;
            ChartTitleText.Text = "Traffic history (last 60 s)";
        }

        UpdateDetailsForSelectedRow();
        RedrawHistoryChart();

        // Let the next connections-tab refresh pick up the new selection.
        lastConnectionRefresh = DateTime.MinValue;
        MaybeRefreshConnections();
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!isInitialized) return;
        if (!ReferenceEquals(e.OriginalSource, MainTabControl)) return;
        MaybeRefreshConnections();
    }

    private void UpdateDetailsForSelectedRow()
    {
        if (TrafficGrid.SelectedItem is not TrafficRowView selected)
        {
            ClearDetails();
            return;
        }

        DetailName.Text = selected.ProcessName;
        DetailPid.Text = selected.Pid > 0 ? selected.Pid.ToString() : "(multiple)";
        DetailDownload.Text = $"{selected.DownloadPerSecond:N1} {currentUnit.Label}";
        DetailUpload.Text = $"{selected.UploadPerSecond:N1} {currentUnit.Label}";
        DetailTotalDownload.Text = $"{selected.TotalDownload:N2} {currentUnit.Label}";
        DetailTotalUpload.Text = $"{selected.TotalUpload:N2} {currentUnit.Label}";
        DetailPeakDownload.Text = $"{selected.PeakDownload:N1} {currentUnit.Label}";
        DetailPeakUpload.Text = $"{selected.PeakUpload:N1} {currentUnit.Label}";

        if (selected.Pid > 0)
        {
            ProcessInfo info = GetProcessInfoCached(selected.Pid);
            DetailPath.Text = info.ExecutablePath ?? "—";
            DetailStartTime.Text = info.StartTime.HasValue
                ? info.StartTime.Value.ToString("g")
                : "—";
            DetailCompany.Text = info.Company ?? "—";
            DetailDescription.Text = info.FileDescription ?? "—";
        }
        else
        {
            DetailPath.Text = "—";
            DetailStartTime.Text = "—";
            DetailCompany.Text = "—";
            DetailDescription.Text = "—";
        }
    }

    private void ClearDetails()
    {
        DetailName.Text = "—";
        DetailPid.Text = "—";
        DetailCompany.Text = "—";
        DetailDescription.Text = "—";
        DetailPath.Text = "—";
        DetailStartTime.Text = "—";
        DetailDownload.Text = "—";
        DetailUpload.Text = "—";
        DetailTotalDownload.Text = "—";
        DetailTotalUpload.Text = "—";
        DetailPeakDownload.Text = "—";
        DetailPeakUpload.Text = "—";
    }

    private void ShowChartMenuItem_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized) return;
        ApplyChartVisibility();
    }

    private void ApplyChartVisibility()
    {
        bool show = ShowChartMenuItem.IsChecked;
        ChartBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ChartRow.Height = show ? new GridLength(110) : new GridLength(0);
        if (show)
            RedrawHistoryChart();
        else
            HistoryCanvas.Children.Clear();
    }

    private void TrafficGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;

        if (e.Column.SortMemberPath == sortMember)
        {
            sortDirection = sortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            sortMember = e.Column.SortMemberPath;
            sortDirection = IsNumericSort(sortMember)
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }

        ApplySort();
        UpdateColumnHeaders();
    }

    private static bool IsNumericSort(string memberName) =>
        memberName is
            nameof(TrafficRowView.Pid) or
            nameof(TrafficRowView.DownloadPerSecond) or
            nameof(TrafficRowView.UploadPerSecond) or
            nameof(TrafficRowView.TotalPerSecond) or
            nameof(TrafficRowView.TotalDownload) or
            nameof(TrafficRowView.TotalUpload) or
            nameof(TrafficRowView.PeakDownload) or
            nameof(TrafficRowView.PeakUpload) or
            nameof(TrafficRowView.Count);

    private void UnitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender == BytesUnitMenuItem) UnitCombo.SelectedIndex = 0;
        else if (sender == KibUnitMenuItem) UnitCombo.SelectedIndex = 1;
        else if (sender == MibUnitMenuItem) UnitCombo.SelectedIndex = 2;
    }

    private void UpdateUnitMenuChecks()
    {
        BytesUnitMenuItem.IsChecked = UnitCombo.SelectedIndex == 0;
        KibUnitMenuItem.IsChecked = UnitCombo.SelectedIndex == 1;
        MibUnitMenuItem.IsChecked = UnitCombo.SelectedIndex == 2;
    }

    private void UpdateColumnHeaders()
    {
        string suffix = currentUnit.Label;

        string FormatHeader(string label, string memberName)
        {
            if (!string.Equals(sortMember, memberName, StringComparison.Ordinal))
                return label;
            return label + (sortDirection == ListSortDirection.Ascending ? " ▲" : " ▼");
        }

        PidColumn.Header = FormatHeader("PID", nameof(TrafficRowView.Pid));
        ProcessColumn.Header = FormatHeader("Process", nameof(TrafficRowView.ProcessName));
        CountColumn.Header = FormatHeader("Count", nameof(TrafficRowView.Count));
        DownloadColumn.Header = FormatHeader($"Download {suffix}", nameof(TrafficRowView.DownloadPerSecond));
        UploadColumn.Header = FormatHeader($"Upload {suffix}", nameof(TrafficRowView.UploadPerSecond));
        TotalColumn.Header = FormatHeader($"Total {suffix}", nameof(TrafficRowView.TotalPerSecond));
        TotalDownloadColumn.Header = FormatHeader("Total downloaded", nameof(TrafficRowView.TotalDownload));
        TotalUploadColumn.Header = FormatHeader("Total uploaded", nameof(TrafficRowView.TotalUpload));
        PeakDownloadColumn.Header = FormatHeader($"Peak download {suffix}", nameof(TrafficRowView.PeakDownload));
        PeakUploadColumn.Header = FormatHeader($"Peak upload {suffix}", nameof(TrafficRowView.PeakUpload));

        foreach (DataGridColumn col in TrafficGrid.Columns)
            col.SortDirection = null;

        DataGridColumn? sortedCol = TrafficGrid.Columns
            .FirstOrDefault(c => c.SortMemberPath == sortMember);
        if (sortedCol is not null)
            sortedCol.SortDirection = sortDirection;
    }

    private void AdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!isInitialized) return;
        string? selected = AdapterCombo.SelectedItem as string;
        selectedAdapterName = selected == "All adapters" ? null : selected;
        monitor.SetLocalAddressFilter(GetAdapterAddresses(selectedAdapterName));
        ClearTrafficState();
        RenderRows();
        lastConnectionRefresh = DateTime.MinValue;
        MaybeRefreshConnections();
        UpdateStatusOnly();
    }

    private static IReadOnlyCollection<IPAddress>? GetAdapterAddresses(string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
            return null;

        try
        {
            NetworkInterface? adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(nic => string.Equals(
                    nic.Name,
                    adapterName,
                    StringComparison.OrdinalIgnoreCase));

            if (adapter is null)
                return null;

            return adapter.GetIPProperties()
                .UnicastAddresses
                .Select(address => address.Address)
                .Where(address => address.AddressFamily is
                    System.Net.Sockets.AddressFamily.InterNetwork or
                    System.Net.Sockets.AddressFamily.InterNetworkV6)
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    private void FollowSelectedProcessCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized) return;
        RefreshConnections();
    }

    private void ThresholdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!isInitialized) return;
        UpdateAlertThreshold();
    }

    private void ThresholdUnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!isInitialized) return;
        UpdateAlertThreshold();
    }

    private double GetThresholdDivisor()
    {
        return (ThresholdUnitCombo.SelectedItem as string) switch
        {
            "KiB/s" => 1024.0,
            "GiB/s" => 1024.0 * 1024.0 * 1024.0,
            _ => 1024.0 * 1024.0 // MiB/s
        };
    }

    private void UpdateAlertThreshold()
    {
        if (double.TryParse(
                ThresholdBox.Text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out double value) &&
            value > 0)
        {
            alertThresholdBytesPerSecond = value * GetThresholdDivisor();
        }
        else
        {
            alertThresholdBytesPerSecond = 0;
        }

        alertFired = false;
    }

    private void MinimizeToTrayMenuItem_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized) return;

        minimizeToTray = MinimizeToTrayMenuItem.IsChecked;
        if (trayIcon is not null)
            trayIcon.Visible = minimizeToTray && WindowState == WindowState.Minimized;
    }

    private void ExportCsvMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export traffic as CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = "csv",
            FileName = $"QNetwork_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            ExportToCsv(dialog.FileName);
            StatusText.Text = $"Exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed: {ex.Message}", "Export Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportToCsv(string filePath)
    {
        var sb = new StringBuilder();
        string unit = currentUnit.Label;
        sb.AppendLine($"PID,Process,Count,\"Download ({unit})\",\"Upload ({unit})\",\"Total ({unit})\",\"Session DL ({unit})\",\"Session UL ({unit})\",\"Peak DL ({unit})\",\"Peak UL ({unit})\"");

        static string Csv(double value) =>
            value.ToString("F2", CultureInfo.InvariantCulture);

        foreach (TrafficRowView row in rows)
        {
            sb.Append(row.Pid).Append(',');
            sb.Append('"').Append(row.ProcessName.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"').Append(',');
            sb.Append(row.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Csv(row.DownloadPerSecond)).Append(',');
            sb.Append(Csv(row.UploadPerSecond)).Append(',');
            sb.Append(Csv(row.TotalPerSecond)).Append(',');
            sb.Append(Csv(row.TotalDownload)).Append(',');
            sb.Append(Csv(row.TotalUpload)).Append(',');
            sb.Append(Csv(row.PeakDownload)).Append(',');
            sb.AppendLine(Csv(row.PeakUpload));
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    private void RefreshConnections_Click(object sender, RoutedEventArgs e)
    {
        RefreshConnections();
    }

    private void MaybeRefreshConnections()
    {
        if (!ConnectionsTab.IsSelected)
            return;

        if (DateTime.Now - lastConnectionRefresh < ConnectionRefreshInterval)
            return;

        RefreshConnections();
    }

    private void RefreshConnections()
    {
        if (!isInitialized)
            return;

        try
        {
            List<ConnectionRow> conns = ConnectionMonitor.GetConnections(selectedAdapterName);
            if (FollowSelectedProcessCheckBox.IsChecked == true &&
                TrafficGrid.SelectedItem is TrafficRowView selected)
            {
                conns = selected.Pid > 0
                    ? conns.Where(conn => conn.Pid == selected.Pid).ToList()
                    : conns.Where(conn => string.Equals(
                        conn.ProcessName,
                        selected.ProcessName,
                        StringComparison.CurrentCultureIgnoreCase)).ToList();
            }

            connections.Clear();
            foreach (ConnectionRow c in conns)
                connections.Add(c);

            lastConnectionRefresh = DateTime.Now;
            string adapterText = selectedAdapterName is null ? "all adapters" : selectedAdapterName;
            string followText = FollowSelectedProcessCheckBox.IsChecked == true &&
                TrafficGrid.SelectedItem is TrafficRowView row
                    ? $" for {row.ProcessName}"
                    : string.Empty;
            ConnectionsTitleText.Text =
                $"Active TCP and UDP connections{followText} ({adapterText}, {conns.Count:N0})";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Connection refresh failed: {ex.Message}";
        }
    }

    private void CopyProcessName_Click(object sender, RoutedEventArgs e)
    {
        if (TrafficGrid.SelectedItem is TrafficRowView row)
            Clipboard.SetText(row.ProcessName);
    }

    private void CopyPid_Click(object sender, RoutedEventArgs e)
    {
        if (TrafficGrid.SelectedItem is TrafficRowView row)
            Clipboard.SetText(row.Pid.ToString());
    }

    private void CopyExePath_Click(object sender, RoutedEventArgs e)
    {
        if (TrafficGrid.SelectedItem is not TrafficRowView row) return;

        string? path = GetSelectedExecutablePath(row);
        if (!string.IsNullOrEmpty(path))
            Clipboard.SetText(path);
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (TrafficGrid.SelectedItem is not TrafficRowView row) return;

        string? path = GetSelectedExecutablePath(row);

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        else
        {
            MessageBox.Show(this, "Executable path is not available.", "Open File Location",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private string? GetSelectedExecutablePath(TrafficRowView row)
    {
        if (!string.IsNullOrEmpty(row.ExecutablePath))
            return row.ExecutablePath;

        return row.Pid > 0 ? GetExecutablePathCached(row.Pid) : null;
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        minimizeToTray = false;
        Close();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow
        {
            Owner = this
        };
        about.ShowDialog();
    }
}
