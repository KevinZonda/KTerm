using System.Text.Json;
using KevinZonda.AgentUsageMonitor.Internal;

namespace KevinZonda.AgentUsageMonitor.Codex;

internal sealed record CodexCredential(
    string AccessToken,
    string RefreshToken,
    string? IdToken,
    string? AccountId,
    DateTimeOffset? LastRefresh)
{
    public bool NeedsRefresh(DateTimeOffset now) =>
        LastRefresh is null || now - LastRefresh > TimeSpan.FromDays(8);
}

internal static class CodexCredentialStore
{
    public static string ResolveHome(CodexUsageOptions options)
    {
        var configured = FirstNotEmpty(options.CodexHome, Environment.GetEnvironmentVariable("CODEX_HOME"));
        return configured ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public static string ResolveAuthPath(CodexUsageOptions options) =>
        FirstNotEmpty(options.AuthFilePath) ?? Path.Combine(ResolveHome(options), "auth.json");

    public static async Task<CodexCredential> LoadAsync(CodexUsageOptions options, CancellationToken cancellationToken)
    {
        var path = ResolveAuthPath(options);
        if (!File.Exists(path))
        {
            throw new UsageException(UsageErrorCode.MissingCredential, $"Codex credentials were not found at {path}.");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.String("OPENAI_API_KEY") is { Length: > 0 } apiKey)
            {
                return new CodexCredential(apiKey, string.Empty, null, null, null);
            }

            var tokens = root.Property("tokens")
                ?? throw new UsageException(UsageErrorCode.InvalidCredential, "Codex auth.json contains no tokens object.");
            var accessToken = tokens.String("access_token", "accessToken")?.Trim();
            var refreshToken = tokens.String("refresh_token", "refreshToken")?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(accessToken))
            {
                throw new UsageException(UsageErrorCode.InvalidCredential, "Codex auth.json contains no access token.");
            }

            var lastRefreshText = root.String("last_refresh", "lastRefresh");
            DateTimeOffset? lastRefresh = DateTimeOffset.TryParse(lastRefreshText, out var parsed) ? parsed : null;
            return new CodexCredential(
                accessToken,
                refreshToken,
                tokens.String("id_token", "idToken"),
                tokens.String("account_id", "accountId"),
                lastRefresh);
        }
        catch (UsageException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new UsageException(UsageErrorCode.InvalidCredential, "Codex auth.json is invalid JSON.", exception);
        }
    }

    private static string? FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
