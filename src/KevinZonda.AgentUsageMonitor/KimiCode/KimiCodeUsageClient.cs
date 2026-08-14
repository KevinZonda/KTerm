using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using KevinZonda.AgentUsageMonitor.Internal;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

public sealed class KimiCodeUsageClient : IUsageClient
{
    private static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan DefaultRateWindow = TimeSpan.FromHours(5);
    private readonly HttpClient _httpClient;
    private readonly KimiCodeUsageOptions _options;

    public KimiCodeUsageClient(HttpClient httpClient, KimiCodeUsageOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new KimiCodeUsageOptions();
    }

    public UsageProvider Provider => UsageProvider.KimiCode;

    public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default) =>
        GetUsageAsync(_options, cancellationToken);

    public async Task<UsageSnapshot> GetUsageAsync(
        KimiCodeUsageOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Exception? apiFailure = null;

        if (options.Mode is KimiCodeUsageMode.Auto or KimiCodeUsageMode.ApiKey)
        {
            var apiKey = FirstNotEmpty(options.ApiKey, Environment.GetEnvironmentVariable("KIMI_CODE_API_KEY"));
            if (apiKey is not null)
            {
                try
                {
                    return await FetchAsync(apiKey, UsageSource.KimiCodeApiKey, options, false, cancellationToken);
                }
                catch (Exception exception) when (options.Mode == KimiCodeUsageMode.Auto && CanFallback(exception))
                {
                    apiFailure = exception;
                }
            }

            if (options.Mode == KimiCodeUsageMode.ApiKey)
            {
                throw new UsageException(UsageErrorCode.MissingCredential, "KIMI_CODE_API_KEY is not configured.");
            }
        }

        var credential = await KimiCodeCredentialStore.LoadAsync(options, cancellationToken);
        if (credential is null || string.IsNullOrWhiteSpace(credential.AccessToken))
        {
            Rethrow(apiFailure);
            throw new UsageException(
                UsageErrorCode.MissingCredential,
                "Kimi Code credentials were not found. Run Kimi Code login or configure KIMI_CODE_API_KEY.");
        }

        if (credential.ExpiresAt is null || credential.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            Rethrow(apiFailure);
            throw new UsageException(
                UsageErrorCode.InvalidCredential,
                "The Kimi Code CLI access token is expired. Log in with Kimi Code again.");
        }

        return await FetchAsync(
            credential.AccessToken,
            UsageSource.KimiCodeCliCredential,
            options,
            true,
            cancellationToken);
    }

    internal static Uri BuildUsageUri(Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new UsageException(
                UsageErrorCode.InvalidConfiguration,
                "Kimi Code BaseUri must be an absolute HTTPS URI without user information.");
        }

        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/coding/v1", StringComparison.OrdinalIgnoreCase))
        {
            path += "/usages";
        }
        else if (path.EndsWith("/coding", StringComparison.OrdinalIgnoreCase))
        {
            path += "/v1/usages";
        }
        else
        {
            path += "/coding/v1/usages";
        }

        builder.Path = path;
        return builder.Uri;
    }

    private async Task<UsageSnapshot> FetchAsync(
        string token,
        UsageSource source,
        KimiCodeUsageOptions options,
        bool addCliIdentity,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUsageUri(options.BaseUri));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(options.UserAgent);

        if (addCliIdentity)
        {
            AddCliIdentityHeaders(request, options);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UsageException(UsageErrorCode.InvalidCredential, "The Kimi Code credential was rejected.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UsageException(
                UsageErrorCode.RemoteError,
                $"Kimi Code usage API returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return Parse(data, source, DateTimeOffset.UtcNow);
        }
        catch (UsageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            throw new UsageException(UsageErrorCode.InvalidResponse, "Invalid Kimi Code usage response.", exception);
        }
    }

    internal static UsageSnapshot Parse(ReadOnlySpan<byte> data, UsageSource source, DateTimeOffset updatedAt)
    {
        using var document = JsonDocument.Parse(data.ToArray());
        var root = document.RootElement;
        var usage = root.Property("usage")
            ?? throw new UsageException(UsageErrorCode.InvalidResponse, "Kimi Code response does not contain usage.");
        var primary = ParseDetail(usage, "7-day usage", WeeklyWindow);

        UsageWindow? secondary = null;
        var limits = root.Property("limits");
        if (limits is { ValueKind: JsonValueKind.Array })
        {
            var first = limits.Value.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.Property("detail") is { } detail)
            {
                secondary = ParseDetail(detail, "Rate limit", ParseWindow(first.Property("window")) ?? DefaultRateWindow);
            }
        }

        return new UsageSnapshot(
            UsageProvider.KimiCode,
            source,
            primary,
            secondary,
            [],
            null,
            null,
            null,
            null,
            updatedAt);
    }

    private static UsageWindow ParseDetail(JsonElement detail, string name, TimeSpan window)
    {
        var limit = detail.Double("limit")
            ?? throw new UsageException(UsageErrorCode.InvalidResponse, $"Kimi Code {name} limit is missing.");
        if (limit <= 0)
        {
            throw new UsageException(UsageErrorCode.InvalidResponse, $"Kimi Code {name} limit must be positive.");
        }

        var used = detail.Double("used");
        if (used is null && detail.Double("remaining") is { } remaining && remaining >= 0 && remaining <= limit)
        {
            used = limit - remaining;
        }

        used ??= 0;
        var reset = detail.String("resetTime", "resetAt", "reset_time", "reset_at");
        DateTimeOffset? resetsAt = DateTimeOffset.TryParse(reset, out var parsedReset) ? parsedReset : null;
        return new UsageWindow(name, JsonHelpers.ClampPercent(used.Value / limit * 100), window, resetsAt, used, limit);
    }

    private static TimeSpan? ParseWindow(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var duration = value.Value.Int64("duration");
        if (duration is null or <= 0)
        {
            return null;
        }

        return value.Value.String("timeUnit", "time_unit") switch
        {
            "TIME_UNIT_MINUTE" => TimeSpan.FromMinutes(duration.Value),
            "TIME_UNIT_HOUR" => TimeSpan.FromHours(duration.Value),
            "TIME_UNIT_DAY" => TimeSpan.FromDays(duration.Value),
            _ => null,
        };
    }

    private static void AddCliIdentityHeaders(HttpRequestMessage request, KimiCodeUsageOptions options)
    {
        request.Headers.TryAddWithoutValidation("X-Msh-Platform", "kimi_code_cli");
        request.Headers.TryAddWithoutValidation("X-Msh-Version", "1.0");
        request.Headers.TryAddWithoutValidation("X-Msh-Device-Id", KimiCodeCredentialStore.ResolveDeviceId(options));
        request.Headers.TryAddWithoutValidation("X-Msh-Device-Name", Environment.MachineName);
        request.Headers.TryAddWithoutValidation("X-Msh-Os-Version", Environment.OSVersion.Version.ToString());
        request.Headers.TryAddWithoutValidation("X-Msh-Device-Model", $"{Environment.OSVersion.Platform} {RuntimeInformation.OSArchitecture}");
    }

    private static string? FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static bool CanFallback(Exception exception) => exception switch
    {
        HttpRequestException => true,
        UsageException usageException when usageException.Code is
            UsageErrorCode.InvalidCredential or
            UsageErrorCode.InvalidResponse or
            UsageErrorCode.RemoteError => true,
        _ => false,
    };

    private static void Rethrow(Exception? exception)
    {
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
