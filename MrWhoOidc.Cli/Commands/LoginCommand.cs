using System.CommandLine;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Auth.Protocols;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Login command - authenticates user via device code flow and stores tokens.
/// </summary>
public sealed class LoginCommand : Command
{
    private const string DefaultScope = "openid profile email roles tenants offline_access";

    public LoginCommand() : base("login", "Authenticate with the OIDC server")
    {
        var serverOption = new Option<string?>("--server", "-s")
        {
            Description = "OIDC server URL (e.g., https://auth.example.com or https://host/t/tenant)"
        };
        
        var clientIdOption = new Option<string?>("--client-id", "-c")
        {
            Description = "Client ID override. If omitted, the CLI client ID is discovered automatically."
        };

        Options.Add(serverOption);
        Options.Add(clientIdOption);

        this.SetAction(async parseResult =>
        {
            var server = parseResult.GetValue(serverOption);
            var clientId = parseResult.GetValue(clientIdOption);
            await HandleAsync(server, clientId);
        });
    }

    private static async Task HandleAsync(string? server, string? clientId)
    {
        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        var currentProfile = config.GetCurrentProfile();
        var normalizedServer = NormalizeServerUrl(server ?? currentProfile?.ServerUrl);

        if (string.IsNullOrWhiteSpace(normalizedServer))
        {
            throw new InvalidOperationException("Server URL is required. Use --server for first-time login.");
        }

        AnsiConsole.MarkupLine($"[cyan]Connecting to[/] {Markup.Escape(normalizedServer)}");

        using var httpClient = CreateHttpClient(normalizedServer);
        var discovery = await FetchDiscoveryAsync(httpClient, normalizedServer).ConfigureAwait(false);
        var tenantSlug = ExtractTenantSlug(discovery.Issuer ?? normalizedServer);
        var matchingProfile = config.Profiles
            .FirstOrDefault(pair => string.Equals(NormalizeServerUrl(pair.Value.ServerUrl), normalizedServer, StringComparison.OrdinalIgnoreCase))
            .Value;

        var resolvedClientId = clientId
            ?? discovery.CliClientId
            ?? matchingProfile?.ClientId
            ?? BuildDefaultCliClientId(tenantSlug);

        if (string.IsNullOrWhiteSpace(resolvedClientId))
        {
            throw new InvalidOperationException(
                "CLI client ID could not be determined automatically. Enable CLI access for the tenant in Admin > Settings or provide --client-id.");
        }

        var deviceAuthorization = await RequestDeviceAuthorizationAsync(
            httpClient,
            discovery.DeviceAuthorizationEndpoint,
            resolvedClientId,
            DefaultScope).ConfigureAwait(false);

        AnsiConsole.Write(new Rule("Device Login").LeftJustified());
        AnsiConsole.MarkupLine($"[green]User code:[/] [bold]{Markup.Escape(deviceAuthorization.UserCode)}[/]");

        if (!string.IsNullOrWhiteSpace(deviceAuthorization.VerificationUriComplete))
        {
            AnsiConsole.MarkupLine("Open this URL in your browser:");
            AnsiConsole.MarkupLine($"[link]{Markup.Escape(deviceAuthorization.VerificationUriComplete)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"Open {Markup.Escape(deviceAuthorization.VerificationUri)} and enter code [bold]{Markup.Escape(deviceAuthorization.UserCode)}[/].");
        }

        AnsiConsole.MarkupLine("Waiting for device authorization to complete...");

        var tokenResponse = await PollForTokenAsync(
            httpClient,
            discovery.TokenEndpoint,
            resolvedClientId,
            deviceAuthorization).ConfigureAwait(false);

        var profileName = ResolveProfileName(config, normalizedServer, tenantSlug);
        DateTimeOffset? tokenExpiry = tokenResponse.ExpiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn)
            : null;

        config.SetProfile(profileName, new ProfileConfig
        {
            ServerUrl = normalizedServer,
            ClientId = resolvedClientId,
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken,
            TokenExpiry = tokenExpiry,
            TenantSlug = tenantSlug,
            IsPlatformAdmin = DeterminePlatformAdmin(tokenResponse.AccessToken),
            TokenIntrospectedAt = DateTimeOffset.UtcNow
        });
        await config.SaveAsync().ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[green]Login successful.[/] Profile [bold]{Markup.Escape(profileName)}[/] saved to {Markup.Escape(CliConfig.GetConfigFilePath())}");
        AnsiConsole.MarkupLine($"[dim]Client ID:[/] {Markup.Escape(resolvedClientId)}");
    }

    private static string NormalizeServerUrl(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return string.Empty;
        }

