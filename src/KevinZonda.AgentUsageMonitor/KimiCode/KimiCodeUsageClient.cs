using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

public sealed class KimiCodeUsageClient : IUsageClient
{
    private readonly HttpClient _httpClient;
    private readonly KimiCodeUsageOptions _options;
    private readonly KimiCodeOAuthClient _oauthClient;
    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private KimiCodeCredential? _runtimeCredential;
    private string? _runtimeCredentialHome;

    public KimiCodeUsageClient(HttpClient httpClient, KimiCodeUsageOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new KimiCodeUsageOptions();
        _oauthClient = new KimiCodeOAuthClient(_httpClient);
    }

    public UsageProvider Provider => UsageProvider.KimiCode;

    public bool AutoRenewToken => _options.AutoRenewToken;

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

        var credential = await GetCliCredentialAsync(options, cancellationToken);
        if (credential is null || string.IsNullOrWhiteSpace(credential.AccessToken))
        {
            Rethrow(apiFailure);
            throw new UsageException(
                UsageErrorCode.MissingCredential,
                "Kimi Code credentials were not found. Run Kimi Code login or configure KIMI_CODE_API_KEY.");
        }

        if (!options.AutoRenewToken
            && (credential.ExpiresAt is null || credential.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1)))
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

    private async Task<KimiCodeCredential?> GetCliCredentialAsync(
        KimiCodeUsageOptions options,
        CancellationToken cancellationToken)
    {
        await _credentialGate.WaitAsync(cancellationToken);
        try
        {
            var home = KimiCodeCredentialStore.ResolveHome(options);
            if (_runtimeCredential is null
                || !string.Equals(_runtimeCredentialHome, home, StringComparison.OrdinalIgnoreCase))
            {
                _runtimeCredential = await KimiCodeCredentialStore.LoadAsync(options, cancellationToken);
                _runtimeCredentialHome = home;
            }

            if (_runtimeCredential is null || !options.AutoRenewToken || !ShouldRefresh(_runtimeCredential))
            {
                return _runtimeCredential;
            }

            // Kimi Code may have refreshed its credential since this monitor
            // started. Prefer the newer disk snapshot, but never write either
            // the disk credential or our in-memory refresh result back.
            var diskCredential = await KimiCodeCredentialStore.LoadAsync(options, cancellationToken);
            if (diskCredential is not null && IsNewerCredential(diskCredential, _runtimeCredential))
            {
                _runtimeCredential = diskCredential;
            }

            if (ShouldRefresh(_runtimeCredential))
            {
                _runtimeCredential = await _oauthClient.RefreshAsync(
                    _runtimeCredential,
                    options,
                    cancellationToken);
            }

            return _runtimeCredential;
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    private static bool ShouldRefresh(KimiCodeCredential credential)
    {
        if (credential.ExpiresAt is null)
        {
            return false;
        }

        var threshold = TimeSpan.FromSeconds(Math.Max(300, credential.ExpiresIn / 2d));
        return credential.ExpiresAt.Value - DateTimeOffset.UtcNow < threshold;
    }

    private static bool IsNewerCredential(
        KimiCodeCredential candidate,
        KimiCodeCredential current) =>
        (!string.Equals(candidate.AccessToken, current.AccessToken, StringComparison.Ordinal)
            || !string.Equals(candidate.RefreshToken, current.RefreshToken, StringComparison.Ordinal))
        && (candidate.ExpiresAt ?? DateTimeOffset.MinValue) >= (current.ExpiresAt ?? DateTimeOffset.MinValue);

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
            KimiCodeRequestHeaders.AddCliIdentity(request, options);
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
            return KimiCodeUsageParser.Parse(data, source, DateTimeOffset.UtcNow);
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
