using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

using QNetwork.Core;

namespace QNetwork;

public partial class MainWindow : Window
{
    private sealed record UnitOption(string Label, double Divisor);

    public sealed class TrafficRowView
    {
        public required int Pid { get; init; }

        public required string ProcessName { get; init; }

        public required double DownloadPerSecond { get; init; }

        public required double UploadPerSecond { get; init; }

        public double TotalPerSecond => DownloadPerSecond + UploadPerSecond;
    }

    private static readonly UnitOption[] UnitOptions =
    [
        new("B/s", 1.0),
        new("KiB/s", 1024.0),
        new("MiB/s", 1024.0 * 1024.0)
    ];

    private readonly ObservableCollection<TrafficRowView> rows = [];
    private readonly DispatcherTimer timer = new();
    private readonly NetworkTrafficMonitor monitor = new();
    private readonly ICollectionView rowsView;
    private readonly DateTime startedAt = DateTime.Now;
    private readonly object lifecycleLock = new();

    private List<TrafficRow> currentRows = [];
    private DateTime lastSampleAt = DateTime.Now;
    private string sortMember = nameof(TrafficRowView.TotalPerSecond);
    private ListSortDirection sortDirection = ListSortDirection.Descending;
    private double lastSampleSeconds = 1.0;
    private UnitOption currentUnit = UnitOptions[1];
    private bool isInitialized;
    private bool isPaused;
    private bool isStarted;

    public MainWindow()
    {
        Rows = rows;
        rowsView = CollectionViewSource.GetDefaultView(rows);
        InitializeComponent();
        DataContext = this;
        ApplySort();

        UnitCombo.ItemsSource = UnitOptions;
        UnitCombo.DisplayMemberPath = nameof(UnitOption.Label);
        UnitCombo.SelectedIndex = 1;

        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += Timer_Tick;
        isInitialized = true;
        UpdateColumnHeaders();
    }

    public ObservableCollection<TrafficRowView> Rows { get; }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
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
            StatusText.Text = "Windows has no free ETW logger resources. Close tracing tools or restart Windows.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not start monitor: {ex.Message}";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        lock (lifecycleLock)
        {
            if (!isStarted)
                return;

            isStarted = false;
        }

        timer.Stop();
        monitor.Dispose();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (isPaused)
            return;

        DateTime sampleAt = DateTime.Now;
        double sampleSeconds = Math.Max((sampleAt - lastSampleAt).TotalSeconds, 0.001);
        lastSampleSeconds = sampleSeconds;
        lastSampleAt = sampleAt;

        List<TrafficRow> sampledRows = monitor.ReadCurrentTraffic();
        bool hideIdle = HideIdleCheckBox.IsChecked == true;

