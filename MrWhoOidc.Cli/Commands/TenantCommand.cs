using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

public sealed class TenantCommand : Command
{
    public TenantCommand() : base("tenant", "Manage platform tenants (platform-admin)")
    {
        Subcommands.Add(new TenantListCommand());
        Subcommands.Add(new TenantGetCommand());
        Subcommands.Add(new TenantCreateCommand());
        Subcommands.Add(new TenantUpdateCommand());
        Subcommands.Add(new TenantDeleteCommand());
    }

    private sealed class TenantListCommand : Command
    {
        public TenantListCommand() : base("list", "List tenants visible to the current platform-admin profile")
        {
            var serverOption = new Option<string?>("--server") { Description = "Platform server URL (defaults to the saved profile server)" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var searchOption = new Option<string?>("--search") { Description = "Filter tenants by slug, name, or description" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format (table or json)",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(searchOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var search = parseResult.GetValue(searchOption);
                var format = parseResult.GetValue(formatOption);
                await HandleAsync(server, profile, search, format).ConfigureAwait(false);
            });
        }

        private static async Task HandleAsync(string? server, string? profileName, string? search, OutputFormat format)
        {
            var config = await CliConfig.LoadAsync().ConfigureAwait(false);
            var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profileName);
            if (!connection.Profile.IsPlatformAdmin)
            {
                throw new InvalidOperationException("Tenant listing requires a platform-admin profile.");
            }

            var platformServer = CliServerConnection.GetPlatformServerUrl(connection.ServerUrl);
            var platformConnection = new AuthenticatedConnection(connection.ProfileName, platformServer, connection.Profile);
            var path = string.IsNullOrWhiteSpace(search)
                ? "platform-admin/api/tenants"
                : $"platform-admin/api/tenants?search={Uri.EscapeDataString(search)}";

            var tenants = await CliAdminApiClient.GetListAsync<TenantListItem>(config, platformConnection, path).ConfigureAwait(false);

            if (format == OutputFormat.Json)
            {
                AnsiConsole.WriteLine(JsonSerializer.Serialize(tenants, SharedJsonOptions.IndentedOptions));
                return;
            }

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Slug")
                .AddColumn("Name")
                .AddColumn("Status")
                .AddColumn("Users")
                .AddColumn("Clients")
                .AddColumn("Issuer");

            foreach (var tenant in tenants)
            {
                table.AddRow(
                    Markup.Escape(tenant.Slug),
                    Markup.Escape(tenant.Name),
                    Markup.Escape(tenant.Status),
                    tenant.UserCount.ToString(),
                    tenant.ClientCount.ToString(),
                    Markup.Escape(tenant.IssuerUri));
            }

