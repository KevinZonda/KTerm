namespace KevinZonda.AgentUsageMonitor;

/// <summary>Common contract for a provider that can retrieve quota usage.</summary>
public interface IUsageClient
{
    /// <summary>Gets the provider served by this client.</summary>
    UsageProvider Provider { get; }

    /// <summary>Retrieves the latest usage using the options configured on the client.</summary>
    Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default);
}
