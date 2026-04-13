using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Manages realms within the current tenant.
/// Realms are security domains that group clients and roles.
/// </summary>
public sealed class RealmCommand : Command
{
    public RealmCommand() : base("realm", "Manage realms that partition clients, users, and roles")
    {
        Subcommands.Add(new RealmListCommand());
        Subcommands.Add(new RealmGetCommand());
        Subcommands.Add(new RealmCreateCommand());
        Subcommands.Add(new RealmUpdateCommand());
        Subcommands.Add(new RealmDeleteCommand());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // realm list
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RealmListCommand : Command
    {
        public RealmListCommand() : base("list", "List all realms in the current tenant")
        {
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var format = parseResult.GetValue(formatOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var realms = await CliAdminApiClient.GetListAsync<RealmItem>(config, connection, "admin/api/realms").ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(realms, SharedJsonOptions.IndentedOptions));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("ID")
                    .AddColumn("Name")
                    .AddColumn("Display Name")
                    .AddColumn("Allow Unconfirmed Login");

                foreach (var r in realms)
                {
                    table.AddRow(
                        Markup.Escape(r.Id.ToString()),
                        Markup.Escape(r.Name),
                        Markup.Escape(r.DisplayName ?? "-"),
                        r.AllowUnconfirmedLogin ? "yes" : "no");
                }

                AnsiConsole.Write(table);
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // realm get <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RealmGetCommand : Command
    {
        public RealmGetCommand() : base("get", "Get details of a specific realm by ID")
        {
            var idArg = new Argument<Guid>("id") { Description = "Realm ID (GUID)" };
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
                var realm = await CliAdminApiClient.GetAsync<RealmItem>(config, connection, $"admin/api/realms/{id}").ConfigureAwait(false);

                if (realm is null)
                {
                    AnsiConsole.MarkupLine("[red]Realm not found.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[bold]ID:[/]                    {Markup.Escape(realm.Id.ToString())}");
                AnsiConsole.MarkupLine($"[bold]Name:[/]                  {Markup.Escape(realm.Name)}");
                AnsiConsole.MarkupLine($"[bold]Display Name:[/]          {Markup.Escape(realm.DisplayName ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Allow Unconfirmed Login:[/] {(realm.AllowUnconfirmedLogin ? "yes" : "no")}");
                AnsiConsole.MarkupLine($"[bold]Created At:[/]            {Markup.Escape(realm.CreatedAt.ToString("u"))}");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // realm create
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RealmCreateCommand : Command
    {
        public RealmCreateCommand() : base("create", "Create a new realm in the current tenant")
        {
            var nameOption = new Option<string?>("--name") { Description = "Realm name (slug, e.g. 'customers')" };
            var displayNameOption = new Option<string?>("--display-name") { Description = "Human-readable display name" };
            var allowUnconfirmedOption = new Option<bool?>("--allow-unconfirmed-login")
            {
                Description = "Allow users to log in without confirming their email (default: true)"
            };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Options.Add(nameOption);
            Options.Add(displayNameOption);
            Options.Add(allowUnconfirmedOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var name = parseResult.GetValue(nameOption)
                    ?? throw new InvalidOperationException("--name is required.");
                var displayName = parseResult.GetValue(displayNameOption);
                var allowUnconfirmed = parseResult.GetValue(allowUnconfirmedOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var result = await CliAdminApiClient.PostAsync<RealmCreatedResult>(
                    config, connection, "admin/api/realms",
                    new { name, displayName, allowUnconfirmedLogin = allowUnconfirmed }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Realm created successfully.[/]");
                AnsiConsole.MarkupLine($"  [bold]ID:[/]           {Markup.Escape(result?.Id.ToString() ?? "-")}");
                AnsiConsole.MarkupLine($"  [bold]Name:[/]         {Markup.Escape(result?.Name ?? name)}");
                AnsiConsole.MarkupLine($"  [bold]Display Name:[/] {Markup.Escape(result?.DisplayName ?? displayName ?? "-")}");
            });
        }
    }
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RealmUpdateCommand : Command
    {
        public RealmUpdateCommand() : base("update", "Update properties of an existing realm")
        {
            var idArg = new Argument<Guid>("id") { Description = "Realm ID (GUID)" };
            var nameOption = new Option<string?>("--name") { Description = "New realm name (slug)" };
            var displayNameOption = new Option<string?>("--display-name") { Description = "New display name" };
            var allowUnconfirmedOption = new Option<bool?>("--allow-unconfirmed-login") { Description = "Allow unconfirmed login (true/false)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(idArg);
            Options.Add(nameOption);
            Options.Add(displayNameOption);
            Options.Add(allowUnconfirmedOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var name = parseResult.GetValue(nameOption);
                var displayName = parseResult.GetValue(displayNameOption);
                var allowUnconfirmed = parseResult.GetValue(allowUnconfirmedOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PutAsync(
                    config, connection, $"admin/api/realms/{id}",
                    new { name, displayName, allowUnconfirmedLogin = allowUnconfirmed }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Realm {id} updated successfully.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // realm delete <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RealmDeleteCommand : Command
    {
        public RealmDeleteCommand() : base("delete", "Delete a realm (realm must have no clients)")
        {
            var idArg = new Argument<Guid>("id") { Description = "Realm ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };

            Arguments.Add(idArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(confirmOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var confirm = parseResult.GetValue(confirmOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Delete realm {id}? This cannot be undone.", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(config, connection, $"admin/api/realms/{id}").ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]Realm {id} deleted.[/]");
            });
        }
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public sealed class RealmItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("allowUnconfirmedLogin")]
    public bool AllowUnconfirmedLogin { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RealmCreatedResult
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("allowUnconfirmedLogin")]
    public bool AllowUnconfirmedLogin { get; set; }
}
