# QNetwork

QNetwork is a Windows desktop tool for live per-process network traffic monitoring using ETW.

## What it does

- Shows active process totals, in/out rates, session totals, peaks, and active connections.
- Includes a 60-second traffic history chart with process-level grouping and filtering.
- Supports CSV export, tray minimization, adapter filtering, and alert thresholds.

## Administrator rights

Per-process network traffic collection uses ETW kernel tracing and requires elevated privileges. The app will prompt to restart with administrator rights when needed.
