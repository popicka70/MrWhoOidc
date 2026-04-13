using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Manages user-client assignments for a user.
/// Registered as a subcommand of UserCommand.
/// </summary>
public sealed class UserClientCommand : Command
{
    public UserClientCommand() : base("client", "Manage which clients a user can access")
    {
        Subcommands.Add(new ClientListCommand());
        Subcommands.Add(new ClientAssignCommand());
        Subcommands.Add(new ClientUnassignCommand());
    }

    private sealed class ClientListCommand : Command
    {
        public ClientListCommand() : base("list", "List clients assigned to a user")
        {
            var userIdArg = new Argument<Guid>("user-id") { Description = "User ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Arguments.Add(userIdArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var userId = parseResult.GetValue(userIdArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var format = parseResult.GetValue(formatOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var items = await CliAdminApiClient.GetListAsync<UserClientAssignmentItem>(
                    config, connection, $"admin/api/users/{userId}/clients").ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(items, SharedJsonOptions.IndentedOptions));
                    return;
                }

                if (items.Count == 0)
                {
                    AnsiConsole.MarkupLine("[grey]No client assignments found.[/]");
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("Client Record ID")
                    .AddColumn("Client ID")
                    .AddColumn("Name")
                    .AddColumn("Realm")
                    .AddColumn("Active");

                foreach (var item in items)
                {
                    table.AddRow(
                        Markup.Escape(item.Id.ToString()),
                        Markup.Escape(item.ClientId ?? "-"),
                        Markup.Escape(item.ClientName ?? "-"),
                        Markup.Escape(item.RealmName ?? item.RealmId.ToString()),
                        item.IsActive ? "yes" : "no");
                }

                AnsiConsole.Write(table);
            });
        }
    }

    private sealed class ClientAssignCommand : Command
    {
        public ClientAssignCommand() : base("assign", "Assign a client to a user")
        {
            var userIdArg = new Argument<Guid>("user-id") { Description = "User ID (GUID)" };
            var clientIdOption = new Option<Guid>("--client-id") { Description = "Client internal ID (GUID) to assign" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(userIdArg);
            Options.Add(clientIdOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var userId = parseResult.GetValue(userIdArg);
                var clientId = parseResult.GetValue(clientIdOption);
                if (clientId == Guid.Empty)
                    throw new InvalidOperationException("--client-id is required.");
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PostAsync<object>(
                    config,
                    connection,
                    $"admin/api/users/{userId}/clients",
                    new { clientId }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Client {clientId} assigned to user {userId}.[/]");
            });
        }
    }

    private sealed class ClientUnassignCommand : Command
    {
        public ClientUnassignCommand() : base("unassign", "Remove a client assignment from a user")
        {
            var userIdArg = new Argument<Guid>("user-id") { Description = "User ID (GUID)" };
            var clientIdOption = new Option<Guid>("--client-id") { Description = "Client internal ID (GUID) to remove" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(userIdArg);
            Options.Add(clientIdOption);
            Options.Add(confirmOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var userId = parseResult.GetValue(userIdArg);
                var clientId = parseResult.GetValue(clientIdOption);
                if (clientId == Guid.Empty)
                    throw new InvalidOperationException("--client-id is required.");
                var confirm = parseResult.GetValue(confirmOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Remove client {clientId} from user {userId}?", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(
                    config, connection, $"admin/api/users/{userId}/clients/{clientId}").ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Client {clientId} removed from user {userId}.[/]");
            });
        }
    }
}

public sealed class UserClientAssignmentItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    [JsonPropertyName("clientName")]
    public string? ClientName { get; set; }

    [JsonPropertyName("realmId")]
    public Guid RealmId { get; set; }

    [JsonPropertyName("realmName")]
    public string? RealmName { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}
