# QNetwork Implementation Plan

This plan captures useful next steps for QNetwork after the current split into:

- `QNetwork.Core`: ETW monitor, traffic rows, filtering and sorting helpers.
- `QNetwork`: WPF application.
- `QNetwork.Cli`: console application.

## 1. Session Totals

Add cumulative byte counters per process for the current app session.

Core changes:
- Extend `TrafficCounter` in `NetworkTrafficMonitor` with `TotalSent` and `TotalReceived`.
- Increment both the interval counters and total counters in `AddSent` / `AddReceived`.
- Add `TotalReceived` and `TotalSent` properties to `TrafficRow`, or introduce a separate `TrafficSnapshotRow` if keeping interval and cumulative data distinct is cleaner.

WPF changes:
- Add optional columns:
  - `Total downloaded`
  - `Total uploaded`
- Format totals using the selected unit, but without `/s`.
- Add View menu toggles for showing/hiding total columns.

CLI changes:
- Add total columns if terminal width allows.
- Otherwise expose totals behind a key toggle.

## 2. Peak Rates

Track the highest observed upload/download rate per process during the session.

Core changes:
- Keep peak values outside raw ETW event handlers if possible, because peak rate depends on sample interval.
- Add a WPF/CLI-side state dictionary keyed by PID:
  - `PeakDownloadBytesPerSecond`
  - `PeakUploadBytesPerSecond`
- Update peaks whenever a new sample is rendered.

WPF changes:
- Add optional `Peak download` and `Peak upload` columns.
- Default them hidden to keep the table compact.
- Add View menu toggles.

Technical note:
- If process grouping is implemented first, decide whether peaks belong per PID or per process group.

## 3. Process Details Panel

Show richer information for the selected row.

Core changes:
- Add a `ProcessInfoResolver` helper:
  - PID
  - process name
  - executable path, when accessible
  - start time, when accessible
  - main module path, when accessible
- Keep exception handling aggressive because protected/system processes often deny access.

WPF changes:
- Add a right-side or bottom details panel.
- Bind to `TrafficGrid.SelectedItem`.
- Show:
  - process name
  - PID
  - executable path
  - current download/upload
  - total download/upload
  - peak download/upload
- Add context actions:
  - copy process name
  - copy PID
  - copy executable path
  - open file location, if path is available

## 4. History Chart

Add a small time-series chart for total traffic.

Implementation approach:
- Start with no chart dependency if possible:
  - Use a WPF `Canvas` or custom `FrameworkElement`.
  - Store the last 60 samples in a ring buffer.
  - Draw download and upload polylines.
- If this becomes too much code, consider a small chart library later.

Data model:
- `TrafficSample`
  - `Timestamp`
  - `DownloadBytesPerSecond`
  - `UploadBytesPerSecond`

WPF behavior:
- Default chart shows total app-observed traffic.
- If a process row is selected, chart switches to that process.
- Show selected process name in the chart header.

## 5. Process Grouping

Group rows by process name, with optional PID-level expansion.

Core changes:
- Add helper method:
  - `TrafficRows.GroupByProcessName(IEnumerable<TrafficRow>)`
- Grouped row should include:
  - process name
  - process count
  - summed received/sent
  - summed totals, if session totals are implemented

WPF changes:
- Add a View menu toggle: `Group by process name`.
- When enabled, table shows one row per process name.
- Add a `Count` column for number of PIDs.
- Optional later: expandable groups with child PIDs.

CLI changes:
- Add a key toggle for grouped/individual view.

## 6. Settings Persistence

Remember user preferences between launches.

Core or WPF-specific:
- Prefer WPF-specific settings first to avoid polluting core with UI concerns.
- Add `QNetworkSettings` in the WPF project.
- Store JSON under:
  - `%APPDATA%\QNetwork\settings.json`

Settings to persist:
- window size and position
- selected unit
- hide-idle toggle
- sort column and direction
- visible optional columns
- process grouping toggle
- search text, probably optional

