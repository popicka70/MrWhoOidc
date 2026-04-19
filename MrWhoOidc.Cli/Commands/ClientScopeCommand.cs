using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Manages scope assignments for a client (post-creation add/remove).
/// Registered as a subcommand of ClientCommand.
/// </summary>
public sealed class ClientScopeCommand : Command
{
    public ClientScopeCommand() : base("scope", "Manage scopes a client is allowed to request")
    {
        Subcommands.Add(new ScopeListCommand());
        Subcommands.Add(new ScopeAddCommand());
        Subcommands.Add(new ScopeRemoveCommand());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client scope list <clientId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ScopeListCommand : Command
    {
        public ScopeListCommand() : base("list", "List scopes assigned to a client")
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
                var scopes = await CliAdminApiClient.GetListAsync<ClientScopeItem>(
                    config, connection, $"admin/api/clients/{clientId}/scopes").ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(scopes, SharedJsonOptions.IndentedOptions));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded).AddColumn("Scope Name");
                foreach (var s in scopes)
                    table.AddRow(Markup.Escape(s.ScopeName));

                AnsiConsole.Write(table);
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client scope add <clientId> --scope <name>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ScopeAddCommand : Command
    {
        public ScopeAddCommand() : base("add", "Add a scope to a client")
        {
            var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
            var scopeOption = new Option<string>("--scope") { Description = "Scope name to add (required)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(clientIdArg);
            Options.Add(scopeOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdArg);
                var scope = parseResult.GetValue(scopeOption)
                    ?? throw new InvalidOperationException("--scope is required.");
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PostAsync<object>(
                    config, connection, $"admin/api/clients/{clientId}/scopes",
                    new { scopeName = scope }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Scope '{Markup.Escape(scope)}' added to client {clientId}.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client scope remove <clientId> --scope <name>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ScopeRemoveCommand : Command
    {
        public ScopeRemoveCommand() : base("remove", "Remove a scope from a client")
        {
            var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
            var scopeOption = new Option<string>("--scope") { Description = "Scope name to remove (required)" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(clientIdArg);
            Options.Add(scopeOption);
            Options.Add(confirmOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdArg);
                var scope = parseResult.GetValue(scopeOption)
                    ?? throw new InvalidOperationException("--scope is required.");
                var confirm = parseResult.GetValue(confirmOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Remove scope '{scope}' from client {clientId}?", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(
                    config, connection, $"admin/api/clients/{clientId}/scopes/{Uri.EscapeDataString(scope)}").ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Scope '{Markup.Escape(scope)}' removed from client {clientId}.[/]");
            });
        }
    }
}

public sealed class ClientScopeItem
{
    [JsonPropertyName("scopeName")]
    public string ScopeName { get; set; } = string.Empty;
}
