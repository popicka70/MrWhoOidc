using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

public sealed class ClientCommand : Command
{
    public ClientCommand() : base("client", "Manage OIDC clients")
    {
        Subcommands.Add(new ClientListCommand());
        Subcommands.Add(new ClientGetCommand());
        Subcommands.Add(new ClientCreateCommand());
        Subcommands.Add(new ClientUpdateCommand());
        Subcommands.Add(new ClientDeleteCommand());
        Subcommands.Add(new ClientSecretCommand());
        Subcommands.Add(new ClientProviderCommand());
        Subcommands.Add(new ClientScopeCommand());
        Subcommands.Add(new ClientValidateCommand());
        Subcommands.Add(new ClientRotateSecretCommand());
    }

    private sealed class ClientListCommand : Command
    {
        public ClientListCommand() : base("list", "List clients for the current tenant or across the platform")
        {
            var serverOption = new Option<string?>("--server") { Description = "Server URL (defaults to the saved profile server)" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var tenantOption = new Option<string?>("--tenant") { Description = "Tenant slug filter for platform-admin profiles" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format (table or json)",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(tenantOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var tenant = parseResult.GetValue(tenantOption);
                var format = parseResult.GetValue(formatOption);
                await HandleAsync(server, profile, tenant, format).ConfigureAwait(false);
            });
        }

        private static async Task HandleAsync(string? server, string? profileName, string? tenant, OutputFormat format)
        {
            var config = await CliConfig.LoadAsync().ConfigureAwait(false);
            var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profileName);

            var (resolvedConnection, path) = ResolveClientListTarget(connection, tenant);
            var clients = await CliAdminApiClient.GetListAsync<ClientListItem>(config, resolvedConnection, path).ConfigureAwait(false);

