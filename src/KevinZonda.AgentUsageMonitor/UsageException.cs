namespace KevinZonda.AgentUsageMonitor;

public enum UsageErrorCode
{
    MissingCredential,
    InvalidCredential,
    InvalidConfiguration,
    InvalidResponse,
    RemoteError,
    ProcessError,
    Timeout,
}

public sealed class UsageException : Exception
{
    public UsageException(UsageErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public UsageErrorCode Code { get; }
}
