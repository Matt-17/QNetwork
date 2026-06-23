namespace QNetwork.Core;

public sealed record GroupedTrafficRow(
    string ProcessName,
    int Count,
    long Received,
    long Sent,
    long TotalReceived,
    long TotalSent)
{
    public long Total => Received + Sent;
    public long SessionTotal => TotalReceived + TotalSent;
}
