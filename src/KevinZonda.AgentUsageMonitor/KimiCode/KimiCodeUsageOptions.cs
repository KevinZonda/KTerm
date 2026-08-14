namespace KevinZonda.AgentUsageMonitor.KimiCode;

public enum KimiCodeUsageMode
{
    Auto,
    ApiKey,
    CliCredential,
}

public sealed class KimiCodeUsageOptions
{
    public KimiCodeUsageMode Mode { get; init; } = KimiCodeUsageMode.Auto;

    public string? ApiKey { get; init; }

    public Uri BaseUri { get; init; } = new("https://api.kimi.com");

    public string? KimiCodeHome { get; init; }

    public string? DeviceId { get; init; }

    public string UserAgent { get; init; } = "KevinZonda.AgentUsageMonitor/1.0";
}
