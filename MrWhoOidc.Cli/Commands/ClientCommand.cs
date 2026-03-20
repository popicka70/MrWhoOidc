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
    }

    private sealed class ClientListCommand : Command
    {
        public ClientListCommand() : base("list", "List clients for the current tenant or across the platform")
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