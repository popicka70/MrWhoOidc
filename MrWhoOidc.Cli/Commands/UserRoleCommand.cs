using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Manages role assignments for a user.
/// Registered as a subcommand of UserCommand.
/// </summary>
public sealed class UserRoleCommand : Command
{
    public UserRoleCommand() : base("role", "Manage role assignments for a user")
    {
        Subcommands.Add(new RoleListCommand());
        Subcommands.Add(new RoleAssignCommand());
        Subcommands.Add(new RoleUnassignCommand());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // user role list <userId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RoleListCommand : Command
    {
        public RoleListCommand() : base("list", "List roles assigned to a user")
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
                var result = await CliAdminApiClient.GetAsync<UserRolesResult>(
                    config, connection, $"admin/api/users/{userId}/roles").ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    return;
                }

                var realmRoles = result?.RealmRoles ?? [];
                var clientRoles = result?.ClientRoles ?? [];

                if (realmRoles.Count == 0 && clientRoles.Count == 0)
                {
                    AnsiConsole.MarkupLine("[grey]No roles assigned.[/]");
                    return;
                }

                if (realmRoles.Count > 0)
                {
                    AnsiConsole.MarkupLine("[bold]Realm Roles:[/]");
                    var table = new Table().Border(TableBorder.Rounded)
                        .AddColumn("Role ID")
                        .AddColumn("Name")
                        .AddColumn("Realm ID")
                        .AddColumn("Active");
                    foreach (var r in realmRoles)
                        table.AddRow(
                            Markup.Escape(r.Id.ToString()),
                            Markup.Escape(r.Name),
                            Markup.Escape(r.RealmId.ToString()),
                            r.IsActive ? "yes" : "no");
                    AnsiConsole.Write(table);
                }

                if (clientRoles.Count > 0)
                {
                    AnsiConsole.MarkupLine("[bold]Client Roles:[/]");
                    var table = new Table().Border(TableBorder.Rounded)
                        .AddColumn("Role ID")
                        .AddColumn("Name")
                        .AddColumn("Client ID")
                        .AddColumn("Active");
                    foreach (var r in clientRoles)
                        table.AddRow(
                            Markup.Escape(r.Id.ToString()),
                            Markup.Escape(r.Name),
                            Markup.Escape(r.ClientId.ToString()),
                            r.IsActive ? "yes" : "no");
                    AnsiConsole.Write(table);
                }
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // user role assign <userId> --role-id <roleId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RoleAssignCommand : Command
    {
        public RoleAssignCommand() : base("assign", "Assign a role to a user")
        {
            var userIdArg = new Argument<Guid>("user-id") { Description = "User ID (GUID)" };
            var roleIdOption = new Option<Guid>("--role-id") { Description = "Role ID to assign (required)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(userIdArg);
            Options.Add(roleIdOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var userId = parseResult.GetValue(userIdArg);
                var roleId = parseResult.GetValue(roleIdOption);
                if (roleId == Guid.Empty)
                    throw new InvalidOperationException("--role-id is required.");
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PostAsync<object>(
                    config, connection, $"admin/api/users/{userId}/roles",
                    new { roleId }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Role {roleId} assigned to user {userId}.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // user role unassign <userId> --role-id <roleId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RoleUnassignCommand : Command
    {
        public RoleUnassignCommand() : base("unassign", "Remove a role from a user")
        {
            var userIdArg = new Argument<Guid>("user-id") { Description = "User ID (GUID)" };
            var roleIdOption = new Option<Guid>("--role-id") { Description = "Role ID to remove (required)" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(userIdArg);
            Options.Add(roleIdOption);
            Options.Add(confirmOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var userId = parseResult.GetValue(userIdArg);
                var roleId = parseResult.GetValue(roleIdOption);
                if (roleId == Guid.Empty)
                    throw new InvalidOperationException("--role-id is required.");
                var confirm = parseResult.GetValue(confirmOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Remove role {roleId} from user {userId}?", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(
                    config, connection, $"admin/api/users/{userId}/roles/{roleId}").ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Role {roleId} removed from user {userId}.[/]");
            });
        }
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public sealed class UserRolesResult
{
    [JsonPropertyName("realmRoles")]
    public List<UserRealmRoleItem> RealmRoles { get; set; } = [];

    [JsonPropertyName("clientRoles")]
    public List<UserClientRoleItem> ClientRoles { get; set; } = [];
}

public sealed class UserRealmRoleItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("realmId")]
    public Guid RealmId { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}

public sealed class UserClientRoleItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("clientId")]
    public Guid ClientId { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}
