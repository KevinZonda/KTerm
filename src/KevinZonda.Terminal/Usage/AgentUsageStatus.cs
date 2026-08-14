namespace KevinZonda.Terminal.Usage;

internal sealed record AgentUsageStatus(IReadOnlyList<AgentProviderUsageStatus> Providers)
{
    internal static AgentUsageStatus Empty { get; } = new([]);
}

internal sealed record AgentProviderUsageStatus(
    string Provider,
    string State,
    IReadOnlyList<AgentUsageWindowStatus> Windows,
    DateTimeOffset? UpdatedAt,
    string? Error);

internal sealed record AgentUsageWindowStatus(
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt);
