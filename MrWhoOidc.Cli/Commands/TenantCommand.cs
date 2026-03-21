using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

public sealed class TenantCommand : Command
{
    public TenantCommand() : base("tenant", "Manage platform tenants")
    {
        Subcommands.Add(new TenantListCommand());
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
                AnsiConsole.WriteLine(JsonSerializer.Serialize(tenants, new JsonSerializerOptions { WriteIndented = true }));
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