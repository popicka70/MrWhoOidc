using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Manages user accounts within the current tenant.
/// User creation always writes credentials to a file to avoid secrets in terminal history.
/// </summary>
public sealed class UserCommand : Command
{
    public UserCommand() : base("user", "Manage users and access bindings within the current tenant")
    {
        Subcommands.Add(new UserListCommand());
        Subcommands.Add(new UserGetCommand());
        Subcommands.Add(new UserCreateCommand());
        Subcommands.Add(new UserUpdateCommand());
        Subcommands.Add(new UserDeleteCommand());
        Subcommands.Add(new UserDeactivateCommand());
        Subcommands.Add(new UserReactivateCommand());
        Subcommands.Add(new UserUnassignedCommand());
        Subcommands.Add(new UserRoleCommand());
        Subcommands.Add(new UserClientCommand());
    }

    private static AuthenticatedConnection ResolvePlatformConnection(CliConfig config, string? server, string? profileName)
    {
        var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profileName);
        if (!connection.Profile.IsPlatformAdmin)
        {
            throw new InvalidOperationException("Unassigned user account operations require a platform-admin profile.");
        }

        var platformServer = CliServerConnection.GetPlatformServerUrl(connection.ServerUrl);
        return new AuthenticatedConnection(connection.ProfileName, platformServer, connection.Profile);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // user list
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class UserListCommand : Command
    {
        public UserListCommand() : base("list", "List users in the current tenant")
        {
            var searchOption = new Option<string?>("--search") { Description = "Filter by username, email, or name" };
            var skipOption = new Option<int?>("--skip") { Description = "Skip this many results (for pagination)" };
            var takeOption = new Option<int?>("--take") { Description = "Return at most this many results (max 500, default 50)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Options.Add(searchOption);
            Options.Add(skipOption);
            Options.Add(takeOption);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var search = parseResult.GetValue(searchOption);
                var skip = parseResult.GetValue(skipOption);
                var take = parseResult.GetValue(takeOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var format = parseResult.GetValue(formatOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var qp = new List<string>();
                if (!string.IsNullOrWhiteSpace(search)) qp.Add($"search={Uri.EscapeDataString(search)}");
                if (skip.HasValue) qp.Add($"skip={skip}");
                if (take.HasValue) qp.Add($"take={take}");
                var path = qp.Count > 0 ? $"admin/api/users?{string.Join('&', qp)}" : "admin/api/users";

                var page = await CliAdminApiClient.GetAsync<UserListPage>(config, connection, path).ConfigureAwait(false);
                var users = page?.Items ?? [];

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(page, SharedJsonOptions.IndentedOptions));
                    return;
                }

                AnsiConsole.MarkupLine($"[grey]Total: {page?.Total ?? users.Count}[/]");

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("ID")
                    .AddColumn("Username")
                    .AddColumn("Email")
                    .AddColumn("Name")
                    .AddColumn("Email Verified")
                    .AddColumn("MFA");

                foreach (var u in users)
                {
                    table.AddRow(
                        Markup.Escape(u.Id.ToString()),
                        Markup.Escape(u.Username),
                        Markup.Escape(u.Email ?? "-"),
                        Markup.Escape(u.Name ?? "-"),
                        u.EmailVerified ? "yes" : "no",
                        u.TotpEnabled ? "totp" : "none");
                }

                AnsiConsole.Write(table);
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // user get <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class UserGetCommand : Command
    {
        public UserGetCommand() : base("get", "Get details of a specific user by ID")
        {
            var idArg = new Argument<Guid>("id") { Description = "User ID (GUID)" };
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
                var user = await CliAdminApiClient.GetAsync<UserItem>(config, connection, $"admin/api/users/{id}").ConfigureAwait(false);

                if (user is null)
                {
                    AnsiConsole.MarkupLine("[red]User not found.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[bold]ID:[/]             {Markup.Escape(user.Id.ToString())}");
                AnsiConsole.MarkupLine($"[bold]Username:[/]       {Markup.Escape(user.Username)}");
                AnsiConsole.MarkupLine($"[bold]Email:[/]          {Markup.Escape(user.Email ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Name:[/]           {Markup.Escape(user.Name ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Email Verified:[/] {(user.EmailVerified ? "yes" : "no")}");
                AnsiConsole.MarkupLine($"[bold]MFA:[/]            {(user.TotpEnabled ? "TOTP enabled" : "none")}");
                AnsiConsole.MarkupLine($"[bold]Created At:[/]     {Markup.Escape(user.CreatedAt.ToString("u"))}");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // user create
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class UserCreateCommand : Command
    {
        public UserCreateCommand() : base("create",
            "Create a new user. Credentials are written to a file (never printed to the terminal).")
        {
            var usernameOption = new Option<string>("--username") { Description = "Username (required)" };
            var emailOption = new Option<string?>("--email") { Description = "Email address" };
            var nameOption = new Option<string?>("--name") { Description = "Display name" };
            var passwordOption = new Option<string?>("--password") { Description = "Set a specific password (if omitted, a secure random password is generated)" };
            var outputOption = new Option<string?>("--output") { Description = "File path for the credentials JSON output (defaults to ~/.mrwho-cli/exports/user-<username>-credentials.json)" };
            var overwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite the output file if it already exists" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Options.Add(usernameOption);
            Options.Add(emailOption);
            Options.Add(nameOption);
            Options.Add(passwordOption);
            Options.Add(outputOption);
            Options.Add(overwriteOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var username = parseResult.GetValue(usernameOption)
                    ?? throw new InvalidOperationException("--username is required.");
                var email = parseResult.GetValue(emailOption);
                var name = parseResult.GetValue(nameOption);
                var password = parseResult.GetValue(passwordOption);
                var output = parseResult.GetValue(outputOption);
                var overwrite = parseResult.GetValue(overwriteOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var result = await CliAdminApiClient.PostAsync<UserCreatedResult>(
                    config, connection, "admin/api/users",
                    new { username, email, name, password }).ConfigureAwait(false);

                if (result is null)
                {
                    AnsiConsole.MarkupLine("[red]User creation failed: server returned an empty response.[/]");
                    return;
                }

                // Write credentials to file — never print password to terminal
                var credentials = new
                {
                    userId = result.Id,
                    username = result.Username,
                    email = result.Email,
                    name = result.Name,
                    password = result.Password,
                    createdAt = DateTimeOffset.UtcNow.ToString("O"),
                    server = connection.ServerUrl,
                    warning = result.Warning
                };
                var credJson = JsonSerializer.Serialize(credentials, SharedJsonOptions.IndentedOptions);

                var suggestedFileName = $"user-{username}-credentials.json";
                await CliFileOutput.WriteTextAsync(credJson, suggestedFileName, output, overwrite).ConfigureAwait(false);
                var resolvedPath = CliFileOutput.ResolveOutputPath(suggestedFileName, output);

                AnsiConsole.MarkupLine($"[green]User created successfully.[/]");
                AnsiConsole.MarkupLine($"  [bold]ID:[/]       {Markup.Escape(result.Id.ToString())}");
                AnsiConsole.MarkupLine($"  [bold]Username:[/] {Markup.Escape(result.Username)}");
                AnsiConsole.MarkupLine($"  [bold]Email:[/]    {Markup.Escape(result.Email ?? "-")}");
                AnsiConsole.MarkupLine($"");
                AnsiConsole.MarkupLine($"[yellow]Credentials written to:[/] {Markup.Escape(resolvedPath)}");
                AnsiConsole.MarkupLine($"[grey]The credential file has owner-only permissions (600). Keep it safe.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // user update <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class UserUpdateCommand : Command
    {
        public UserUpdateCommand() : base("update", "Update properties of an existing user")
        {
            var idArg = new Argument<Guid>("id") { Description = "User ID (GUID)" };
            var nameOption = new Option<string?>("--name") { Description = "New display name" };
            var emailOption = new Option<string?>("--email") { Description = "New email address" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(idArg);
            Options.Add(nameOption);
            Options.Add(emailOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var name = parseResult.GetValue(nameOption);
                var email = parseResult.GetValue(emailOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PutAsync(
                    config, connection, $"admin/api/users/{id}",
                    new { name, email }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]User {id} updated successfully.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // user delete <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class UserDeleteCommand : Command
    {
        public UserDeleteCommand() : base("delete", "Delete a user from the current tenant")
        {
            var idArg = new Argument<Guid>("id") { Description = "User ID (GUID)" };
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
                    if (!AnsiConsole.Confirm($"Delete user {id}? This cannot be undone.", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(config, connection, $"admin/api/users/{id}").ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]User {id} deleted.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // user deactivate <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class UserDeactivateCommand : Command
    {
        public UserDeactivateCommand() : base("deactivate", "Deactivate a user (blocks login, preserves data)")
        {
            var idArg = new Argument<Guid>("id") { Description = "User ID (GUID)" };
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
                    if (!AnsiConsole.Confirm($"Deactivate user {id}? They will no longer be able to log in, but their data is preserved.", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.PostAsync<object>(
                    config, connection, $"admin/api/users/{id}/deactivate", new { }).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]User {id} deactivated.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // user reactivate <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class UserReactivateCommand : Command
    {
        public UserReactivateCommand() : base("reactivate", "Reactivate a deactivated user")
        {
            var idArg = new Argument<Guid>("id") { Description = "User ID (GUID)" };
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
                    if (!AnsiConsole.Confirm($"Reactivate user {id}? They will be able to log in again.", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.PostAsync<object>(
                    config, connection, $"admin/api/users/{id}/reactivate", new { }).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]User {id} reactivated.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // user unassigned ...
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class UserUnassignedCommand : Command
    {
        public UserUnassignedCommand() : base("unassigned", "Manage platform accounts with no active tenant membership")
        {
            Subcommands.Add(new ListCommand());
            Subcommands.Add(new GetCommand());
            Subcommands.Add(new TerminateCommand());
        }

        private sealed class ListCommand : Command
        {
            public ListCommand() : base("list", "List platform accounts with no active tenant membership")
            {
                var searchOption = new Option<string?>("--search") { Description = "Filter by username, email, or name" };
                var skipOption = new Option<int?>("--skip") { Description = "Skip this many results (for pagination)" };
                var takeOption = new Option<int?>("--take") { Description = "Return at most this many results (max 500, default 50)" };
                var serverOption = new Option<string?>("--server") { Description = "Server URL" };
                var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
                var formatOption = new Option<OutputFormat>("--format")
                {
                    Description = "Output format: table or json",
                    DefaultValueFactory = _ => OutputFormat.Table
                };

                Options.Add(searchOption);
                Options.Add(skipOption);
                Options.Add(takeOption);
                Options.Add(serverOption);
                Options.Add(profileOption);
                Options.Add(formatOption);

                this.SetSafeAction(async parseResult =>
                {
                    var search = parseResult.GetValue(searchOption);
                    var skip = parseResult.GetValue(skipOption);
                    var take = parseResult.GetValue(takeOption);
                    var server = parseResult.GetValue(serverOption);
                    var profile = parseResult.GetValue(profileOption);
                    var format = parseResult.GetValue(formatOption);

                    var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                    var connection = ResolvePlatformConnection(config, server, profile);

                    var qp = new List<string>();
                    if (!string.IsNullOrWhiteSpace(search)) qp.Add($"search={Uri.EscapeDataString(search)}");
                    if (skip.HasValue) qp.Add($"skip={skip}");
                    if (take.HasValue) qp.Add($"take={take}");
                    var path = qp.Count > 0
                        ? $"platform-admin/api/users/unassigned?{string.Join('&', qp)}"
                        : "platform-admin/api/users/unassigned";

                    var page = await CliAdminApiClient.GetAsync<PlatformUserAccountListPage>(config, connection, path).ConfigureAwait(false);
                    var users = page?.Items ?? [];

                    if (format == OutputFormat.Json)
                    {
                        Console.Out.WriteLine(JsonSerializer.Serialize(page, SharedJsonOptions.IndentedOptions));
                        return;
                    }

                    AnsiConsole.MarkupLine($"[grey]Total: {page?.Total ?? users.Count}[/]");
                    var table = new Table().Border(TableBorder.Rounded)
                        .AddColumn("ID")
                        .AddColumn("Username")
                        .AddColumn("Email")
                        .AddColumn("Name")
                        .AddColumn("MFA")
                        .AddColumn("Memberships")
                        .AddColumn("Created");

                    foreach (var user in users)
                    {
                        table.AddRow(
                            Markup.Escape(user.Id.ToString()),
                            Markup.Escape(user.Username),
                            Markup.Escape(user.Email ?? "-"),
                            Markup.Escape(user.Name ?? "-"),
                            user.TotpEnabled ? "totp" : "none",
                            user.MembershipCount.ToString(),
                            Markup.Escape(user.CreatedAt.ToString("u")));
                    }

                    AnsiConsole.Write(table);
                });
            }
        }

        private sealed class GetCommand : Command
        {
            public GetCommand() : base("get", "Get an unassigned platform account by ID")
            {
                var idArg = new Argument<Guid>("id") { Description = "User account ID (GUID)" };
                var serverOption = new Option<string?>("--server") { Description = "Server URL" };
                var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
                var formatOption = new Option<OutputFormat>("--format")
                {
                    Description = "Output format: table or json",
                    DefaultValueFactory = _ => OutputFormat.Table
                };

                Arguments.Add(idArg);
                Options.Add(serverOption);
                Options.Add(profileOption);
                Options.Add(formatOption);

                this.SetSafeAction(async parseResult =>
                {
                    var id = parseResult.GetValue(idArg);
                    var server = parseResult.GetValue(serverOption);
                    var profile = parseResult.GetValue(profileOption);
                    var format = parseResult.GetValue(formatOption);

                    var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                    var connection = ResolvePlatformConnection(config, server, profile);
                    var user = await CliAdminApiClient.GetAsync<PlatformUserAccountItem>(
                        config, connection, $"platform-admin/api/users/unassigned/{id}").ConfigureAwait(false);

                    if (user is null)
                    {
                        AnsiConsole.MarkupLine("[red]Unassigned user account not found.[/]");
                        return;
                    }

                    if (format == OutputFormat.Json)
                    {
                        Console.Out.WriteLine(JsonSerializer.Serialize(user, SharedJsonOptions.IndentedOptions));
                        return;
                    }

                    AnsiConsole.MarkupLine($"[bold]ID:[/]                  {Markup.Escape(user.Id.ToString())}");
                    AnsiConsole.MarkupLine($"[bold]Username:[/]            {Markup.Escape(user.Username)}");
                    AnsiConsole.MarkupLine($"[bold]Email:[/]               {Markup.Escape(user.Email ?? "-")}");
                    AnsiConsole.MarkupLine($"[bold]Name:[/]                {Markup.Escape(user.Name ?? "-")}");
                    AnsiConsole.MarkupLine($"[bold]Email Verified:[/]      {(user.EmailVerified ? "yes" : "no")}");
                    AnsiConsole.MarkupLine($"[bold]MFA:[/]                 {(user.TotpEnabled ? "TOTP enabled" : "none")}");
                    AnsiConsole.MarkupLine($"[bold]Memberships:[/]         {user.MembershipCount}");
                    AnsiConsole.MarkupLine($"[bold]Active Memberships:[/]  {user.ActiveMembershipCount}");
                    AnsiConsole.MarkupLine($"[bold]Created At:[/]          {Markup.Escape(user.CreatedAt.ToString("u"))}");
                });
            }
        }

        private sealed class TerminateCommand : Command
        {
            public TerminateCommand() : base("terminate", "Terminate an unassigned platform account")
            {
                var idArg = new Argument<Guid>("id") { Description = "User account ID (GUID)" };
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
                        if (!AnsiConsole.Confirm($"Terminate unassigned user account {id}? This cannot be undone.", defaultValue: false))
                        {
                            AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                            return;
                        }
                    }

                    var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                    var connection = ResolvePlatformConnection(config, server, profile);
                    await CliAdminApiClient.DeleteAsync(
                        config, connection, $"platform-admin/api/users/unassigned/{id}").ConfigureAwait(false);
                    AnsiConsole.MarkupLine($"[green]Unassigned user account {id} terminated.[/]");
                });
            }
        }
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public sealed class UserItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("emailVerified")]
    public bool EmailVerified { get; set; }

    [JsonPropertyName("totpEnabled")]
    public bool TotpEnabled { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class UserListPage
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("items")]
    public List<UserItem> Items { get; set; } = [];
}

public sealed class UserCreatedResult
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("warning")]
    public string? Warning { get; set; }
}

public sealed class PlatformUserAccountItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("emailVerified")]
    public bool EmailVerified { get; set; }

    [JsonPropertyName("totpEnabled")]
    public bool TotpEnabled { get; set; }

    [JsonPropertyName("lockedOutUntil")]
    public DateTimeOffset? LockedOutUntil { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("failedLoginAttempts")]
    public int FailedLoginAttempts { get; set; }

    [JsonPropertyName("lastFailedLoginAt")]
    public DateTimeOffset? LastFailedLoginAt { get; set; }

    [JsonPropertyName("membershipCount")]
    public int MembershipCount { get; set; }

    [JsonPropertyName("activeMembershipCount")]
    public int ActiveMembershipCount { get; set; }
}

public sealed class PlatformUserAccountListPage
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("items")]
    public List<PlatformUserAccountItem> Items { get; set; } = [];
}
