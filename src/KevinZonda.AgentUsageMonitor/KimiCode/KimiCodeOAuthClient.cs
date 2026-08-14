using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using KevinZonda.AgentUsageMonitor.Internal;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

internal sealed class KimiCodeOAuthClient(HttpClient httpClient)
{
    private const string ClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";

    internal async Task<KimiCodeCredential> RefreshAsync(
        KimiCodeCredential credential,
        KimiCodeUsageOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            throw new UsageException(
                UsageErrorCode.InvalidCredential,
                "The Kimi Code CLI credential has no refresh token.");
        }

        var endpoint = BuildRefreshUri(options.OAuthBaseUri);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd(options.UserAgent);
                KimiCodeRequestHeaders.AddCliIdentity(request, options);
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = credential.RefreshToken,
                });

                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return ParseRefreshResponse(data);
                }

                var errorCode = TryReadErrorCode(data);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    || string.Equals(errorCode, "invalid_grant", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UsageException(
                        UsageErrorCode.InvalidCredential,
                        "The Kimi Code refresh token was rejected. Log in with Kimi Code again.");
                }

                if (!IsRetryable(response.StatusCode) || attempt == 2)
                {
                    throw new UsageException(
                        UsageErrorCode.RemoteError,
                        $"Kimi Code token refresh returned HTTP {(int)response.StatusCode}.");
                }
            }
            catch (HttpRequestException exception)
            {
                if (attempt == 2)
                {
                    throw new UsageException(
                        UsageErrorCode.RemoteError,
                        "Unable to reach the Kimi Code OAuth service.",
                        exception);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
        }

        throw new UsageException(UsageErrorCode.RemoteError, "Kimi Code token refresh failed.");
    }

    internal static Uri BuildRefreshUri(Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri
            || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new UsageException(
                UsageErrorCode.InvalidConfiguration,
                "Kimi Code OAuthBaseUri must be an absolute HTTPS URI without user information.");
        }

        var builder = new UriBuilder(baseUri);
        builder.Path = $"{builder.Path.TrimEnd('/')}/api/oauth/token";
        return builder.Uri;
    }

    private static KimiCodeCredential ParseRefreshResponse(ReadOnlySpan<byte> data)
    {
        try
        {
            using var document = JsonDocument.Parse(data.ToArray());
            var root = document.RootElement;
            var accessToken = root.String("access_token", "accessToken")?.Trim();
            var refreshToken = root.String("refresh_token", "refreshToken")?.Trim();
            var expiresIn = root.Int64("expires_in", "expiresIn");
            if (string.IsNullOrEmpty(accessToken)
                || string.IsNullOrEmpty(refreshToken)
                || expiresIn is null or <= 0)
            {
                throw new UsageException(
                    UsageErrorCode.InvalidResponse,
                    "Kimi Code token refresh returned an incomplete credential.");
            }

            return new KimiCodeCredential(
                accessToken,
                refreshToken,
                DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value),
                expiresIn.Value,
                root.String("scope") ?? string.Empty,
                root.String("token_type", "tokenType") ?? "Bearer");
        }
        catch (JsonException exception)
        {
            throw new UsageException(
                UsageErrorCode.InvalidResponse,
                "Kimi Code token refresh returned invalid JSON.",
                exception);
        }
    }

    private static string? TryReadErrorCode(ReadOnlySpan<byte> data)
    {
        try
        {
            using var document = JsonDocument.Parse(data.ToArray());
            return document.RootElement.String("error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
}
