using System.Text;
using System.Text.Json;
using KevinZonda.AgentUsageMonitor.Internal;

namespace KevinZonda.AgentUsageMonitor.Codex;

internal static class CodexUsageParser
{
    public static UsageSnapshot ParseOAuth(ReadOnlySpan<byte> data, CodexCredential credential, DateTimeOffset updatedAt)
    {
        using var document = JsonDocument.Parse(data.ToArray());
        var root = document.RootElement;
        var rateLimit = root.Property("rate_limit", "rateLimit");
        var primary = ParseWindow(rateLimit?.Property("primary_window", "primaryWindow"), "Session");
        var secondary = ParseWindow(rateLimit?.Property("secondary_window", "secondaryWindow"), "Weekly");
        NormalizeWindows(ref primary, ref secondary);

        var creditsElement = root.Property("credits");
        var credits = creditsElement is null
            ? null
            : new UsageCredits(
                creditsElement.Value.Double("balance"),
                creditsElement.Value.Boolean("unlimited") ?? false);
        var budget = ParseBudget(
            root.Property("individual_limit", "individualLimit")
            ?? rateLimit?.Property("individual_limit", "individualLimit")
            ?? root.Property("spend_control", "spendControl")?.Property("individual_limit", "individualLimit"));
        var extras = ParseAdditionalWindows(root.Property("additional_rate_limits"));
        var (email, tokenPlan) = ParseIdentity(credential.IdToken);
        var plan = root.String("plan_type") ?? tokenPlan;

        if (primary is null && secondary is null && credits is null && budget is null)
        {
            throw new UsageException(UsageErrorCode.InvalidResponse, "Codex response contains no usage or credit data.");
        }

        return new UsageSnapshot(
            UsageProvider.Codex,
            UsageSource.CodexOAuth,
            primary,
            secondary,
            extras,
            credits,
            budget,
            email,
            plan,
            updatedAt);
    }

    public static UsageSnapshot ParseRpc(JsonElement limitsResult, JsonElement? accountResult, DateTimeOffset updatedAt)
    {
        var limits = limitsResult.Property("rateLimits", "rate_limits") ?? limitsResult;
        var primary = ParseRpcWindow(limits.Property("primary"), "Session");
        var secondary = ParseRpcWindow(limits.Property("secondary"), "Weekly");
        NormalizeWindows(ref primary, ref secondary);

        var creditsElement = limits.Property("credits");
        var credits = creditsElement is null
            ? null
            : new UsageCredits(
                creditsElement.Value.Double("balance"),
                creditsElement.Value.Boolean("unlimited") ?? false);
        var budget = ParseBudget(limits.Property("individualLimit", "individual_limit"));
        var (email, plan) = ParseRpcAccount(accountResult);
        plan ??= limits.String("planType", "plan_type");

        if (primary is null && secondary is null && credits is null && budget is null)
        {
            throw new UsageException(UsageErrorCode.InvalidResponse, "Codex app-server returned no usage or credit data.");
        }

        return new UsageSnapshot(
            UsageProvider.Codex,
            UsageSource.CodexAppServer,
            primary,
            secondary,
            [],
            credits,
            budget,
            email,
            plan,
            updatedAt);
    }

    private static UsageWindow? ParseWindow(JsonElement? value, string name)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usedPercent = value.Value.Double("used_percent", "usedPercent");
        if (usedPercent is null)
        {
            return null;
        }

        var seconds = value.Value.Int64("limit_window_seconds", "limitWindowSeconds");
        return new UsageWindow(
            name,
            JsonHelpers.ClampPercent(usedPercent.Value),
            seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null,
            JsonHelpers.UnixDate(value.Value.Int64("reset_at", "resetAt")));
    }

    private static UsageWindow? ParseRpcWindow(JsonElement? value, string name)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usedPercent = value.Value.Double("usedPercent", "used_percent");
        if (usedPercent is null)
        {
            return null;
        }

        var minutes = value.Value.Int64("windowDurationMins", "window_duration_mins");
        return new UsageWindow(
            name,
            JsonHelpers.ClampPercent(usedPercent.Value),
            minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null,
            JsonHelpers.UnixDate(value.Value.Int64("resetsAt", "resets_at")));
    }

    private static UsageBudget? ParseBudget(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object || value.Value.Double("limit") is not { } limit
            || limit <= 0)
        {
            return null;
        }

        var used = value.Value.Double("used");
        var remaining = value.Value.Double("remainingPercent", "remaining_percent");
        used ??= remaining is null ? 0 : limit * Math.Clamp(100 - remaining.Value, 0, 100) / 100;
        remaining ??= Math.Clamp(100 - used.Value / limit * 100, 0, 100);
        return new UsageBudget(
            "Monthly credit limit",
            limit,
            used.Value,
            remaining.Value,
            JsonHelpers.UnixDate(value.Value.Int64("resetsAt", "resets_at", "reset_at")));
    }

    private static IReadOnlyList<UsageWindow> ParseAdditionalWindows(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<UsageWindow>();
        foreach (var entry in value.Value.EnumerateArray())
        {
            var name = entry.String("limit_name", "metered_feature") ?? "Codex extra limit";
            var rateLimit = entry.Property("rate_limit");
            var primary = ParseWindow(rateLimit?.Property("primary_window"), name);
            var secondary = ParseWindow(rateLimit?.Property("secondary_window"), name + " Weekly");
            if (primary is not null)
            {
                result.Add(primary);
            }

            if (secondary is not null)
            {
                result.Add(secondary);
            }
        }

        return result;
    }

    private static void NormalizeWindows(ref UsageWindow? primary, ref UsageWindow? secondary)
    {
        if (primary?.Window == TimeSpan.FromDays(7) && secondary?.Window == TimeSpan.FromHours(5))
        {
            (primary, secondary) = (secondary with { Name = "Session" }, primary with { Name = "Weekly" });
        }
        else if (primary?.Window == TimeSpan.FromDays(7) && secondary is null)
        {
            secondary = primary with { Name = "Weekly" };
            primary = null;
        }
        else if (secondary?.Window == TimeSpan.FromHours(5) && primary is null)
        {
            primary = secondary with { Name = "Session" };
            secondary = null;
        }
    }

    private static (string? Email, string? Plan) ParseIdentity(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return (null, null);
        }

        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3)
            {
                return (null, null);
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            var profile = root.Property("https://api.openai.com/profile");
            var auth = root.Property("https://api.openai.com/auth");
            return (
                root.String("email") ?? profile?.String("email"),
                auth?.String("chatgpt_plan_type") ?? root.String("chatgpt_plan_type"));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return (null, null);
        }
    }

    private static (string? Email, string? Plan) ParseRpcAccount(JsonElement? value)
    {
        var account = value?.Property("account");
        if (account is null || account.Value.ValueKind != JsonValueKind.Object
            || !string.Equals(account.Value.String("type"), "chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        return (account.Value.String("email"), account.Value.String("planType", "plan_type"));
    }
}
