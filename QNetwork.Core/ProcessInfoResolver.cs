using System.Diagnostics;

namespace QNetwork.Core;

public static class ProcessInfoResolver
{
    public static ProcessInfo Resolve(int pid)
    {
        if (pid == 0)
            return new ProcessInfo(0, "System/Idle", null, null, null, null);

        try
        {
            using Process process = Process.GetProcessById(pid);
            string processName = process.ProcessName;
            string? executablePath = null;
            DateTime? startTime = null;

            try { executablePath = process.MainModule?.FileName; }
            catch { /* protected or system process */ }

            try { startTime = process.StartTime; }
            catch { /* may be denied */ }

            string? fileDescription = null;
            string? company = null;

            if (executablePath is not null)
            {
                try
                {
                    FileVersionInfo versionInfo =
                        FileVersionInfo.GetVersionInfo(executablePath);
                    fileDescription = string.IsNullOrWhiteSpace(versionInfo.FileDescription)
                        ? null
                        : versionInfo.FileDescription;
                    company = string.IsNullOrWhiteSpace(versionInfo.CompanyName)
                        ? null
                        : versionInfo.CompanyName;
                }
                catch { /* version info not available */ }
            }

            return new ProcessInfo(
                pid,
                processName,
                executablePath,
                startTime,
                fileDescription,
                company);
        }
        catch
        {
            return new ProcessInfo(pid, "Unknown", null, null, null, null);
        }
    }
}

public sealed record ProcessInfo(
    int Pid,
    string ProcessName,
    string? ExecutablePath,
    DateTime? StartTime,
    string? FileDescription,
    string? Company);