        return server.Trim().TrimEnd('/');
    }

    private static HttpClient CreateHttpClient(string server)
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

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var ipAddress) && IPAddress.IsLoopback(ipAddress);
    }

    private static async Task<DiscoveryDocument> FetchDiscoveryAsync(HttpClient httpClient, string server)
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

    private static string? BuildDefaultCliClientId(string? tenantSlug)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug))
        {
            return null;
        }

        return $"mrwho-cli-{tenantSlug.Trim().ToLowerInvariant()}";
    }

    private static async Task<DeviceAuthorizationResponse> RequestDeviceAuthorizationAsync(
        HttpClient httpClient,
        string endpoint,
        string clientId,
        string scope)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [OAuthConstants.Parameters.ClientId] = clientId,
            [OAuthConstants.Parameters.Scope] = scope
        });

        using var response = await httpClient.PostAsync(endpoint, content).ConfigureAwait(false);
        var payload = await ReadJsonOrThrowAsync<DeviceAuthorizationResponse>(response, "device authorization response").ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(payload?.ErrorDescription ?? payload?.Error ?? $"Device authorization failed with HTTP {(int)response.StatusCode}.");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceCode) || string.IsNullOrWhiteSpace(payload.UserCode))
        {
            throw new InvalidOperationException("The device authorization response was incomplete.");
        }

        return payload;
    }

    private static async Task<TokenResponse> PollForTokenAsync(
        HttpClient httpClient,
        string tokenEndpoint,
        string clientId,
        DeviceAuthorizationResponse deviceAuthorization)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceAuthorization.ExpiresIn > 0 ? deviceAuthorization.ExpiresIn : 600);
        var pollIntervalSeconds = Math.Max(deviceAuthorization.Interval, 1);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds)).ConfigureAwait(false);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [OAuthConstants.Parameters.GrantType] = OAuthConstants.GrantTypes.DeviceCode,
                [OAuthConstants.Parameters.DeviceCode] = deviceAuthorization.DeviceCode,
                [OAuthConstants.Parameters.ClientId] = clientId
            });

            using var response = await httpClient.PostAsync(tokenEndpoint, content).ConfigureAwait(false);
            var tokenResponse = await ReadJsonOrThrowAsync<TokenResponse>(response, "token response").ConfigureAwait(false);

            if (response.IsSuccessStatusCode && tokenResponse is not null && !string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                return tokenResponse;
            }

            if (tokenResponse is null)
            {
                throw new InvalidOperationException($"Token endpoint returned HTTP {(int)response.StatusCode} without a JSON payload.");
            }

            switch (tokenResponse.Error)
            {
                case OAuthConstants.ErrorCodes.AuthorizationPending:
                    continue;
                case OAuthConstants.ErrorCodes.SlowDown:
                    pollIntervalSeconds = Math.Max(tokenResponse.Interval ?? (pollIntervalSeconds + 5), pollIntervalSeconds + 1);
                    continue;
                case OAuthConstants.ErrorCodes.AccessDenied:
                    throw new InvalidOperationException(tokenResponse.ErrorDescription ?? "The authorization request was denied.");
                case OAuthConstants.ErrorCodes.ExpiredToken:
                    throw new InvalidOperationException(tokenResponse.ErrorDescription ?? "The device code expired before authorization completed.");
                default:
                    throw new InvalidOperationException(tokenResponse.ErrorDescription ?? tokenResponse.Error ?? $"Token request failed with HTTP {(int)response.StatusCode}.");
            }
        }

        throw new TimeoutException("Timed out waiting for device authorization to complete.");
    }

    private static async Task<T?> ReadJsonOrThrowAsync<T>(HttpResponseMessage response, string responseLabel)
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

    private static string? ExtractTenantSlug(string? issuer)
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

    private static string ResolveProfileName(CliConfig config, string serverUrl, string? tenantSlug)
    {
        var existing = config.Profiles.FirstOrDefault(pair =>
            string.Equals(NormalizeServerUrl(pair.Value.ServerUrl), serverUrl, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(existing.Key))
        {
            return existing.Key;
        }

        var baseName = !string.IsNullOrWhiteSpace(tenantSlug)
            ? tenantSlug
            : new Uri(serverUrl).Host.Replace('.', '-');

        if (!config.Profiles.ContainsKey(baseName))
        {
            return baseName;
        }

        var suffix = 2;
        while (config.Profiles.ContainsKey($"{baseName}-{suffix}"))
        {
            suffix++;
        }

        return $"{baseName}-{suffix}";
    }

    private static bool DeterminePlatformAdmin(string accessToken)
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

    private sealed class DiscoveryDocument
    {
        [JsonPropertyName("issuer")]
        public string? Issuer { get; set; }

        [JsonPropertyName("device_authorization_endpoint")]
        public string DeviceAuthorizationEndpoint { get; set; } = string.Empty;

        [JsonPropertyName("token_endpoint")]
        public string TokenEndpoint { get; set; } = string.Empty;

        [JsonPropertyName("mrwho_cli_client_id")]
        public string? CliClientId { get; set; }
    }

    private sealed class DeviceAuthorizationResponse
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

    private sealed class TokenResponse
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
}
