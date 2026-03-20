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
    }

    private sealed class ScopeListCommand : Command
    {
        public ScopeListCommand() : base("list", "List scopes for the current tenant or across the platform")
        {
            var serverOption = new Option<string?>("--server", "Server URL (defaults to the saved profile server)");
            var profileOption = new Option<string?>("--profile", "Authenticated profile to use");
            var tenantOption = new Option<string?>("--tenant", "Tenant slug filter for platform-admin profiles");
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format (table or json)",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(tenantOption);
            Options.Add(formatOption);

            this.SetAction(async parseResult =>
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
                AnsiConsole.WriteLine(JsonSerializer.Serialize(scopes, new JsonSerializerOptions { WriteIndented = true }));
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