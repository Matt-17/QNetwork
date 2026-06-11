namespace QNetwork.Core;

public sealed record TrafficRow(
    int Pid,
    string ProcessName,
    long Received,
    long Sent)
{
    public long Total => Received + Sent;
}