Technical notes:
- Save on close.
- Load on startup after `InitializeComponent`.
- Validate values defensively in case settings are edited or from an older app version.

## 7. Auto-Elevation Relaunch

Improve startup when the app is not elevated.

Current state:
- WPF has a manifest with `requireAdministrator`.
- CLI still prints an admin message.

CLI improvement:
- Detect non-elevated state.
- Offer a clear error and optional docs message.
- Optionally add `--elevate` later to relaunch itself as admin.

WPF improvement:
- If manifest behavior changes to `asInvoker` later, show a dialog:
  - "QNetwork needs administrator rights for ETW kernel tracing."
  - button: "Restart as administrator"
- Use `ProcessStartInfo.UseShellExecute = true` and `Verb = "runas"`.

## 8. Process Icons and Publisher Info

Make process identity easier to scan.

Core changes:
- Add a best-effort resolver for:
  - executable path
  - file description
  - company/publisher from version info

WPF changes:
- Add a small icon column.
- Cache icons by executable path.
- Use a fallback icon for inaccessible/system processes.

Technical notes:
- Icon extraction should be cached and performed carefully to avoid UI stalls.
- Use async background loading if extraction proves slow.

## 9. Export

Allow users to export current view or session summary.

WPF changes:
- Add `File > Export CSV`.
- Export either:
  - current visible table
  - full unfiltered current snapshot
  - session totals, once available

Core changes:
- Optional `CsvExporter` helper if both WPF and CLI need it.

Technical notes:
- Use invariant culture for numeric CSV values.
- Include unit metadata in headers, for example `Download (KiB/s)`.

## 10. Tray Mode

Support long-running monitoring without an open window.

WPF changes:
- Add tray icon.
- Add setting: minimize to tray.
- Tooltip should show total current download/upload.
- Context menu:
  - Open QNetwork
  - Pause/Resume
  - Exit

Technical notes:
- WPF has no native tray icon control; use WinForms `NotifyIcon` or a small helper package.
- Ensure ETW session stops on explicit Exit.

## 11. Threshold Alerts

Notify when a process exceeds a configured rate.

Settings:
- threshold value
- unit
- duration before alert
- cooldown per process

Implementation:
- Evaluate thresholds during each sample.
- Keep state by PID or process group:
  - first time above threshold
  - last notification time
- Use Windows toast notifications later; start with status bar or tray balloon.

## 12. Connection View

Show remote endpoints per process.

Research needed:
- Current ETW events provide enough packet-level process IDs and sizes, but endpoint mapping may require additional event fields or separate Windows APIs.
- Possible sources:
  - ETW TCP/IP events with address/port payloads
  - `IPGlobalProperties.GetActiveTcpConnections`
  - Windows IP Helper API for PID-aware TCP/UDP tables

Initial scope:
- Add a separate tab: `Connections`.
- Columns:
  - process
  - PID
  - protocol
  - local endpoint
  - remote endpoint
  - state

Technical caution:
- Joining endpoint data to traffic counters can be approximate.
- Avoid reverse DNS by default because it can block or create network traffic.

## 13. Adapter Filtering

Filter traffic by network adapter.

Research needed:
- Confirm whether the TraceEvent TCP/IP payloads expose interface index reliably.
- If not, adapter filtering may require another data source.

Possible UI:
- toolbar combo: `All adapters`
- list Wi-Fi, Ethernet, VPN adapters

Technical caution:
- VPNs and virtual adapters can make attribution confusing.

## Suggested Order

1. Settings persistence.
2. Session totals.
3. Peak columns.
4. Process details panel.
5. Group by process name.
6. History chart.
7. Export CSV.
8. Process icons.
9. Tray mode.
10. Threshold alerts.
11. Connection view.
12. Adapter filtering.

The first five items are the highest value and fit the current architecture with low risk. Connection and adapter features need more investigation before implementation.
