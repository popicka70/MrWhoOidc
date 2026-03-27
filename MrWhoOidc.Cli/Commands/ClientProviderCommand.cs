using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Manages the link between a client and identity providers (which IdPs appear on the login page).
/// Registered as nested subcommands under the existing ClientCommand.
/// </summary>
public sealed class ClientProviderCommand : Command
{
    public ClientProviderCommand() : base("provider", "Manage client-provider links")
    {
        Subcommands.Add(new ClientProviderListCommand());
        Subcommands.Add(new ClientProviderLinkCommand());
        Subcommands.Add(new ClientProviderUpdateCommand());
        Subcommands.Add(new ClientProviderUnlinkCommand());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client provider list <clientId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ClientProviderListCommand : Command
    {
        public ClientProviderListCommand() : base("list", "List identity providers linked to a client")
        {
            var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Arguments.Add(clientIdArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var format = parseResult.GetValue(formatOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var links = await CliAdminApiClient.GetListAsync<ClientProviderLinkItem>(
                    config, connection, $"admin/api/clients/{clientId}/providers").ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(links, SharedJsonOptions.IndentedOptions));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("Provider ID")
                    .AddColumn("Provider Name")
                    .AddColumn("Enabled")
                    .AddColumn("Auto-Redirect")
                    .AddColumn("Order");

                foreach (var l in links)
                {
                    table.AddRow(
                        Markup.Escape(l.IdentityProviderId.ToString()),
                        Markup.Escape(l.ProviderName ?? "-"),
                        l.Enabled ? "yes" : "no",
                        l.AutoRedirectIfSingle ? "yes" : "no",
                        l.Order.ToString());
                }

                AnsiConsole.Write(table);
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client provider link <clientId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ClientProviderLinkCommand : Command
    {
        public ClientProviderLinkCommand() : base("link", "Link an identity provider to a client")
        {
            var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
            var providerIdOption = new Option<Guid>("--provider-id") { Description = "Identity provider ID (GUID)" };
            var enabledOption = new Option<bool?>("--enabled") { Description = "Enable the provider for this client (default: true)" };
            var autoRedirectOption = new Option<bool?>("--auto-redirect") { Description = "Auto-redirect if this is the only provider" };
            var orderOption = new Option<int?>("--order") { Description = "Display order on the login page" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(clientIdArg);
            Options.Add(providerIdOption);
            Options.Add(enabledOption);
            Options.Add(autoRedirectOption);
            Options.Add(orderOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdArg);
                var providerId = parseResult.GetValue(providerIdOption);
                var enabled = parseResult.GetValue(enabledOption);
                var autoRedirect = parseResult.GetValue(autoRedirectOption);
                var order = parseResult.GetValue(orderOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                if (providerId == Guid.Empty)
                    throw new InvalidOperationException("--provider-id is required.");

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PostAsync<object>(
                    config, connection, $"admin/api/clients/{clientId}/providers",
                    new { identityProviderId = providerId, enabled, autoRedirectIfSingle = autoRedirect, order }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Provider {providerId} linked to client {clientId}.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client provider update <clientId> <providerId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ClientProviderUpdateCommand : Command
    {
        public ClientProviderUpdateCommand() : base("update", "Update a client-provider link")
        {
            var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
            var providerIdArg = new Argument<Guid>("provider-id") { Description = "Identity provider ID (GUID)" };
            var enabledOption = new Option<bool?>("--enabled") { Description = "Enable or disable the provider for this client" };
            var autoRedirectOption = new Option<bool?>("--auto-redirect") { Description = "Auto-redirect if sole provider" };
            var orderOption = new Option<int?>("--order") { Description = "Display order" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(clientIdArg);
            Arguments.Add(providerIdArg);
            Options.Add(enabledOption);
            Options.Add(autoRedirectOption);
            Options.Add(orderOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdArg);
                var providerId = parseResult.GetValue(providerIdArg);
                var enabled = parseResult.GetValue(enabledOption);
                var autoRedirect = parseResult.GetValue(autoRedirectOption);
                var order = parseResult.GetValue(orderOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PutAsync(
                    config, connection, $"admin/api/clients/{clientId}/providers/{providerId}",
                    new { enabled, autoRedirectIfSingle = autoRedirect, order }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Client-provider link updated.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client provider unlink <clientId> <providerId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ClientProviderUnlinkCommand : Command
    {
        public ClientProviderUnlinkCommand() : base("unlink", "Remove an identity provider from a client")
        {
            var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
            var providerIdArg = new Argument<Guid>("provider-id") { Description = "Identity provider ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };

            Arguments.Add(clientIdArg);
            Arguments.Add(providerIdArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(confirmOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdArg);
                var providerId = parseResult.GetValue(providerIdArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var confirm = parseResult.GetValue(confirmOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Unlink provider {providerId} from client {clientId}?", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(
                    config, connection, $"admin/api/clients/{clientId}/providers/{providerId}").ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Provider {providerId} unlinked from client {clientId}.[/]");
            });
        }
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public sealed class ClientProviderLinkItem
{
    [JsonPropertyName("identityProviderId")]
    public Guid IdentityProviderId { get; set; }

    [JsonPropertyName("providerName")]
    public string? ProviderName { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("autoRedirectIfSingle")]
    public bool AutoRedirectIfSingle { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }
}
