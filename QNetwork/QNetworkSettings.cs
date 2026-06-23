using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QNetwork;

public sealed class QNetworkSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QNetwork",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    // Window
    public double WindowWidth { get; set; } = 980;
    public double WindowHeight { get; set; } = 720;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;

    // Display
    public string Unit { get; set; } = "KiB/s";
    public bool HideIdle { get; set; } = true;
    public bool GroupByProcessName { get; set; } = false;
    public string? SearchText { get; set; } = null;

    // Sort
    public string SortMember { get; set; } = "TotalPerSecond";
    public string SortDirection { get; set; } = "Descending";

    // Optional columns
    public bool ShowTotalDownload { get; set; } = false;
    public bool ShowTotalUpload { get; set; } = false;
    public bool ShowPeakDownload { get; set; } = false;
    public bool ShowPeakUpload { get; set; } = false;
    public bool ShowIcons { get; set; } = false;

    // Panels
    public bool ShowChart { get; set; } = true;
    public bool ShowDetailsPanel { get; set; } = false;

    // Tray
    public bool MinimizeToTray { get; set; } = false;

    // Alerts: value is expressed in AlertThresholdUnit, exactly as entered.
    public double AlertThresholdValue { get; set; } = 0.0;
    public string AlertThresholdUnit { get; set; } = "MiB/s";

    // Adapter
    public string? SelectedAdapterName { get; set; } = null;

    public static QNetworkSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new QNetworkSettings();

            string json = File.ReadAllText(SettingsPath);
            QNetworkSettings settings = JsonSerializer.Deserialize<QNetworkSettings>(json, JsonOptions)
                   ?? new QNetworkSettings();
            settings.Validate();
            return settings;
        }
        catch
        {
            return new QNetworkSettings();
        }
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Settings save failure is non-fatal.
        }
    }

    private void Validate()
    {
        if (WindowWidth < 820 || double.IsNaN(WindowWidth) || double.IsInfinity(WindowWidth))
            WindowWidth = 980;
        if (WindowHeight < 560 || double.IsNaN(WindowHeight) || double.IsInfinity(WindowHeight))
            WindowHeight = 720;

        if (Unit is not ("B/s" or "KiB/s" or "MiB/s"))
            Unit = "KiB/s";

        if (SortDirection is not ("Ascending" or "Descending"))
            SortDirection = "Descending";

        string[] validSortMembers =
        [
            "Pid",
            "ProcessName",
            "Count",
            "DownloadPerSecond",
            "UploadPerSecond",
            "TotalPerSecond",
            "TotalDownload",
            "TotalUpload",
            "PeakDownload",
            "PeakUpload"
        ];
        if (!validSortMembers.Contains(SortMember))
            SortMember = "TotalPerSecond";

        if (AlertThresholdValue < 0 ||
            double.IsNaN(AlertThresholdValue) ||
            double.IsInfinity(AlertThresholdValue))
            AlertThresholdValue = 0;

        if (AlertThresholdUnit is not ("KiB/s" or "MiB/s" or "GiB/s"))
            AlertThresholdUnit = "MiB/s";
    }
}
