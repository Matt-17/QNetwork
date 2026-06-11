namespace QNetwork.Core;

public sealed class InsufficientEtwResourcesException : Exception
{
    public InsufficientEtwResourcesException(Exception innerException)
        : base(
            "Windows has no free ETW logger resources for the network monitor.",
            innerException)
    {
    }
}
