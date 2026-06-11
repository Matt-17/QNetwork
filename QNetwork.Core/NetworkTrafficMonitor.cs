using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace QNetwork.Core;

public sealed class NetworkTrafficMonitor : IAsyncDisposable, IDisposable
{
    private sealed class TrafficCounter
    {
        public long Sent;
        public long Received;
    }

    private const string SessionName = "QNetworkMonitor";
    private const string OldSessionNamePrefix = "NetzwerkMonitor-";
    private const int HResultInsufficientResources = unchecked((int)0x800705AA);

    private readonly ConcurrentDictionary<int, TrafficCounter> traffic = new();
    private TraceEventSession? session;
    private Task? processingTask;
    private EventHandler? processExitHandler;
    private bool isStopped = true;

    public static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    public static bool IsElevated => TraceEventSession.IsElevated() == true;

    public void Start()
    {
        ThrowIfStarted();
        StopLeftoverSessions();

        var newSession = new TraceEventSession(SessionName)
        {
            StopOnDispose = true
        };

        try
        {
            newSession.EnableKernelProvider(
                KernelTraceEventParser.Keywords.NetworkTCPIP);
        }
        catch (COMException exception)
            when (exception.HResult == HResultInsufficientResources)
        {
            newSession.Dispose();
            throw new InsufficientEtwResourcesException(exception);
        }

        RegisterHandlers(newSession);

        session = newSession;
        isStopped = false;
        processExitHandler = (_, _) => Stop();
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;
        processingTask = Task.Run(() => newSession.Source.Process());
    }

    public void Stop()
    {
        if (isStopped)
            return;

        isStopped = true;
        session?.Stop();
    }

    public async ValueTask StopAsync()
    {
        Stop();

        if (processingTask is not null)
            await processingTask.ConfigureAwait(false);
    }

    public List<TrafficRow> ReadCurrentTraffic()
    {
        var rows = new List<TrafficRow>();

        foreach ((int pid, TrafficCounter counter) in traffic)
        {
            long sent = Interlocked.Exchange(ref counter.Sent, 0);
            long received = Interlocked.Exchange(ref counter.Received, 0);

            rows.Add(new TrafficRow(
                pid,
                GetProcessName(pid),
                received,
                sent));
        }

        return rows;
    }

    public static void StopLeftoverSessions()
    {
        foreach (string activeSessionName in TraceEventSession.GetActiveSessionNames())
        {
            if (!IsOwnSessionName(activeSessionName))
                continue;

            try
            {
                using TraceEventSession? activeSession =
                    TraceEventSession.GetActiveSession(activeSessionName);

                activeSession?.Stop();
            }
            catch (Exception exception) when (
                exception is COMException or InvalidOperationException)
            {
                // The session may already be gone or owned by a dying process.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        UnregisterProcessExitHandler();
        session?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        processingTask?.Wait(TimeSpan.FromSeconds(2));
        UnregisterProcessExitHandler();
        session?.Dispose();
    }

    private void RegisterHandlers(TraceEventSession traceSession)
    {
        traceSession.Source.Kernel.TcpIpSend += data =>
            AddSent(data.ProcessID, data.size);

        traceSession.Source.Kernel.TcpIpRecv += data =>
            AddReceived(data.ProcessID, data.size);

        traceSession.Source.Kernel.TcpIpSendIPV6 += data =>
            AddSent(data.ProcessID, data.size);

        traceSession.Source.Kernel.TcpIpRecvIPV6 += data =>
            AddReceived(data.ProcessID, data.size);

        traceSession.Source.Kernel.UdpIpSend += data =>
            AddSent(data.ProcessID, data.size);

        traceSession.Source.Kernel.UdpIpRecv += data =>
            AddReceived(data.ProcessID, data.size);

        traceSession.Source.Kernel.UdpIpSendIPV6 += data =>
            AddSent(data.ProcessID, data.size);

        traceSession.Source.Kernel.UdpIpRecvIPV6 += data =>
            AddReceived(data.ProcessID, data.size);
    }

    private void AddSent(int pid, int bytes)
    {
        if (pid < 0 || bytes <= 0)
            return;

        TrafficCounter counter =
            traffic.GetOrAdd(pid, _ => new TrafficCounter());

        Interlocked.Add(ref counter.Sent, bytes);
    }

    private void AddReceived(int pid, int bytes)
    {
        if (pid < 0 || bytes <= 0)
            return;

        TrafficCounter counter =
            traffic.GetOrAdd(pid, _ => new TrafficCounter());

        Interlocked.Add(ref counter.Received, bytes);
    }

    private void ThrowIfStarted()
    {
        if (!isStopped)
            throw new InvalidOperationException("The network traffic monitor is already started.");
    }

    private void UnregisterProcessExitHandler()
    {
        if (processExitHandler is null)
            return;

        AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        processExitHandler = null;
    }

    private static bool IsOwnSessionName(string sessionName)
    {
        return string.Equals(
                sessionName,
                SessionName,
                StringComparison.OrdinalIgnoreCase) ||
            sessionName.StartsWith(
                OldSessionNamePrefix,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return pid == 0 ? "System/Idle" : "<beendet oder unbekannt>";
        }
    }
}
