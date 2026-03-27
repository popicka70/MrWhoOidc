using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Manages roles within the current tenant.
/// Roles are scoped to a realm and can be assigned to users.
/// </summary>
public sealed class RoleCommand : Command
{
    public RoleCommand() : base("role", "Manage roles within the current tenant")
    {
        Subcommands.Add(new RoleListCommand());
        Subcommands.Add(new RoleGetCommand());
        Subcommands.Add(new RoleCreateCommand());
        Subcommands.Add(new RoleUpdateCommand());
        Subcommands.Add(new RoleDeleteCommand());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // role list
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RoleListCommand : Command
    {
        public RoleListCommand() : base("list", "List roles in the current tenant")
        {
            var realmIdOption = new Option<Guid?>("--realm-id") { Description = "Filter roles by realm ID" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Options.Add(realmIdOption);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var realmId = parseResult.GetValue(realmIdOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var format = parseResult.GetValue(formatOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var path = realmId.HasValue
                    ? $"admin/api/roles?realmId={realmId}"
                    : "admin/api/roles";
                var roles = await CliAdminApiClient.GetListAsync<RoleItem>(config, connection, path).ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(roles, SharedJsonOptions.IndentedOptions));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("ID")
                    .AddColumn("Name")
                    .AddColumn("Realm ID")
                    .AddColumn("Active");

                foreach (var r in roles)
                {
                    table.AddRow(
                        Markup.Escape(r.Id.ToString()),
                        Markup.Escape(r.Name),
                        Markup.Escape(r.RealmId.ToString()),
                        r.IsActive ? "yes" : "no");
                }

                AnsiConsole.Write(table);
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // role get <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RoleGetCommand : Command
    {
        public RoleGetCommand() : base("get", "Get details of a specific role by ID")
        {
            var idArg = new Argument<Guid>("id") { Description = "Role ID (GUID)" };
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
                var role = await CliAdminApiClient.GetAsync<RoleItem>(config, connection, $"admin/api/roles/{id}").ConfigureAwait(false);

                if (role is null)
                {
                    AnsiConsole.MarkupLine("[red]Role not found.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[bold]ID:[/]       {Markup.Escape(role.Id.ToString())}");
                AnsiConsole.MarkupLine($"[bold]Name:[/]     {Markup.Escape(role.Name)}");
                AnsiConsole.MarkupLine($"[bold]Realm ID:[/] {Markup.Escape(role.RealmId.ToString())}");
                AnsiConsole.MarkupLine($"[bold]Active:[/]   {(role.IsActive ? "yes" : "no")}");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // role create
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RoleCreateCommand : Command
    {
        public RoleCreateCommand() : base("create", "Create a new role in the current tenant")
        {
            var nameOption = new Option<string?>("--name") { Description = "Role name (required)" };
            var realmIdOption = new Option<Guid?>("--realm-id") { Description = "Realm ID the role belongs to (required)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Options.Add(nameOption);
            Options.Add(realmIdOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var name = parseResult.GetValue(nameOption)
                    ?? throw new InvalidOperationException("--name is required.");
                var realmId = parseResult.GetValue(realmIdOption)
                    ?? throw new InvalidOperationException("--realm-id is required.");
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var result = await CliAdminApiClient.PostAsync<RoleCreatedResult>(
                    config, connection, "admin/api/roles",
                    new { name, realmId }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Role created successfully.[/]");
                AnsiConsole.MarkupLine($"  [bold]ID:[/]       {Markup.Escape(result?.Id.ToString() ?? "-")}");
                AnsiConsole.MarkupLine($"  [bold]Name:[/]     {Markup.Escape(result?.Name ?? name)}");
                AnsiConsole.MarkupLine($"  [bold]Realm ID:[/] {Markup.Escape(result?.RealmId.ToString() ?? realmId.ToString())}");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // role update <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RoleUpdateCommand : Command
    {
        public RoleUpdateCommand() : base("update", "Update properties of an existing role")
        {
            var idArg = new Argument<Guid>("id") { Description = "Role ID (GUID)" };
            var nameOption = new Option<string?>("--name") { Description = "New role name" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(idArg);
            Options.Add(nameOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var name = parseResult.GetValue(nameOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PutAsync(
                    config, connection, $"admin/api/roles/{id}",
                    new { name }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Role {id} updated successfully.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // role delete <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RoleDeleteCommand : Command
    {
        public RoleDeleteCommand() : base("delete", "Delete a role from the current tenant")
        {
            var idArg = new Argument<Guid>("id") { Description = "Role ID (GUID)" };
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
                    if (!AnsiConsole.Confirm($"Delete role {id}? This cannot be undone.", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(config, connection, $"admin/api/roles/{id}").ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]Role {id} deleted.[/]");
            });
        }
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public sealed class RoleItem
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

public sealed class RoleCreatedResult
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("realmId")]
    public Guid RealmId { get; set; }
}
