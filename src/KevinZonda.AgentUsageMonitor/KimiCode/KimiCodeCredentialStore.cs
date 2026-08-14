using System.Text.Json;
using KevinZonda.AgentUsageMonitor.Internal;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

internal sealed record KimiCodeCredential(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset? ExpiresAt,
    long ExpiresIn,
    string Scope,
    string TokenType);

internal static class KimiCodeCredentialStore
{
    public static string ResolveHome(KimiCodeUsageOptions options)
    {
        var configured = FirstNotEmpty(
            options.KimiCodeHome,
            Environment.GetEnvironmentVariable("KIMI_CODE_HOME"));
        return configured ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code");
    }

    public static async Task<KimiCodeCredential?> LoadAsync(
        KimiCodeUsageOptions options,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(ResolveHome(options), "credentials", "kimi-code.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var accessToken = root.String("access_token", "accessToken")?.Trim() ?? string.Empty;
        var refreshToken = root.String("refresh_token", "refreshToken")?.Trim() ?? string.Empty;
        DateTimeOffset? expiresAt = root.Double("expires_at", "expiresAt") is { } seconds && double.IsFinite(seconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(seconds * 1000)))
            : null;
        return new KimiCodeCredential(
            accessToken,
            refreshToken,
            expiresAt,
            root.Int64("expires_in", "expiresIn") ?? 0,
            root.String("scope") ?? string.Empty,
            root.String("token_type", "tokenType") ?? "Bearer");
    }

    public static string ResolveDeviceId(KimiCodeUsageOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DeviceId))
        {
            return options.DeviceId.Trim();
        }

        var path = Path.Combine(ResolveHome(options), "device_id");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        return Guid.NewGuid().ToString("D");
    }

    private static string? FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