            AnsiConsole.Write(table);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // tenant get <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class TenantGetCommand : Command
    {
        public TenantGetCommand() : base("get", "Get details of a specific tenant by ID")
        {
            var idArg = new Argument<Guid>("id") { Description = "Tenant ID (GUID)" };
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
                if (!connection.Profile.IsPlatformAdmin)
                    throw new InvalidOperationException("Tenant operations require a platform-admin profile.");

                var platformServer = CliServerConnection.GetPlatformServerUrl(connection.ServerUrl);
                var platformConnection = new AuthenticatedConnection(connection.ProfileName, platformServer, connection.Profile);
                var tenant = await CliAdminApiClient.GetAsync<TenantListItem>(
                    config, platformConnection, $"platform-admin/api/tenants/{id}").ConfigureAwait(false);

                if (tenant is null)
                {
                    AnsiConsole.MarkupLine("[red]Tenant not found.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[bold]ID:[/]          {Markup.Escape(tenant.Id.ToString())}");
                AnsiConsole.MarkupLine($"[bold]Slug:[/]        {Markup.Escape(tenant.Slug)}");
                AnsiConsole.MarkupLine($"[bold]Name:[/]        {Markup.Escape(tenant.Name)}");
                AnsiConsole.MarkupLine($"[bold]Description:[/] {Markup.Escape(tenant.Description ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Status:[/]      {Markup.Escape(tenant.Status)}");
                AnsiConsole.MarkupLine($"[bold]Issuer:[/]      {Markup.Escape(tenant.IssuerUri)}");
                AnsiConsole.MarkupLine($"[bold]Admin Email:[/] {Markup.Escape(tenant.AdminEmail ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Users:[/]       {tenant.UserCount} / {tenant.MaxUsers}");
                AnsiConsole.MarkupLine($"[bold]Clients:[/]     {tenant.ClientCount} / {tenant.MaxClients}");
                AnsiConsole.MarkupLine($"[bold]Created At:[/]  {Markup.Escape(tenant.CreatedAt.ToString("u"))}");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // tenant create
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class TenantCreateCommand : Command
    {
        public TenantCreateCommand() : base("create",
            "Create (seed) a new tenant with default realm, admin user, and system clients. Credentials are written to a file.")
        {
            var slugOption = new Option<string>("--slug") { Description = "URL-safe tenant slug (required)" };
            var nameOption = new Option<string>("--name") { Description = "Display name (required)" };
            var adminEmailOption = new Option<string?>("--admin-email") { Description = "Admin email address" };
            var adminPasswordOption = new Option<string?>("--admin-password") { Description = "Admin password (generated if omitted)" };
            var outputOption = new Option<string?>("--output") { Description = "File path for the credentials JSON" };
            var overwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite output file if exists" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Options.Add(slugOption);
            Options.Add(nameOption);
            Options.Add(adminEmailOption);
            Options.Add(adminPasswordOption);
            Options.Add(outputOption);
            Options.Add(overwriteOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var slug = parseResult.GetValue(slugOption)
                    ?? throw new InvalidOperationException("--slug is required.");
                var name = parseResult.GetValue(nameOption)
                    ?? throw new InvalidOperationException("--name is required.");
                var adminEmail = parseResult.GetValue(adminEmailOption);
                var adminPassword = parseResult.GetValue(adminPasswordOption);
                var output = parseResult.GetValue(outputOption);
                var overwrite = parseResult.GetValue(overwriteOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                if (!connection.Profile.IsPlatformAdmin)
                    throw new InvalidOperationException("Tenant creation requires a platform-admin profile.");

                var platformServer = CliServerConnection.GetPlatformServerUrl(connection.ServerUrl);
                var platformConnection = new AuthenticatedConnection(connection.ProfileName, platformServer, connection.Profile);

                var result = await CliAdminApiClient.PostAsync<TenantSeedResult>(
                    config, platformConnection, "platform-admin/api/seed-tenant",
                    new { tenantSlug = slug, tenantName = name, adminEmail, adminPassword }).ConfigureAwait(false);

                if (result is null)
                {
                    AnsiConsole.MarkupLine("[red]Tenant creation failed: server returned an empty response.[/]");
                    return;
                }

                // Write credentials to file
                var credentials = new
                {
                    tenantId = result.TenantId,
                    tenantSlug = result.TenantSlug,
                    tenantName = result.TenantName,
                    adminEmail = result.AdminEmail,
                    adminPassword = result.AdminPassword,
                    adminClientId = result.AdminClientId,
                    webClientId = result.WebClientId,
                    loginUrl = result.LoginUrl,
                    adminUrl = result.AdminUrl,
                    createdAt = DateTimeOffset.UtcNow.ToString("O"),
                    server = connection.ServerUrl
                };
                var credJson = JsonSerializer.Serialize(credentials, SharedJsonOptions.IndentedOptions);
                var suggestedFileName = $"tenant-{slug}-credentials.json";
                await CliFileOutput.WriteTextAsync(credJson, suggestedFileName, output, overwrite).ConfigureAwait(false);
                var resolvedPath = CliFileOutput.ResolveOutputPath(suggestedFileName, output);

                AnsiConsole.MarkupLine($"[green]Tenant created successfully.[/]");
                AnsiConsole.MarkupLine($"  [bold]ID:[/]      {Markup.Escape(result.TenantId?.ToString() ?? "-")}");
                AnsiConsole.MarkupLine($"  [bold]Slug:[/]    {Markup.Escape(result.TenantSlug ?? slug)}");
                AnsiConsole.MarkupLine($"  [bold]Name:[/]    {Markup.Escape(result.TenantName ?? name)}");
                AnsiConsole.MarkupLine($"  [bold]Login:[/]   {Markup.Escape(result.LoginUrl ?? "-")}");
                AnsiConsole.MarkupLine($"  [bold]Admin:[/]   {Markup.Escape(result.AdminUrl ?? "-")}");
                AnsiConsole.MarkupLine($"");
                AnsiConsole.MarkupLine($"[yellow]Credentials written to:[/] {Markup.Escape(resolvedPath)}");
                AnsiConsole.MarkupLine($"[grey]The credential file has owner-only permissions (600). Keep it safe.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // tenant update <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class TenantUpdateCommand : Command
    {
        public TenantUpdateCommand() : base("update", "Update properties of an existing tenant")
        {
            var idArg = new Argument<Guid>("id") { Description = "Tenant ID (GUID)" };
            var nameOption = new Option<string?>("--name") { Description = "New display name" };
            var descriptionOption = new Option<string?>("--description") { Description = "New description" };
            var adminEmailOption = new Option<string?>("--admin-email") { Description = "New admin email" };
            var statusOption = new Option<string?>("--status") { Description = "New status (Active, Suspended)" };
            var maxUsersOption = new Option<int?>("--max-users") { Description = "Max users limit" };
            var maxClientsOption = new Option<int?>("--max-clients") { Description = "Max clients limit" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(idArg);
            Options.Add(nameOption);
            Options.Add(descriptionOption);
            Options.Add(adminEmailOption);
            Options.Add(statusOption);
            Options.Add(maxUsersOption);
            Options.Add(maxClientsOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                if (!connection.Profile.IsPlatformAdmin)
                    throw new InvalidOperationException("Tenant update requires a platform-admin profile.");

                var platformServer = CliServerConnection.GetPlatformServerUrl(connection.ServerUrl);
                var platformConnection = new AuthenticatedConnection(connection.ProfileName, platformServer, connection.Profile);

                await CliAdminApiClient.PutAsync(
                    config, platformConnection, $"platform-admin/api/tenants/{id}",
                    new
                    {
                        name = parseResult.GetValue(nameOption),
                        description = parseResult.GetValue(descriptionOption),
                        adminEmail = parseResult.GetValue(adminEmailOption),
                        status = parseResult.GetValue(statusOption),
                        maxUsers = parseResult.GetValue(maxUsersOption),
                        maxClients = parseResult.GetValue(maxClientsOption)
                    }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Tenant {id} updated successfully.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // tenant delete <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class TenantDeleteCommand : Command
    {
        public TenantDeleteCommand() : base("delete", "Soft-delete a tenant (sets status to Deleted)")
        {
            var idArg = new Argument<Guid>("id") { Description = "Tenant ID (GUID)" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(idArg);
            Options.Add(confirmOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var confirm = parseResult.GetValue(confirmOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Delete tenant {id}? This will soft-delete the tenant.", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                if (!connection.Profile.IsPlatformAdmin)
                    throw new InvalidOperationException("Tenant deletion requires a platform-admin profile.");

                var platformServer = CliServerConnection.GetPlatformServerUrl(connection.ServerUrl);
                var platformConnection = new AuthenticatedConnection(connection.ProfileName, platformServer, connection.Profile);
                await CliAdminApiClient.DeleteAsync(
                    config, platformConnection, $"platform-admin/api/tenants/{id}").ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Tenant {id} soft-deleted.[/]");
            });
        }
    }
}

public sealed class TenantListItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("issuerUri")]
    public string IssuerUri { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("userCount")]
    public int UserCount { get; set; }

    [JsonPropertyName("clientCount")]
    public int ClientCount { get; set; }

    [JsonPropertyName("maxUsers")]
    public int MaxUsers { get; set; }

    [JsonPropertyName("maxClients")]
    public int MaxClients { get; set; }

    [JsonPropertyName("adminEmail")]
    public string? AdminEmail { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TenantSeedResult
{
    [JsonPropertyName("tenantId")]
    public Guid? TenantId { get; set; }

    [JsonPropertyName("tenantSlug")]
    public string? TenantSlug { get; set; }

    [JsonPropertyName("tenantName")]
    public string? TenantName { get; set; }

    [JsonPropertyName("adminEmail")]
    public string? AdminEmail { get; set; }

    [JsonPropertyName("adminPassword")]
    public string? AdminPassword { get; set; }

    [JsonPropertyName("adminClientId")]
    public string? AdminClientId { get; set; }

    [JsonPropertyName("webClientId")]
    public string? WebClientId { get; set; }

    [JsonPropertyName("loginUrl")]
    public string? LoginUrl { get; set; }

    [JsonPropertyName("adminUrl")]
    public string? AdminUrl { get; set; }
}
