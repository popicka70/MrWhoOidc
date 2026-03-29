using System.CommandLine;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
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

        var profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Name for the profile (codename: alphanumeric + hyphens). Auto-generated from tenant slug if omitted."
        };

        Options.Add(serverOption);
        Options.Add(clientIdOption);
        Options.Add(profileOption);

        this.SetSafeAction(async parseResult =>
        {
            var server = parseResult.GetValue(serverOption);
            var clientId = parseResult.GetValue(clientIdOption);
            var profile = parseResult.GetValue(profileOption);
            await HandleAsync(server, clientId, profile);
        });
    }

    private static async Task HandleAsync(string? server, string? clientId, string? explicitProfile)
    {
        // Validate explicit profile name early
        if (!string.IsNullOrWhiteSpace(explicitProfile) && !CliConfig.IsValidProfileName(explicitProfile))
        {
            throw new InvalidOperationException(
                $"Invalid profile name '{explicitProfile}'. Use a codename (alphanumeric and hyphens, e.g. 'my-prod') or the server URL.");
        }

        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        var normalizedServer = CliServerConnection.ResolveServerUrlOrThrow(config, server);

        AnsiConsole.MarkupLine($"[cyan]Connecting to[/] {Markup.Escape(normalizedServer)}");

        using var httpClient = CliServerConnection.CreateHttpClient(normalizedServer);
        var discovery = await CliServerConnection.FetchDiscoveryAsync(httpClient, normalizedServer).ConfigureAwait(false);
        var tenantSlug = CliServerConnection.ExtractTenantSlug(discovery.Issuer ?? normalizedServer);
        var matchingProfile = config.Profiles
            .FirstOrDefault(pair => string.Equals(CliServerConnection.NormalizeServerUrl(pair.Value.ServerUrl), normalizedServer, StringComparison.OrdinalIgnoreCase))
            .Value;

        var resolvedClientId = clientId
            ?? discovery.CliClientId
            ?? matchingProfile?.ClientId
            ?? CliServerConnection.BuildDefaultCliClientId(tenantSlug);

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

        var profileName = ResolveProfileName(config, normalizedServer, tenantSlug, explicitProfile);
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
            IsPlatformAdmin = CliServerConnection.DeterminePlatformAdmin(tokenResponse.AccessToken),
            TokenIntrospectedAt = DateTimeOffset.UtcNow
        });
        await config.SaveAsync().ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[green]Login successful.[/] Profile [bold]{Markup.Escape(profileName)}[/] saved to {Markup.Escape(CliConfig.GetConfigFilePath())}");
        AnsiConsole.MarkupLine($"[dim]Client ID:[/] {Markup.Escape(resolvedClientId)}");
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
        var payload = await CliServerConnection.ReadJsonOrThrowAsync<DeviceAuthorizationResponse>(response, "device authorization response").ConfigureAwait(false);

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
            var tokenResponse = await CliServerConnection.ReadJsonOrThrowAsync<TokenResponse>(response, "token response").ConfigureAwait(false);

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

    private static string ResolveProfileName(CliConfig config, string serverUrl, string? tenantSlug, string? explicitProfile)
    {
        // If an explicit profile name was provided, use it (already validated)
        if (!string.IsNullOrWhiteSpace(explicitProfile))
        {
            // If the name is already taken, verify it points to the same server
            if (config.Profiles.TryGetValue(explicitProfile, out var existingProfile))
            {
                var existingServer = CliServerConnection.NormalizeServerUrl(existingProfile.ServerUrl);
                if (!string.Equals(existingServer, serverUrl, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Profile '{explicitProfile}' is already bound to {existingServer}. Choose a different name or remove it first.");
                }
            }

            return explicitProfile;
        }

        // Auto-generate: look for an existing profile targeting the same server
        var existing = config.Profiles.FirstOrDefault(pair =>
            string.Equals(CliServerConnection.NormalizeServerUrl(pair.Value.ServerUrl), serverUrl, StringComparison.OrdinalIgnoreCase));

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
}
