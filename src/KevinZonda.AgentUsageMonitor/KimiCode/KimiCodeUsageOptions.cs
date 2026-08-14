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

    public Uri OAuthBaseUri { get; init; } = new("https://auth.kimi.com");

    public string? KimiCodeHome { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>
    /// Refreshes an expiring CLI OAuth token for this client instance only.
    /// Refreshed credentials are kept in memory and are never written to disk.
    /// </summary>
    public bool AutoRenewToken { get; init; }

    public string UserAgent { get; init; } = "KevinZonda.AgentUsageMonitor/1.0";
}
