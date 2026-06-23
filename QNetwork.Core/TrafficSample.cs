namespace QNetwork.Core;

public sealed record TrafficSample(
    DateTime Timestamp,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond);
