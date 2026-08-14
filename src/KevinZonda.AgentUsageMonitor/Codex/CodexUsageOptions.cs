namespace KevinZonda.AgentUsageMonitor.Codex;

public enum CodexUsageMode
{
    Auto,
    OAuth,
    AppServer,
}

public sealed class CodexUsageOptions
{
    public CodexUsageMode Mode { get; init; } = CodexUsageMode.Auto;

    public string? CodexHome { get; init; }

    public string? AuthFilePath { get; init; }

    public Uri? ApiBaseUri { get; init; }

    public string CodexExecutable { get; init; } = "codex";

    public TimeSpan RpcInitializeTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan RpcRequestTimeout { get; init; } = TimeSpan.FromSeconds(3);
}
