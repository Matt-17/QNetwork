using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace QNetwork;

public sealed record ConnectionRow(
    int Pid,
    string ProcessName,
    string Protocol,
    string LocalAddress,
    string LocalEndpoint,
    string RemoteEndpoint,
    string State);

public static class ConnectionMonitor
{
    private enum TcpTableClass
    {
        OwnerPidAll = 5
    }

    private enum UdpTableClass
    {
        OwnerPid = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        nint pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        TcpTableClass TableClass,
        int Reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedUdpTable(
        nint pUdpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        UdpTableClass TableClass,
        int Reserved);

    private const int AfInet = 2;
    private const int NoError = 0;

    public static List<ConnectionRow> GetConnections(string? adapterName = null)
    {
        HashSet<IPAddress>? adapterAddresses = GetAdapterAddresses(adapterName);
        Dictionary<int, string> processNames = SnapshotProcessNames();
        var result = new List<ConnectionRow>();
        result.AddRange(GetTcpConnections(adapterAddresses, processNames));
        result.AddRange(GetUdpConnections(adapterAddresses, processNames));
        return result;
    }

    private static IEnumerable<ConnectionRow> GetTcpConnections(
        HashSet<IPAddress>? adapterAddresses,
        Dictionary<int, string> processNames)
    {
        int size = 0;
        GetExtendedTcpTable(nint.Zero, ref size, true, AfInet, TcpTableClass.OwnerPidAll, 0);
        if (size <= 0) yield break;

        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            int ret = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableClass.OwnerPidAll, 0);
            if (ret != NoError) yield break;

            int numEntries = Marshal.ReadInt32(buffer);
            nint ptr = buffer + 4;
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (int i = 0; i < numEntries; i++)
            {
                MibTcpRowOwnerPid row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(ptr);
                ptr += rowSize;

                int pid = (int)row.dwOwningPid;
                IPAddress localAddress = ToAddress(row.dwLocalAddr);
                if (!IsIncludedAddress(localAddress, adapterAddresses))
                    continue;

                string local = FormatEndpoint(localAddress, row.dwLocalPort);
                string remote = FormatEndpoint(row.dwRemoteAddr, row.dwRemotePort);

                yield return new ConnectionRow(
                    pid,
                    GetProcessName(pid, processNames),
                    "TCP",
                    localAddress.ToString(),
                    local,
                    remote,
                    GetTcpStateName(row.dwState));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IEnumerable<ConnectionRow> GetUdpConnections(
        HashSet<IPAddress>? adapterAddresses,
        Dictionary<int, string> processNames)
    {
        int size = 0;
        GetExtendedUdpTable(nint.Zero, ref size, true, AfInet, UdpTableClass.OwnerPid, 0);
        if (size <= 0) yield break;

        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            int ret = GetExtendedUdpTable(buffer, ref size, true, AfInet, UdpTableClass.OwnerPid, 0);
            if (ret != NoError) yield break;

            int numEntries = Marshal.ReadInt32(buffer);
            nint ptr = buffer + 4;
            int rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();

            for (int i = 0; i < numEntries; i++)
            {
                MibUdpRowOwnerPid row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(ptr);
                ptr += rowSize;

                int pid = (int)row.dwOwningPid;
                IPAddress localAddress = ToAddress(row.dwLocalAddr);
                if (!IsIncludedAddress(localAddress, adapterAddresses))
                    continue;

                string local = FormatEndpoint(localAddress, row.dwLocalPort);

                yield return new ConnectionRow(
                    pid,
                    GetProcessName(pid, processNames),
                    "UDP",
                    localAddress.ToString(),
                    local,
                    "*:*",
                    "LISTEN");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static HashSet<IPAddress>? GetAdapterAddresses(string? adapterName)
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
                .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .ToHashSet();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsIncludedAddress(
        IPAddress localAddress,
        HashSet<IPAddress>? adapterAddresses)
    {
        if (adapterAddresses is null)
            return true;

        if (IPAddress.Any.Equals(localAddress))
            return true;

        return adapterAddresses.Contains(localAddress);
    }

    private static IPAddress ToAddress(uint addr)
    {
        var ipBytes = BitConverter.GetBytes(addr);
        return new IPAddress(ipBytes);
    }

    private static string FormatEndpoint(uint addr, uint port)
    {
        return FormatEndpoint(ToAddress(addr), port);
    }

    private static string FormatEndpoint(IPAddress ip, uint port)
    {
        int hostPort = IPAddress.NetworkToHostOrder((short)port) & 0xFFFF;
        return $"{ip}:{hostPort}";
    }

    private static Dictionary<int, string> SnapshotProcessNames()
    {
        var names = new Dictionary<int, string>();
        try
        {
            foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
            {
                using (process)
                {
                    names[process.Id] = process.ProcessName;
                }
            }
        }
        catch
        {
            // Fall back to whatever was collected so far.
        }

        names[0] = "System";
        return names;
    }

    private static string GetProcessName(int pid, Dictionary<int, string> processNames)
    {
        return processNames.TryGetValue(pid, out string? name) ? name : "Unknown";
    }

    private static string GetTcpStateName(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTEN",
        3 => "SYN_SENT",
        4 => "SYN_RCVD",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => "UNKNOWN"
    };
}
