using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Protocols;

namespace MrWhoOidc.Cli.Services;

public static class CliServerConnection
{
    public static string ResolveServerUrlOrThrow(CliConfig config, string? explicitServer = null, string? profileName = null)
    {
        var normalizedExplicitServer = NormalizeServerUrl(explicitServer);
        if (!string.IsNullOrWhiteSpace(normalizedExplicitServer))
        {
            return normalizedExplicitServer;
        }

        ProfileConfig? profile;
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            if (!config.Profiles.TryGetValue(profileName, out profile))
            {
                throw new InvalidOperationException($"Profile '{profileName}' was not found.");
            }
        }
        else
        {
            profile = config.GetCurrentProfile();
        }

        var normalizedProfileServer = NormalizeServerUrl(profile?.ServerUrl);
        if (!string.IsNullOrWhiteSpace(normalizedProfileServer))
        {
            return normalizedProfileServer;
        }

        throw new InvalidOperationException("Server URL is required. Use --server or log in first so the current profile has a saved server.");
    }

    public static AuthenticatedConnection ResolveAuthenticatedConnectionOrThrow(CliConfig config, string? explicitServer = null, string? profileName = null)
    {
        var normalizedExplicitServer = NormalizeServerUrl(explicitServer);
        string resolvedProfileName;
        ProfileConfig profile;

        if (!string.IsNullOrWhiteSpace(profileName))
        {
            if (!config.Profiles.TryGetValue(profileName, out profile!))
            {
                throw new InvalidOperationException($"Profile '{profileName}' was not found.");
            }

            resolvedProfileName = profileName;
        }
        else if (!string.IsNullOrWhiteSpace(normalizedExplicitServer))
        {
            var matchingProfile = config.Profiles.FirstOrDefault(pair =>
                pair.Value.IsAuthenticated
                && string.Equals(NormalizeServerUrl(pair.Value.ServerUrl), normalizedExplicitServer, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(matchingProfile.Key))
            {
                throw new InvalidOperationException(
                    $"No authenticated profile is saved for {normalizedExplicitServer}. Log in first or provide --profile.");
            }

            resolvedProfileName = matchingProfile.Key;
            profile = matchingProfile.Value;
        }
        else
        {
            resolvedProfileName = config.CurrentProfile;
            if (!config.Profiles.TryGetValue(resolvedProfileName, out profile!))
            {
                throw new InvalidOperationException("No current profile is configured. Log in first or provide --profile.");
            }
        }

        var resolvedServer = NormalizeServerUrl(profile.ServerUrl);
        if (!string.IsNullOrWhiteSpace(normalizedExplicitServer))
        {
            resolvedServer = normalizedExplicitServer;
            var profileServer = NormalizeServerUrl(profile.ServerUrl);
            if (!string.IsNullOrWhiteSpace(profileServer)
                && !string.Equals(profileServer, normalizedExplicitServer, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Profile '{resolvedProfileName}' is bound to {profileServer}, not {normalizedExplicitServer}.");
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedServer))
        {
            throw new InvalidOperationException($"Profile '{resolvedProfileName}' does not have a saved server URL.");
        }

        if (!profile.IsAuthenticated)
        {
            throw new InvalidOperationException($"Profile '{resolvedProfileName}' is not authenticated. Run login first.");
        }

        var connection = new AuthenticatedConnection(resolvedProfileName, resolvedServer, profile);

        // Write server context to stderr so it never pollutes JSON on stdout
        Console.Error.WriteLine($"Server: {resolvedServer}  (profile: {resolvedProfileName})");

        return connection;
    }

    public static string NormalizeServerUrl(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return string.Empty;
        }

        return server.Trim().TrimEnd('/');
    }

    public static HttpClient CreateHttpClient(string server)
    {
        var handler = new HttpClientHandler();

        if (Uri.TryCreate(server, UriKind.Absolute, out var serverUri) && IsLoopbackHost(serverUri.Host))
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public static HttpClient CreateAuthenticatedHttpClient(AuthenticatedConnection connection, string accessToken)
    {
        var httpClient = CreateHttpClient(connection.ServerUrl);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return httpClient;
    }

    public static async Task<DiscoveryDocument> FetchDiscoveryAsync(HttpClient httpClient, string server)
    {
        var discoveryUrl = $"{server}/.well-known/openid-configuration";
        var discovery = await httpClient.GetFromJsonAsync<DiscoveryDocument>(discoveryUrl).ConfigureAwait(false);

        if (discovery is null)
        {
            throw new InvalidOperationException($"Discovery document was empty at {discoveryUrl}");
        }

        if (string.IsNullOrWhiteSpace(discovery.TokenEndpoint))
        {
            discovery.TokenEndpoint = $"{server}/token";
        }

        if (string.IsNullOrWhiteSpace(discovery.DeviceAuthorizationEndpoint))
        {
            discovery.DeviceAuthorizationEndpoint = $"{server}/device/authorize";
        }

        return discovery;
    }

    public static async Task<T?> ReadJsonOrThrowAsync<T>(HttpResponseMessage response, string responseLabel)
    {
        var payloadText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(payloadText))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payloadText, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
            var snippet = payloadText.Length > 240 ? payloadText[..240] + "..." : payloadText;
            throw new InvalidOperationException(
                $"The server returned a non-JSON {responseLabel} (HTTP {(int)response.StatusCode}, Content-Type: {contentType}). Body: {snippet}");
        }
    }

    public static async Task<string> GetValidAccessTokenAsync(CliConfig config, AuthenticatedConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Profile.AccessToken) && !connection.Profile.IsTokenExpired)
        {
            return connection.Profile.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(connection.Profile.RefreshToken))
        {
            throw new InvalidOperationException(
                $"Profile '{connection.ProfileName}' does not have a valid access token or refresh token. Run login again.");
        }

        using var httpClient = CreateHttpClient(connection.ServerUrl);
        var discovery = await FetchDiscoveryAsync(httpClient, connection.ServerUrl).ConfigureAwait(false);
        var refreshedToken = await RefreshAccessTokenAsync(
            httpClient,
            discovery.TokenEndpoint,
            connection.Profile.ClientId,
            connection.Profile.RefreshToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(refreshedToken.AccessToken))
        {
            throw new InvalidOperationException("Refresh token grant did not return an access token.");
        }

        connection.Profile.AccessToken = refreshedToken.AccessToken;
        connection.Profile.RefreshToken = refreshedToken.RefreshToken ?? connection.Profile.RefreshToken;
        connection.Profile.TokenExpiry = refreshedToken.ExpiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(refreshedToken.ExpiresIn)
            : null;
        connection.Profile.IsPlatformAdmin = DeterminePlatformAdmin(refreshedToken.AccessToken);
        connection.Profile.TokenIntrospectedAt = DateTimeOffset.UtcNow;
        config.SetProfile(connection.ProfileName, connection.Profile);
        await config.SaveAsync().ConfigureAwait(false);

        return refreshedToken.AccessToken;
    }

    public static string GetPlatformServerUrl(string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri))
        {
            return NormalizeServerUrl(server);
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2 && string.Equals(segments[0], "t", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri)
            {
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };
            return NormalizeServerUrl(builder.Uri.ToString());
        }

        return NormalizeServerUrl(server);
    }

    public static string CombineRelativePath(string server, string relativePath)
    {
        return $"{NormalizeServerUrl(server)}/{relativePath.TrimStart('/')}";
    }

    private static async Task<TokenResponse> RefreshAccessTokenAsync(
        HttpClient httpClient,
        string tokenEndpoint,
        string clientId,
        string refreshToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [OAuthConstants.Parameters.GrantType] = OAuthConstants.GrantTypes.RefreshToken,
            [OAuthConstants.Parameters.RefreshToken] = refreshToken,
            [OAuthConstants.Parameters.ClientId] = clientId
        });

        using var response = await httpClient.PostAsync(tokenEndpoint, content).ConfigureAwait(false);
        var payload = await ReadJsonOrThrowAsync<TokenResponse>(response, "refresh token response").ConfigureAwait(false);

        if (!response.IsSuccessStatusCode || payload is null)
        {
            var message = MapTokenErrorToUserMessage(payload?.Error, payload?.ErrorDescription, (int)response.StatusCode, clientId);
            throw new InvalidOperationException(message);
        }

        return payload;
    }

    private static string MapTokenErrorToUserMessage(string? error, string? errorDescription, int statusCode, string clientId)
    {
        // Prefer error_description when it's informative
        if (!string.IsNullOrWhiteSpace(errorDescription) &&
            !string.Equals(errorDescription, error, StringComparison.OrdinalIgnoreCase))
        {
            return errorDescription;
        }

        return error?.ToLowerInvariant() switch
        {
            "invalid_client" or "unknown client" =>
                $"The CLI client '{clientId}' is not recognized by the server. " +
                "CLI access may not be enabled for this tenant. " +
                "Enable it in the Admin UI (Settings → CLI Access), then run: mrwho-cli login",

            "invalid_grant" =>
                "Your session has expired. Please log in again: mrwho-cli login",

            "unauthorized_client" =>
                $"The CLI client '{clientId}' is not authorized for token refresh. " +
                "Re-enable CLI access in the Admin UI, then run: mrwho-cli login",

            _ => error ?? $"Token refresh failed (HTTP {statusCode})."
        };
    }

    public static string? ExtractTenantSlug(string? issuer)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri))
        {
            return null;
        }

        var segments = issuerUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (string.Equals(segments[index], "t", StringComparison.OrdinalIgnoreCase))
            {
                return segments[index + 1];
            }
        }

        return null;
    }

    public static string? BuildDefaultCliClientId(string? tenantSlug)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug))
        {
            return null;
        }

        return $"mrwho-cli-{tenantSlug.Trim().ToLowerInvariant()}";
    }

    public static bool DeterminePlatformAdmin(string accessToken)
    {
        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
            return jwt.Claims.Any(claim =>
                (string.Equals(claim.Type, "role", StringComparison.OrdinalIgnoreCase)
                || string.Equals(claim.Type, "roles", StringComparison.OrdinalIgnoreCase))
                && string.Equals(claim.Value, "platform-admin", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var ipAddress) && IPAddress.IsLoopback(ipAddress);
    }
}

public sealed record AuthenticatedConnection(string ProfileName, string ServerUrl, ProfileConfig Profile);

public sealed class DiscoveryDocument
{
    [JsonPropertyName("issuer")]
    public string? Issuer { get; set; }

    [JsonPropertyName("authorization_endpoint")]
    public string? AuthorizationEndpoint { get; set; }

    [JsonPropertyName("device_authorization_endpoint")]
    public string DeviceAuthorizationEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("userinfo_endpoint")]
    public string? UserInfoEndpoint { get; set; }

    [JsonPropertyName("jwks_uri")]
    public string? JwksUri { get; set; }

    [JsonPropertyName("revocation_endpoint")]
    public string? RevocationEndpoint { get; set; }

    [JsonPropertyName("grant_types_supported")]
    public string[] GrantTypesSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("response_types_supported")]
    public string[] ResponseTypesSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("scopes_supported")]
    public string[] ScopesSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public string[] TokenEndpointAuthMethodsSupported { get; set; } = Array.Empty<string>();

    [JsonPropertyName("mrwho_cli_client_id")]
    public string? CliClientId { get; set; }
}

public sealed class DeviceAuthorizationResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

public sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }

    [JsonPropertyName("interval")]
    public int? Interval { get; set; }
}
