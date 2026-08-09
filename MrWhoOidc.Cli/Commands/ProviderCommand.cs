using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Manages identity providers (upstream OIDC/SAML IdPs) within the current tenant or at platform scope.
/// </summary>
public sealed class ProviderCommand : Command
{
    public ProviderCommand() : base("provider", "Manage external identity providers, claim mappings, and keys")
    {
        Subcommands.Add(new ProviderListCommand());
        Subcommands.Add(new ProviderGetCommand());
        Subcommands.Add(new ProviderCreateCommand());
        Subcommands.Add(new ProviderUpdateCommand());
        Subcommands.Add(new ProviderDeleteCommand());
        Subcommands.Add(new ProviderClaimMappingCommand());
        Subcommands.Add(new ProviderKeyCommand());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // provider list
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ProviderListCommand : Command
    {
        public ProviderListCommand() : base("list", "List identity providers for the current tenant")
        {
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var platformOption = new Option<bool>("--platform") { Description = "List platform sign-in identity providers" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(platformOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var platform = parseResult.GetValue(platformOption);
                var format = parseResult.GetValue(formatOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var apiConnection = ResolveProviderConnection(connection, platform);
                var providers = await CliAdminApiClient.GetListAsync<ProviderListItem>(config, apiConnection, ProviderEndpoint(platform)).ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(providers, SharedJsonOptions.IndentedOptions));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("ID")
                    .AddColumn("Name")
                    .AddColumn("Type")
                    .AddColumn("Enabled")
                    .AddColumn("Default");

                foreach (var p in providers)
                {
                    table.AddRow(
                        Markup.Escape(p.Id.ToString()),
                        Markup.Escape(p.Name ?? "-"),
                        Markup.Escape(p.Type ?? "-"),
                        p.Enabled ? "yes" : "no",
                        p.IsDefault ? "yes" : "no");
                }

                AnsiConsole.Write(table);
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // provider get <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ProviderGetCommand : Command
    {
        public ProviderGetCommand() : base("get", "Get details of a specific identity provider")
        {
            var idArg = new Argument<Guid>("id") { Description = "Provider ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var platformOption = new Option<bool>("--platform") { Description = "Get a platform sign-in identity provider" };

            Arguments.Add(idArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(platformOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var platform = parseResult.GetValue(platformOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var apiConnection = ResolveProviderConnection(connection, platform);
                var provider = await CliAdminApiClient.GetAsync<ProviderDetail>(config, apiConnection, $"{ProviderEndpoint(platform)}/{id}").ConfigureAwait(false);

                if (provider is null)
                {
                    AnsiConsole.MarkupLine("[red]Provider not found.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[bold]ID:[/]          {Markup.Escape(provider.Id.ToString())}");
                AnsiConsole.MarkupLine($"[bold]Name:[/]        {Markup.Escape(provider.Name ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Type:[/]        {Markup.Escape(provider.Type ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Enabled:[/]     {(provider.Enabled ? "yes" : "no")}");
                AnsiConsole.MarkupLine($"[bold]Default:[/]     {(provider.IsDefault ? "yes" : "no")}");
                AnsiConsole.MarkupLine($"[bold]Authority:[/]   {Markup.Escape(provider.Authority ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Client ID:[/]   {Markup.Escape(provider.ClientId ?? "-")}");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // provider create
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ProviderCreateCommand : Command
    {
        public ProviderCreateCommand() : base("create", "Create a new identity provider")
        {
            var nameOption = new Option<string?>("--name") { Description = "Provider display name" };
            var typeOption = new Option<string?>("--type") { Description = "Provider type: Oidc or Saml" };
            var authorityOption = new Option<string?>("--authority") { Description = "OIDC issuer/authority URL" };
            var clientIdOption = new Option<string?>("--client-id") { Description = "OAuth client_id for this provider" };
            var clientSecretOption = new Option<string?>("--client-secret") { Description = "OAuth client_secret for this provider" };
            var enabledOption = new Option<bool?>("--enabled") { Description = "Enable the provider (default: true)" };
            var isDefaultOption = new Option<bool?>("--is-default") { Description = "Set as the default provider" };
            var allowRegistrationOption = new Option<bool?>("--allow-registration") { Description = "Show this provider on public sign-in and registration pages in the default tenant" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var platformOption = new Option<bool>("--platform") { Description = "Create a platform sign-in identity provider" };

            Options.Add(nameOption);
            Options.Add(typeOption);
            Options.Add(authorityOption);
            Options.Add(clientIdOption);
            Options.Add(clientSecretOption);
            Options.Add(enabledOption);
            Options.Add(isDefaultOption);
            Options.Add(allowRegistrationOption);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(platformOption);

            this.SetSafeAction(async parseResult =>
            {
                var name = parseResult.GetValue(nameOption)
                    ?? throw new InvalidOperationException("--name is required.");
                var type = parseResult.GetValue(typeOption)
                    ?? throw new InvalidOperationException("--type is required (Oidc or Saml).");
                var authority = parseResult.GetValue(authorityOption);
                var clientId = parseResult.GetValue(clientIdOption);
                var clientSecret = parseResult.GetValue(clientSecretOption);
                var enabled = parseResult.GetValue(enabledOption);
                var isDefault = parseResult.GetValue(isDefaultOption);
                var allowRegistration = parseResult.GetValue(allowRegistrationOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var platform = parseResult.GetValue(platformOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var apiConnection = ResolveProviderConnection(connection, platform);
                var configJson = BuildOidcConfigJson(type, authority, clientId, clientSecret);

                var result = await CliAdminApiClient.PostAsync<ProviderCreatedResult>(
                    config, apiConnection, ProviderEndpoint(platform),
                    new { name, type, authority, clientId, clientSecret, configJson,
                          enabled = enabled ?? true,
                          isDefault = isDefault ?? false,
                          allowRegistration = allowRegistration ?? false }).ConfigureAwait(false);

                AnsiConsole.MarkupLine("[green]Provider created successfully.[/]");
                AnsiConsole.MarkupLine($"  [bold]ID:[/]   {Markup.Escape(result?.Id.ToString() ?? "-")}");
                AnsiConsole.MarkupLine($"  [bold]Name:[/] {Markup.Escape(result?.Name ?? name)}");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // provider update <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ProviderUpdateCommand : Command
    {
        public ProviderUpdateCommand() : base("update", "Update an existing identity provider")
        {
            var idArg = new Argument<Guid>("id") { Description = "Provider ID (GUID)" };
            var nameOption = new Option<string?>("--name") { Description = "New display name" };
            var enabledOption = new Option<bool?>("--enabled") { Description = "Enable or disable the provider" };
            var isDefaultOption = new Option<bool?>("--is-default") { Description = "Set as the default provider" };
            var authorityOption = new Option<string?>("--authority") { Description = "New OIDC authority URL" };
            var clientIdOption = new Option<string?>("--client-id") { Description = "New OAuth client_id" };
            var clientSecretOption = new Option<string?>("--client-secret") { Description = "New OAuth client_secret" };
            var allowRegistrationOption = new Option<bool?>("--allow-registration") { Description = "Show this provider on public sign-in and registration pages in the default tenant" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var platformOption = new Option<bool>("--platform") { Description = "Update a platform sign-in identity provider" };

            Arguments.Add(idArg);
            Options.Add(nameOption);
            Options.Add(enabledOption);
            Options.Add(isDefaultOption);
            Options.Add(authorityOption);
            Options.Add(clientIdOption);
            Options.Add(clientSecretOption);
            Options.Add(allowRegistrationOption);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(platformOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var name = parseResult.GetValue(nameOption);
                var enabled = parseResult.GetValue(enabledOption);
                var isDefault = parseResult.GetValue(isDefaultOption);
                var authority = parseResult.GetValue(authorityOption);
                var clientId = parseResult.GetValue(clientIdOption);
                var clientSecret = parseResult.GetValue(clientSecretOption);
                var allowRegistration = parseResult.GetValue(allowRegistrationOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var platform = parseResult.GetValue(platformOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var apiConnection = ResolveProviderConnection(connection, platform);

                // GET current state so we can merge only user-supplied values
                // (the PUT endpoint expects the full entity including non-nullable bools).
                var current = await CliAdminApiClient.GetAsync<JsonObject>(
                    config, apiConnection, $"{ProviderEndpoint(platform)}/{id}").ConfigureAwait(false);
                if (current is null)
                    throw new InvalidOperationException($"Provider {id} not found.");

                if (name is not null) current["name"] = name;
                if (enabled.HasValue) current["enabled"] = enabled.Value;
                if (isDefault.HasValue) current["isDefault"] = isDefault.Value;
                if (authority is not null) current["authority"] = authority;
                if (clientId is not null) current["clientId"] = clientId;
                if (clientSecret is not null) current["clientSecret"] = clientSecret;
                if (allowRegistration.HasValue) current["allowRegistration"] = allowRegistration.Value;
                if (authority is not null || clientId is not null || clientSecret is not null)
                {
                    current["configJson"] = MergeOidcConfigJson(current, authority, clientId, clientSecret);
                }

                await CliAdminApiClient.PutAsync(
                    config, apiConnection, $"{ProviderEndpoint(platform)}/{id}", current).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Provider {id} updated successfully.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // provider delete <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ProviderDeleteCommand : Command
    {
        public ProviderDeleteCommand() : base("delete", "Delete an identity provider")
        {
            var idArg = new Argument<Guid>("id") { Description = "Provider ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };
            var platformOption = new Option<bool>("--platform") { Description = "Delete a platform sign-in identity provider" };

            Arguments.Add(idArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(confirmOption);
            Options.Add(platformOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var confirm = parseResult.GetValue(confirmOption);
                var platform = parseResult.GetValue(platformOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Delete provider {id}? This cannot be undone.", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var apiConnection = ResolveProviderConnection(connection, platform);
                await CliAdminApiClient.DeleteAsync(config, apiConnection, $"{ProviderEndpoint(platform)}/{id}").ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]Provider {id} deleted.[/]");
            });
        }
    }

    private static AuthenticatedConnection ResolveProviderConnection(AuthenticatedConnection connection, bool platform)
    {
        if (!platform)
        {
            return connection;
        }

        if (!connection.Profile.IsPlatformAdmin)
        {
            throw new InvalidOperationException("Platform provider commands require a platform-admin profile.");
        }

        var platformServer = CliServerConnection.GetPlatformServerUrl(connection.ServerUrl);
        return new AuthenticatedConnection(connection.ProfileName, platformServer, connection.Profile);
    }

    private static string ProviderEndpoint(bool platform) => platform ? "platform-admin/api/providers" : "admin/api/providers";

    private static string? BuildOidcConfigJson(string type, string? authority, string? clientId, string? clientSecret)
    {
        if (!string.Equals(type, "Oidc", StringComparison.OrdinalIgnoreCase) && type != "0")
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        return JsonSerializer.Serialize(new
        {
            Authority = authority.Trim().TrimEnd('/'),
            ClientId = clientId.Trim(),
            ClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret,
            ResponseType = "code",
            Scopes = new[] { "openid", "profile", "email" },
            UsePKCE = true,
            UseJAR = false,
            UsePAR = false,
            ClockSkewSeconds = 120,
            TokenValidation = new
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true
            },
            BackChannelLogout = true,
            ExtraAuthParams = new Dictionary<string, string>()
        }, SharedJsonOptions.IndentedOptions);
    }

    private static string MergeOidcConfigJson(JsonObject current, string? authority, string? clientId, string? clientSecret)
    {
        JsonObject config;
        var existing = current["configJson"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            config = JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
        }
        else
        {
            config = new JsonObject
            {
                ["ResponseType"] = "code",
                ["Scopes"] = new JsonArray("openid", "profile", "email"),
                ["UsePKCE"] = true,
                ["UseJAR"] = false,
                ["UsePAR"] = false,
                ["ClockSkewSeconds"] = 120,
                ["TokenValidation"] = new JsonObject
                {
                    ["ValidateIssuer"] = true,
                    ["ValidateAudience"] = true,
                    ["ValidateLifetime"] = true
                },
                ["BackChannelLogout"] = true,
                ["ExtraAuthParams"] = new JsonObject()
            };
        }

        if (!string.IsNullOrWhiteSpace(authority)) config["Authority"] = authority.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(clientId)) config["ClientId"] = clientId.Trim();
        if (!string.IsNullOrWhiteSpace(clientSecret)) config["ClientSecret"] = clientSecret;

        return config.ToJsonString(SharedJsonOptions.IndentedOptions);
    }
}

// ── Provider claim-mapping subcommands ───────────────────────────────────────

internal sealed class ProviderClaimMappingCommand : Command
{
    public ProviderClaimMappingCommand() : base("claim-mapping", "Manage provider claim mappings")
    {
        Subcommands.Add(new ClaimMappingListCommand());
        Subcommands.Add(new ClaimMappingCreateCommand());
        Subcommands.Add(new ClaimMappingUpdateCommand());
        Subcommands.Add(new ClaimMappingDeleteCommand());
    }

    private sealed class ClaimMappingListCommand : Command
    {
        public ClaimMappingListCommand() : base("list", "List claim mappings for a provider")
        {
            var providerIdArg = new Argument<Guid>("provider-id") { Description = "Provider ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Arguments.Add(providerIdArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var providerId = parseResult.GetValue(providerIdArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var format = parseResult.GetValue(formatOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var mappings = await CliAdminApiClient.GetListAsync<ClaimMappingItem>(
                    config, connection, $"admin/api/providers/{providerId}/claim-mappings").ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(mappings, SharedJsonOptions.IndentedOptions));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("ID")
                    .AddColumn("External Claim")
                    .AddColumn("Local Claim")
                    .AddColumn("Transform");

                foreach (var m in mappings)
                {
                    table.AddRow(
                        Markup.Escape(m.Id.ToString()),
                        Markup.Escape(m.ExternalClaim ?? "-"),
                        Markup.Escape(m.LocalClaim ?? "-"),
                        Markup.Escape(m.Transform ?? "-"));
                }

                AnsiConsole.Write(table);
            });
        }
    }

    private sealed class ClaimMappingCreateCommand : Command
    {
        public ClaimMappingCreateCommand() : base("create", "Create a new claim mapping for a provider")
        {
            var providerIdArg = new Argument<Guid>("provider-id") { Description = "Provider ID (GUID)" };
            var externalOption = new Option<string?>("--external-claim") { Description = "External claim name" };
            var localOption = new Option<string?>("--local-claim") { Description = "Local claim name" };
            var transformOption = new Option<string?>("--transform") { Description = "Optional transform expression" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(providerIdArg);
            Options.Add(externalOption);
            Options.Add(localOption);
            Options.Add(transformOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var providerId = parseResult.GetValue(providerIdArg);
                var externalClaim = parseResult.GetValue(externalOption)
                    ?? throw new InvalidOperationException("--external-claim is required.");
                var localClaim = parseResult.GetValue(localOption)
                    ?? throw new InvalidOperationException("--local-claim is required.");
                var transform = parseResult.GetValue(transformOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var result = await CliAdminApiClient.PostAsync<ClaimMappingItem>(
                    config, connection, $"admin/api/providers/{providerId}/claim-mappings",
                    new { externalClaim, localClaim, transform }).ConfigureAwait(false);

                AnsiConsole.MarkupLine("[green]Claim mapping created.[/]");
                AnsiConsole.MarkupLine($"  [bold]ID:[/] {Markup.Escape(result?.Id.ToString() ?? "-")}");
            });
        }
    }

    private sealed class ClaimMappingUpdateCommand : Command
    {
        public ClaimMappingUpdateCommand() : base("update", "Update an existing claim mapping")
        {
            var providerIdArg = new Argument<Guid>("provider-id") { Description = "Provider ID (GUID)" };
            var mappingIdArg = new Argument<Guid>("mapping-id") { Description = "Mapping ID (GUID)" };
            var localOption = new Option<string?>("--local-claim") { Description = "New local claim name" };
            var transformOption = new Option<string?>("--transform") { Description = "New transform expression" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(providerIdArg);
            Arguments.Add(mappingIdArg);
            Options.Add(localOption);
            Options.Add(transformOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var providerId = parseResult.GetValue(providerIdArg);
                var mappingId = parseResult.GetValue(mappingIdArg);
                var localClaim = parseResult.GetValue(localOption);
                var transform = parseResult.GetValue(transformOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                // GET current mapping so we can merge — the PUT endpoint requires
                // both externalClaim and localClaim to be non-empty.
                var mappings = await CliAdminApiClient.GetListAsync<JsonObject>(
                    config, connection, $"admin/api/providers/{providerId}/claim-mappings").ConfigureAwait(false);
                var current = mappings.FirstOrDefault(m =>
                    m["id"]?.ToString().Equals(mappingId.ToString(), StringComparison.OrdinalIgnoreCase) == true);
                if (current is null)
                    throw new InvalidOperationException($"Claim mapping {mappingId} not found.");

                if (localClaim is not null) current["localClaim"] = localClaim;
                if (transform is not null) current["transform"] = transform;

                await CliAdminApiClient.PutAsync(
                    config, connection, $"admin/api/providers/{providerId}/claim-mappings/{mappingId}",
                    current).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Claim mapping {mappingId} updated.[/]");
            });
        }
    }

    private sealed class ClaimMappingDeleteCommand : Command
    {
        public ClaimMappingDeleteCommand() : base("delete", "Delete a claim mapping")
        {
            var providerIdArg = new Argument<Guid>("provider-id") { Description = "Provider ID (GUID)" };
            var mappingIdArg = new Argument<Guid>("mapping-id") { Description = "Mapping ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };

            Arguments.Add(providerIdArg);
            Arguments.Add(mappingIdArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(confirmOption);

            this.SetSafeAction(async parseResult =>
            {
                var providerId = parseResult.GetValue(providerIdArg);
                var mappingId = parseResult.GetValue(mappingIdArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var confirm = parseResult.GetValue(confirmOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Delete claim mapping {mappingId}?", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(
                    config, connection, $"admin/api/providers/{providerId}/claim-mappings/{mappingId}").ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Claim mapping {mappingId} deleted.[/]");
            });
        }
    }
}

// ── Provider key subcommands ─────────────────────────────────────────────────

internal sealed class ProviderKeyCommand : Command
{
    public ProviderKeyCommand() : base("key", "Manage provider signing keys")
    {
        Subcommands.Add(new KeyListCommand());
        Subcommands.Add(new KeyAddCommand());
        Subcommands.Add(new KeyUpdateCommand());
        Subcommands.Add(new KeyDeleteCommand());
    }

    private sealed class KeyListCommand : Command
    {
        public KeyListCommand() : base("list", "List keys for a provider")
        {
            var providerIdArg = new Argument<Guid>("provider-id") { Description = "Provider ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Arguments.Add(providerIdArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var providerId = parseResult.GetValue(providerIdArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var format = parseResult.GetValue(formatOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var keys = await CliAdminApiClient.GetListAsync<ProviderKeyItem>(
                    config, connection, $"admin/api/providers/{providerId}/keys").ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(keys, SharedJsonOptions.IndentedOptions));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("ID")
                    .AddColumn("Kid")
                    .AddColumn("Purpose")
                    .AddColumn("Active")
                    .AddColumn("Publishable");

                foreach (var k in keys)
                {
                    table.AddRow(
                        Markup.Escape(k.Id.ToString()),
                        Markup.Escape(k.Kid ?? "-"),
                        Markup.Escape(k.Purpose ?? "-"),
                        k.Active ? "yes" : "no",
                        k.Publishable ? "yes" : "no");
                }

                AnsiConsole.Write(table);
            });
        }
    }

    private sealed class KeyAddCommand : Command
    {
        public KeyAddCommand() : base("add", "Add a key to a provider")
        {
            var providerIdArg = new Argument<Guid>("provider-id") { Description = "Provider ID (GUID)" };
            var jwkFileOption = new Option<string?>("--jwk-file") { Description = "Path to a JWK JSON file" };
            var activeOption = new Option<bool?>("--active") { Description = "Set the key as active (deactivates others)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(providerIdArg);
            Options.Add(jwkFileOption);
            Options.Add(activeOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var providerId = parseResult.GetValue(providerIdArg);
                var jwkFile = parseResult.GetValue(jwkFileOption)
                    ?? throw new InvalidOperationException("--jwk-file is required.");
                var active = parseResult.GetValue(activeOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                if (!File.Exists(jwkFile))
                    throw new InvalidOperationException($"File not found: {jwkFile}");

                var jwkJson = await File.ReadAllTextAsync(jwkFile).ConfigureAwait(false);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var result = await CliAdminApiClient.PostAsync<ProviderKeyItem>(
                    config, connection, $"admin/api/providers/{providerId}/keys",
                    new { jwk = jwkJson, active }).ConfigureAwait(false);

                AnsiConsole.MarkupLine("[green]Key added.[/]");
                AnsiConsole.MarkupLine($"  [bold]ID:[/] {Markup.Escape(result?.Id.ToString() ?? "-")}");
            });
        }
    }

    private sealed class KeyUpdateCommand : Command
    {
        public KeyUpdateCommand() : base("update", "Update a provider key")
        {
            var providerIdArg = new Argument<Guid>("provider-id") { Description = "Provider ID (GUID)" };
            var keyIdArg = new Argument<Guid>("key-id") { Description = "Key ID (GUID)" };
            var activeOption = new Option<bool?>("--active") { Description = "Set key active state" };
            var publishableOption = new Option<bool?>("--publishable") { Description = "Set key publishable state" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(providerIdArg);
            Arguments.Add(keyIdArg);
            Options.Add(activeOption);
            Options.Add(publishableOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var providerId = parseResult.GetValue(providerIdArg);
                var keyId = parseResult.GetValue(keyIdArg);
                var active = parseResult.GetValue(activeOption);
                var publishable = parseResult.GetValue(publishableOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PutAsync(
                    config, connection, $"admin/api/providers/{providerId}/keys/{keyId}",
                    new { active, publishable }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Key {keyId} updated.[/]");
            });
        }
    }

    private sealed class KeyDeleteCommand : Command
    {
        public KeyDeleteCommand() : base("delete", "Delete a provider key")
        {
            var providerIdArg = new Argument<Guid>("provider-id") { Description = "Provider ID (GUID)" };
            var keyIdArg = new Argument<Guid>("key-id") { Description = "Key ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };

            Arguments.Add(providerIdArg);
            Arguments.Add(keyIdArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(confirmOption);

            this.SetSafeAction(async parseResult =>
            {
                var providerId = parseResult.GetValue(providerIdArg);
                var keyId = parseResult.GetValue(keyIdArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var confirm = parseResult.GetValue(confirmOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Delete key {keyId}?", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(
                    config, connection, $"admin/api/providers/{providerId}/keys/{keyId}").ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Key {keyId} deleted.[/]");
            });
        }
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public sealed class ProviderListItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }
}

public sealed class ProviderDetail
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("authority")]
    public string? Authority { get; set; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    [JsonPropertyName("allowRegistration")]
    public bool AllowRegistration { get; set; }
}

public sealed class ProviderCreatedResult
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class ClaimMappingItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("externalClaim")]
    public string? ExternalClaim { get; set; }

    [JsonPropertyName("localClaim")]
    public string? LocalClaim { get; set; }

    [JsonPropertyName("transform")]
    public string? Transform { get; set; }
}

public sealed class ProviderKeyItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("kid")]
    public string? Kid { get; set; }

    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("publishable")]
    public bool Publishable { get; set; }
}