            if (format == OutputFormat.Json)
            {
                AnsiConsole.WriteLine(JsonSerializer.Serialize(clients, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Client ID")
                .AddColumn("Name")
                .AddColumn("Tenant")
                .AddColumn("Realm")
                .AddColumn("PAR")
                .AddColumn("PKCE")
                .AddColumn("System");

            foreach (var client in clients)
            {
                table.AddRow(
                    Markup.Escape(client.ClientId),
                    Markup.Escape(client.ClientName ?? "-"),
                    Markup.Escape(client.TenantSlug ?? "-"),
                    Markup.Escape(client.RealmName),
                    client.RequirePar ? "yes" : "no",
                    client.RequirePkce ? "yes" : "no",
                    client.IsSystemClient ? "yes" : "no");
            }

            AnsiConsole.Write(table);
        }

        private static (AuthenticatedConnection Connection, string Path) ResolveClientListTarget(AuthenticatedConnection connection, string? tenant)
        {
            if (connection.Profile.IsPlatformAdmin && (string.IsNullOrWhiteSpace(connection.Profile.TenantSlug) || !string.IsNullOrWhiteSpace(tenant)))
            {
                var platformServer = CliServerConnection.GetPlatformServerUrl(connection.ServerUrl);
                var path = string.IsNullOrWhiteSpace(tenant)
                    ? "platform-admin/api/clients"
                    : $"platform-admin/api/clients?tenant={Uri.EscapeDataString(tenant)}";
                return (new AuthenticatedConnection(connection.ProfileName, platformServer, connection.Profile), path);
            }

            if (!string.IsNullOrWhiteSpace(tenant) && !string.Equals(tenant, connection.Profile.TenantSlug, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The selected profile is tenant-scoped. Use a platform-admin profile to query a different tenant.");
            }

            return (connection, "admin/api/clients");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client get <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ClientGetCommand : Command
    {
        public ClientGetCommand() : base("get", "Get details of a specific client by internal ID (GUID)")
        {
            var idArg = new Argument<Guid>("id") { Description = "Client internal ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(idArg);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var result = await CliAdminApiClient.GetAsync<ClientDetail>(config, connection, $"admin/api/clients/{id}").ConfigureAwait(false);

                if (result is null)
                {
                    AnsiConsole.MarkupLine("[red]Client not found.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[bold]ID:[/]              {Markup.Escape(result.Id.ToString())}");
                AnsiConsole.MarkupLine($"[bold]Client ID:[/]       {Markup.Escape(result.ClientId)}");
                AnsiConsole.MarkupLine($"[bold]Client Name:[/]     {Markup.Escape(result.ClientName ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Realm:[/]           {Markup.Escape(result.RealmName ?? result.RealmId.ToString())}");
                AnsiConsole.MarkupLine($"[bold]Require PKCE:[/]    {(result.RequirePkce ? "yes" : "no")}");
                AnsiConsole.MarkupLine($"[bold]Require Consent:[/] {(result.RequireConsent ? "yes" : "no")}");
                AnsiConsole.MarkupLine($"[bold]Require PAR:[/]     {(result.RequirePar ? "yes" : "no")}");
                AnsiConsole.MarkupLine($"[bold]System Client:[/]   {(result.IsSystemClient ? "yes" : "no")}");
                AnsiConsole.MarkupLine($"[bold]Scope:[/]           {Markup.Escape(result.Scope ?? "-")}");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client create
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ClientCreateCommand : Command
    {
        public ClientCreateCommand() : base("create",
            "Create a new OIDC client. If --create-initial-secret is set, the secret is written to a file.")
        {
            var clientIdOption = new Option<string>("--client-id") { Description = "OAuth 2.0 client_id string" };
            var clientNameOption = new Option<string>("--client-name") { Description = "Human-readable name" };
            var realmIdOption = new Option<Guid?>("--realm-id") { Description = "Realm GUID to register the client in" };
            var pkceOption = new Option<bool?>("--require-pkce") { Description = "Require PKCE (default: true)" };
            var consentOption = new Option<bool?>("--require-consent") { Description = "Require user consent (default: true)" };
            var scopeOption = new Option<string?>("--scope") { Description = "Space-separated list of allowed scopes" };
            var grantTypesOption = new Option<string[]?>("--grant-types") { Description = "Allowed grant types (e.g. authorization_code, client_credentials)" };
            var redirectsOption = new Option<string[]?>("--redirect-uris") { Description = "Allowed login redirect URIs" };
            var logoutRedirectsOption = new Option<string[]?>("--logout-redirect-uris") { Description = "Allowed logout redirect URIs" };
            var createSecretOption = new Option<bool>("--create-initial-secret") { Description = "Generate and activate an initial client secret (written to file)" };
            var outputOption = new Option<string?>("--output") { Description = "File path for the credentials JSON (defaults to exports dir)" };
            var overwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite output file if it exists" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Options.Add(clientIdOption);
            Options.Add(clientNameOption);
            Options.Add(realmIdOption);
            Options.Add(pkceOption);
            Options.Add(consentOption);
            Options.Add(scopeOption);
            Options.Add(grantTypesOption);
            Options.Add(redirectsOption);
            Options.Add(logoutRedirectsOption);
            Options.Add(createSecretOption);
            Options.Add(outputOption);
            Options.Add(overwriteOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdOption)
                    ?? throw new InvalidOperationException("--client-id is required.");
                var clientName = parseResult.GetValue(clientNameOption)
                    ?? throw new InvalidOperationException("--client-name is required.");
                var realmId = parseResult.GetValue(realmIdOption)
                    ?? throw new InvalidOperationException("--realm-id is required.");
                var requirePkce = parseResult.GetValue(pkceOption);
                var requireConsent = parseResult.GetValue(consentOption);
                var scope = parseResult.GetValue(scopeOption);
                var grantTypes = parseResult.GetValue(grantTypesOption);
                var redirectUris = parseResult.GetValue(redirectsOption);
                var logoutRedirectUris = parseResult.GetValue(logoutRedirectsOption);
                var createSecret = parseResult.GetValue(createSecretOption);
                var output = parseResult.GetValue(outputOption);
                var overwrite = parseResult.GetValue(overwriteOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var result = await CliAdminApiClient.PostAsync<ClientCreatedResult>(
                    config, connection, "admin/api/clients",
                    new
                    {
                        clientId,
                        clientName,
                        realmId,
                        requirePkce,
                        requireConsent,
                        scope,
                        grantTypes = grantTypes?.ToList(),
                        allowedLoginRedirectUris = redirectUris?.ToList(),
                        allowedLogoutRedirectUris = logoutRedirectUris?.ToList(),
                        createInitialSecret = createSecret
                    }).ConfigureAwait(false);

                if (result is null)
                {
                    AnsiConsole.MarkupLine("[red]Client creation failed: server returned an empty response.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]Client created successfully.[/]");
                AnsiConsole.MarkupLine($"  [bold]ID:[/]        {Markup.Escape(result.Id.ToString())}");
                AnsiConsole.MarkupLine($"  [bold]Client ID:[/] {Markup.Escape(result.ClientId ?? clientId)}");
                AnsiConsole.MarkupLine($"  [bold]Name:[/]      {Markup.Escape(result.ClientName ?? clientName)}");
                AnsiConsole.MarkupLine($"  [bold]Realm ID:[/]  {Markup.Escape(result.RealmId.ToString())}");

                if (!string.IsNullOrWhiteSpace(result.InitialSecret))
                {
                    var credentials = new
                    {
                        clientInternalId = result.Id,
                        clientId = result.ClientId,
                        clientName = result.ClientName,
                        realmId = result.RealmId,
                        initialSecret = result.InitialSecret,
                        createdAt = DateTimeOffset.UtcNow.ToString("O"),
                        server = connection.ServerUrl,
                        warning = result.Warning
                    };
                    var credJson = JsonSerializer.Serialize(credentials, new JsonSerializerOptions { WriteIndented = true });
                    var safeClientId = string.Concat((result.ClientId ?? (string)clientId)
                        .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
                    var suggestedFileName = $"client-{safeClientId}-credentials.json";
                    await CliFileOutput.WriteTextAsync(credJson, suggestedFileName, output, overwrite).ConfigureAwait(false);
                    var resolvedPath = CliFileOutput.ResolveOutputPath(suggestedFileName, output);

                    AnsiConsole.MarkupLine($"");
                    AnsiConsole.MarkupLine($"[yellow]Initial secret written to:[/] {Markup.Escape(resolvedPath)}");
                    AnsiConsole.MarkupLine($"[grey]The credential file has owner-only permissions (600). Keep it safe.[/]");
                }
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client delete <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ClientDeleteCommand : Command
    {
        public ClientDeleteCommand() : base("delete", "Delete a client (by internal GUID, not client_id string)")
        {
            var idArg = new Argument<Guid>("id") { Description = "Client internal ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };

            Arguments.Add(idArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(confirmOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var confirm = parseResult.GetValue(confirmOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Delete client {id}? This cannot be undone.", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(config, connection, $"admin/api/clients/{id}").ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]Client {id} deleted.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client update <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ClientUpdateCommand : Command
    {
        public ClientUpdateCommand() : base("update", "Update properties of an existing client (only non-null fields are applied)")
        {
            var idArg = new Argument<Guid>("id") { Description = "Client internal ID (GUID)" };
            var clientNameOption = new Option<string?>("--client-name") { Description = "New display name" };
            var pkceOption = new Option<bool?>("--require-pkce") { Description = "Require PKCE" };
            var consentOption = new Option<bool?>("--require-consent") { Description = "Require consent" };
            var parOption = new Option<bool?>("--require-par") { Description = "Require PAR" };
            var scopeOption = new Option<string?>("--scope") { Description = "Space-separated allowed scopes" };
            var grantTypesOption = new Option<string[]?>("--grant-types") { Description = "Allowed grant types" };
            var redirectsOption = new Option<string[]?>("--redirect-uris") { Description = "Allowed login redirect URIs" };
            var logoutRedirectsOption = new Option<string[]?>("--logout-redirect-uris") { Description = "Allowed logout redirect URIs" };
            var bclUriOption = new Option<string?>("--backchannel-logout-uri") { Description = "Back-channel logout URI" };
            var fclUriOption = new Option<string?>("--frontchannel-logout-uri") { Description = "Front-channel logout URI" };
            var authMethodOption = new Option<string?>("--token-auth-method") { Description = "Token endpoint auth method" };
            var oboOption = new Option<bool?>("--obo-enabled") { Description = "Enable OBO/Token Exchange" };
            var localLoginOption = new Option<bool?>("--allow-local-login") { Description = "Allow local login" };
            var externalIdpOption = new Option<bool?>("--allow-external-idp") { Description = "Allow external IdP login" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(idArg);
            Options.Add(clientNameOption);
            Options.Add(pkceOption);
            Options.Add(consentOption);
            Options.Add(parOption);
            Options.Add(scopeOption);
            Options.Add(grantTypesOption);
            Options.Add(redirectsOption);
            Options.Add(logoutRedirectsOption);
            Options.Add(bclUriOption);
            Options.Add(fclUriOption);
            Options.Add(authMethodOption);
            Options.Add(oboOption);
            Options.Add(localLoginOption);
            Options.Add(externalIdpOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(
                    config, parseResult.GetValue(serverOption), parseResult.GetValue(profileOption));

                await CliAdminApiClient.PutAsync(
                    config, connection, $"admin/api/clients/{id}",
                    new
                    {
                        clientName = parseResult.GetValue(clientNameOption),
                        requirePkce = parseResult.GetValue(pkceOption),
                        requireConsent = parseResult.GetValue(consentOption),
                        requirePar = parseResult.GetValue(parOption),
                        scope = parseResult.GetValue(scopeOption),
                        grantTypes = parseResult.GetValue(grantTypesOption)?.ToList(),
                        allowedLoginRedirectUris = parseResult.GetValue(redirectsOption)?.ToList(),
                        allowedLogoutRedirectUris = parseResult.GetValue(logoutRedirectsOption)?.ToList(),
                        backChannelLogoutUri = parseResult.GetValue(bclUriOption),
                        frontChannelLogoutUri = parseResult.GetValue(fclUriOption),
                        tokenEndpointAuthMethod = parseResult.GetValue(authMethodOption),
                        oboEnabled = parseResult.GetValue(oboOption),
                        allowLocalLogin = parseResult.GetValue(localLoginOption),
                        allowExternalIdp = parseResult.GetValue(externalIdpOption)
                    }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Client {id} updated successfully.[/]");
            });
        }
    }
}

public sealed class ClientDetail
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("clientName")]
    public string? ClientName { get; set; }

    [JsonPropertyName("realmId")]
    public Guid RealmId { get; set; }

    [JsonPropertyName("realmName")]
    public string? RealmName { get; set; }

    [JsonPropertyName("requirePkce")]
    public bool RequirePkce { get; set; }

    [JsonPropertyName("requireConsent")]
    public bool RequireConsent { get; set; }

    [JsonPropertyName("requirePar")]
    public bool RequirePar { get; set; }

    [JsonPropertyName("isSystemClient")]
    public bool IsSystemClient { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

public sealed class ClientCreatedResult
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    [JsonPropertyName("clientName")]
    public string? ClientName { get; set; }

    [JsonPropertyName("realmId")]
    public Guid RealmId { get; set; }

    [JsonPropertyName("initialSecret")]
    public string? InitialSecret { get; set; }

    [JsonPropertyName("warning")]
    public string? Warning { get; set; }
}

public sealed class ClientListItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("clientName")]
    public string? ClientName { get; set; }

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; set; }

    [JsonPropertyName("tenantSlug")]
    public string? TenantSlug { get; set; }

    [JsonPropertyName("tenantName")]
    public string? TenantName { get; set; }

    [JsonPropertyName("realmId")]
    public Guid RealmId { get; set; }

    [JsonPropertyName("realmName")]
    public string RealmName { get; set; } = string.Empty;

    [JsonPropertyName("requirePkce")]
    public bool RequirePkce { get; set; }

    [JsonPropertyName("requireConsent")]
    public bool RequireConsent { get; set; }

    [JsonPropertyName("requirePar")]
    public bool RequirePar { get; set; }

    [JsonPropertyName("hasJwks")]
    public bool HasJwks { get; set; }

    [JsonPropertyName("isSystemClient")]
    public bool IsSystemClient { get; set; }

    [JsonPropertyName("grantTypes")]
    public string[] GrantTypes { get; set; } = Array.Empty<string>();

    [JsonPropertyName("scopes")]
    public string[] Scopes { get; set; } = Array.Empty<string>();
}
