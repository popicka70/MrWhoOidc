using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

public sealed class ScopeCommand : Command
{
    public ScopeCommand() : base("scope", "Manage OAuth and OIDC scopes")
    {
        Subcommands.Add(new ScopeListCommand());
        Subcommands.Add(new ScopeCreateCommand());
        Subcommands.Add(new ScopeUpdateCommand());
        Subcommands.Add(new ScopeDeleteCommand());
    }

    private sealed class ScopeListCommand : Command
    {
        public ScopeListCommand() : base("list", "List scopes for the current tenant or across the platform")
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

            var (resolvedConnection, path) = ResolveScopeListTarget(connection, tenant);
            var scopes = await CliAdminApiClient.GetListAsync<ScopeListItem>(config, resolvedConnection, path).ConfigureAwait(false);

            if (format == OutputFormat.Json)
            {
                AnsiConsole.WriteLine(JsonSerializer.Serialize(scopes, SharedJsonOptions.IndentedOptions));
                return;
            }

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Name")
                .AddColumn("Tenant")
                .AddColumn("Global")
                .AddColumn("Exposed")
                .AddColumn("Description");

            foreach (var scope in scopes)
            {
                table.AddRow(
                    Markup.Escape(scope.Name),
                    Markup.Escape(scope.IsGlobal ? "global" : (scope.TenantSlug ?? "-")),
                    scope.IsGlobal ? "yes" : "no",
                    scope.IsExposed ? "yes" : "no",
                    Markup.Escape(scope.Description ?? "-"));
            }

            AnsiConsole.Write(table);
        }

        private static (AuthenticatedConnection Connection, string Path) ResolveScopeListTarget(AuthenticatedConnection connection, string? tenant)
        {
            if (connection.Profile.IsPlatformAdmin && (string.IsNullOrWhiteSpace(connection.Profile.TenantSlug) || !string.IsNullOrWhiteSpace(tenant)))
            {
                var platformServer = CliServerConnection.GetPlatformServerUrl(connection.ServerUrl);
                var path = string.IsNullOrWhiteSpace(tenant)
                    ? "platform-admin/api/scopes"
                    : $"platform-admin/api/scopes?tenant={Uri.EscapeDataString(tenant)}";
                return (new AuthenticatedConnection(connection.ProfileName, platformServer, connection.Profile), path);
            }

            if (!string.IsNullOrWhiteSpace(tenant) && !string.Equals(tenant, connection.Profile.TenantSlug, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The selected profile is tenant-scoped. Use a platform-admin profile to query a different tenant.");
            }

            return (connection, "admin/api/scopes");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // scope create
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ScopeCreateCommand : Command
    {
        public ScopeCreateCommand() : base("create", "Create a new tenant-scoped OAuth/OIDC scope")
        {
            var nameOption = new Option<string?>("--name") { Description = "Scope name (e.g. api.read)" };
            var descriptionOption = new Option<string?>("--description") { Description = "Human-readable description" };
            var isExposedOption = new Option<bool?>("--is-exposed") { Description = "Expose scope in discovery (default: true)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Options.Add(nameOption);
            Options.Add(descriptionOption);
            Options.Add(isExposedOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var name = parseResult.GetValue(nameOption)
                    ?? throw new InvalidOperationException("--name is required.");
                var description = parseResult.GetValue(descriptionOption);
                var isExposed = parseResult.GetValue(isExposedOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PostAsync<object?>(
                    config, connection, "admin/api/scopes",
                    new { name, description, isExposed }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Scope '{Markup.Escape(name!)}' created.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // scope update <name>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ScopeUpdateCommand : Command
    {
        public ScopeUpdateCommand() : base("update", "Update a tenant-scoped scope's description or exposure flag")
        {
            var nameArg = new Argument<string>("name") { Description = "Scope name to update" };
            var descriptionOption = new Option<string?>("--description") { Description = "Human-readable description" };
            var isExposedOption = new Option<bool?>("--is-exposed") { Description = "Expose scope in discovery" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(nameArg);
            Options.Add(descriptionOption);
            Options.Add(isExposedOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var name = parseResult.GetValue(nameArg)!;
                var description = parseResult.GetValue(descriptionOption);
                var isExposed = parseResult.GetValue(isExposedOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PutAsync(
                    config, connection, $"admin/api/scopes/{Uri.EscapeDataString(name)}",
                    new { name, description, isExposed }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Scope '{Markup.Escape(name)}' updated.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // scope delete <name>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ScopeDeleteCommand : Command
    {
        public ScopeDeleteCommand() : base("delete", "Delete a tenant-scoped scope")
        {
            var nameArg = new Argument<string>("name") { Description = "Scope name to delete" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };

            Arguments.Add(nameArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(confirmOption);

            this.SetSafeAction(async parseResult =>
            {
                var name = parseResult.GetValue(nameArg)!;
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var confirm = parseResult.GetValue(confirmOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Delete scope '{name}'? This cannot be undone.", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(config, connection, $"admin/api/scopes/{Uri.EscapeDataString(name)}").ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]Scope '{Markup.Escape(name)}' deleted.[/]");
            });
        }
    }
}

public sealed class ScopeListItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isExposed")]
    public bool IsExposed { get; set; }

    [JsonPropertyName("isGlobal")]
    public bool IsGlobal { get; set; }

    [JsonPropertyName("tenantId")]
    public Guid? TenantId { get; set; }

    [JsonPropertyName("tenantSlug")]
    public string? TenantSlug { get; set; }

    [JsonPropertyName("tenantName")]
    public string? TenantName { get; set; }
}
