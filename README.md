# QNetwork

QNetwork is a Windows desktop tool for operator-facing network observability.
It uses ETW to provide live per-process traffic telemetry in one place for troubleshooting,
incident response, and production diagnostics.

## What it does

- Monitors live process network activity from ETW sessions.
- Shows per-process send/receive throughput, session totals, peaks, and connection counts.
- Supports process grouping, adapter filtering, history trends, CSV export, alerts, tray mode, and compact operator views.
- Includes `QNetwork.Cli` for terminal diagnostics and one-shot JSON snapshots.

## Administrator rights

The installer is machine-wide, and the app requires administrator rights when launched because ETW kernel tracing requires elevation.
The desktop app declares `requireAdministrator`, so Windows shows the UAC shield/elevation prompt. The CLI can also relaunch elevated with `--elevate`.

## Installation

```powershell
winget install Code-iX.QNetwork
```

Manual fallback: download `QNetwork-X.Y.Z-x64.msi` from the latest GitHub Release and install it.

## CLI

```powershell
QNetwork.Cli --once --json --elevate
```

Useful options:

- `--elevate`: restart through UAC if administrator rights are required.
- `--once`: capture one sample and exit.
- `--json`: emit the `--once` sample as JSON.

## Build and test

```powershell
dotnet restore QNetwork.slnx
dotnet build QNetwork.slnx -c Release
dotnet test QNetwork.slnx
```

## Releasing

- Push a tag like `v1.2.3`.
- The release workflow builds/tests, publishes self-contained `win-x64`, builds `QNetwork-1.2.3-x64.msi`, creates a GitHub Release, and runs `winget-releaser` when `WINGET_TOKEN` is configured.
- Keep the installer `UpgradeCode` stable across releases.
- The winget identifier is `Code-iX.QNetwork`; the installer is self-contained and should not declare a .NET runtime dependency.