        if (TrafficRows.ShouldReplaceDisplayedRows(sampledRows, currentRows, hideIdle))
        {
            currentRows = sampledRows;
            UpdateRows(sampledRows, sampleSeconds);
        }
        else
        {
            UpdateStatusOnly();
        }
    }

    private void UpdateRows(IReadOnlyCollection<TrafficRow> sampledRows, double sampleSeconds)
    {
        if (!isInitialized)
            return;

        bool hideIdle = HideIdleCheckBox.IsChecked == true;
        string searchText = SearchBox.Text;
        UnitOption unit = currentUnit;

        List<TrafficRowView> visibleRows = TrafficRows
            .Filter(sampledRows, hideIdle, searchText)
            .Select(row => new TrafficRowView
            {
                Pid = row.Pid,
                ProcessName = row.ProcessName,
                DownloadPerSecond = row.Received / unit.Divisor / sampleSeconds,
                UploadPerSecond = row.Sent / unit.Divisor / sampleSeconds
            })
            .ToList();

        rows.Clear();

        foreach (TrafficRowView row in visibleRows)
            rows.Add(row);

        rowsView.Refresh();
        UpdateMetrics(visibleRows);
        UpdateStatusOnly();
    }

    private void UpdateMetrics(IReadOnlyCollection<TrafficRowView> visibleRows)
    {
        double download = visibleRows.Sum(row => row.DownloadPerSecond);
        double upload = visibleRows.Sum(row => row.UploadPerSecond);

        DownloadText.Text = $"{download:N1} {currentUnit.Label}";
        UploadText.Text = $"{upload:N1} {currentUnit.Label}";
        ProcessCountText.Text = visibleRows.Count.ToString("N0");
    }

    private void UpdateStatusOnly()
    {
        if (!isInitialized)
            return;

        TimeSpan uptime = DateTime.Now - startedAt;
        string filterText = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? "no search"
            : $"search '{SearchBox.Text}'";

        StatusText.Text =
            $"{filterText} | unit {currentUnit.Label} | uptime {uptime:hh\\:mm\\:ss}";
    }

    private void ApplySort()
    {
        rowsView.SortDescriptions.Clear();
        rowsView.SortDescriptions.Add(new SortDescription(sortMember, sortDirection));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!isInitialized)
            return;

        UpdateRows(currentRows, lastSampleSeconds);
    }

    private void FilterControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized)
            return;

        HideIdleMenuItem.IsChecked = HideIdleCheckBox.IsChecked == true;
        UpdateRows(currentRows, lastSampleSeconds);
    }

    private void HideIdleMenuItem_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized)
            return;

        HideIdleCheckBox.IsChecked = HideIdleMenuItem.IsChecked;
        UpdateRows(currentRows, lastSampleSeconds);
    }

    private void UnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!isInitialized)
            return;

        if (UnitCombo.SelectedItem is not UnitOption option)
            return;

        currentUnit = option;
        UpdateUnitMenuChecks();
        UpdateColumnHeaders();
        UpdateRows(currentRows, lastSampleSeconds);
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        SetPaused(!isPaused);
    }

    private void PauseMenuItem_Changed(object sender, RoutedEventArgs e)
    {
        if (!isInitialized)
            return;

        SetPaused(PauseMenuItem.IsChecked);
    }

    private void SetPaused(bool paused)
    {
        isPaused = paused;
        PauseButton.Content = isPaused ? "Resume" : "Pause";

        if (PauseMenuItem.IsChecked != isPaused)
            PauseMenuItem.IsChecked = isPaused;

        UpdateStatusOnly();
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

    private static bool IsNumericSort(string memberName)
    {
        return memberName is
            nameof(TrafficRowView.Pid) or
            nameof(TrafficRowView.DownloadPerSecond) or
            nameof(TrafficRowView.UploadPerSecond) or
            nameof(TrafficRowView.TotalPerSecond);
    }

    private void UnitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender == BytesUnitMenuItem)
            UnitCombo.SelectedIndex = 0;
        else if (sender == KibUnitMenuItem)
            UnitCombo.SelectedIndex = 1;
        else if (sender == MibUnitMenuItem)
            UnitCombo.SelectedIndex = 2;
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

        PidColumn.Header = FormatHeader(
            "PID",
            nameof(TrafficRowView.Pid));
        ProcessColumn.Header = FormatHeader(
            "Process",
            nameof(TrafficRowView.ProcessName));
        DownloadColumn.Header = FormatHeader(
            "Download " + suffix,
            nameof(TrafficRowView.DownloadPerSecond));
        UploadColumn.Header = FormatHeader(
            "Upload " + suffix,
            nameof(TrafficRowView.UploadPerSecond));
        TotalColumn.Header = FormatHeader(
            "Total " + suffix,
            nameof(TrafficRowView.TotalPerSecond));

        foreach (DataGridColumn column in TrafficGrid.Columns)
            column.SortDirection = null;

        DataGridColumn? sortedColumn = TrafficGrid.Columns
            .FirstOrDefault(column => column.SortMemberPath == sortMember);

        if (sortedColumn is not null)
            sortedColumn.SortDirection = sortDirection;
    }

    private string FormatHeader(string label, string memberName)
    {
        if (!string.Equals(sortMember, memberName, StringComparison.Ordinal))
            return label;

        string arrow = sortDirection == ListSortDirection.Ascending ? " ^" : " v";
        return label + arrow;
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "QNetwork monitors per-process network traffic using Windows ETW kernel events.",
            "About QNetwork",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
