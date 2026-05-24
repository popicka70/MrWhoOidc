using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

public sealed class InvitationCommand : Command
{
    public InvitationCommand() : base("invitation", "Manage tenant user invitations")
    {
        Subcommands.Add(new InvitationListCommand());
        Subcommands.Add(new InvitationCreateCommand());
        Subcommands.Add(new InvitationRevokeCommand());
    }

    private sealed class InvitationListCommand : Command
    {
        public InvitationListCommand() : base("list", "List invitations in the current tenant")
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
                var invitations = await CliAdminApiClient.GetListAsync<TenantInvitationCliItem>(
                    config, connection, "admin/api/invitations").ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(invitations, SharedJsonOptions.IndentedOptions));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("ID")
                    .AddColumn("Email")
                    .AddColumn("Name")
                    .AddColumn("Status")
                    .AddColumn("Role")
                    .AddColumn("Expires")
                    .AddColumn("Invited By");

                foreach (var invitation in invitations)
                {
                    table.AddRow(
                        Markup.Escape(invitation.Id.ToString()),
                        Markup.Escape(invitation.Email),
                        Markup.Escape(invitation.DisplayName ?? "-"),
                        Markup.Escape(invitation.Status),
                        invitation.IsTenantAdmin ? "tenant-admin" : "member",
                        Markup.Escape(invitation.ExpiresAt.ToString("u")),
                        Markup.Escape(invitation.InvitedByUsername ?? "-"));
                }

                AnsiConsole.Write(table);
            });
        }
    }

    private sealed class InvitationCreateCommand : Command
    {
        public InvitationCreateCommand() : base("create", "Create a tenant invitation and return its one-time invitation link")
        {
            var emailOption = new Option<string>("--email") { Description = "Invited user's email address (required)" };
            var displayNameOption = new Option<string?>("--display-name") { Description = "Display name to apply when accepted" };
            var tenantAdminOption = new Option<bool>("--tenant-admin") { Description = "Invite the user as a tenant admin" };
            var validDaysOption = new Option<int>("--valid-days")
            {
                Description = "Number of days before the invitation expires (1-90)",
                DefaultValueFactory = _ => 7
            };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Options.Add(emailOption);
            Options.Add(displayNameOption);
            Options.Add(tenantAdminOption);
            Options.Add(validDaysOption);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var email = parseResult.GetValue(emailOption)
                    ?? throw new InvalidOperationException("--email is required.");
                var displayName = parseResult.GetValue(displayNameOption);
                var tenantAdmin = parseResult.GetValue(tenantAdminOption);
                var validDays = parseResult.GetValue(validDaysOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var format = parseResult.GetValue(formatOption);

                if (string.IsNullOrWhiteSpace(email))
                    throw new InvalidOperationException("--email is required.");
                if (validDays is < 1 or > 90)
                    throw new InvalidOperationException("--valid-days must be between 1 and 90.");

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var result = await CliAdminApiClient.PostAsync<TenantInvitationCreatedCliResult>(
                    config,
                    connection,
                    "admin/api/invitations",
                    new { email, displayName, isTenantAdmin = tenantAdmin, validDays }).ConfigureAwait(false);

                if (result is null)
                {
                    AnsiConsole.MarkupLine("[red]Invitation creation failed: server returned an empty response.[/]");
                    return;
                }

                if (format == OutputFormat.Json)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(result, SharedJsonOptions.IndentedOptions));
                    return;
                }

                AnsiConsole.MarkupLine("[green]Invitation created successfully.[/]");
                if (result.Invitation is not null)
                {
                    AnsiConsole.MarkupLine($"  [bold]ID:[/]      {Markup.Escape(result.Invitation.Id.ToString())}");
                    AnsiConsole.MarkupLine($"  [bold]Email:[/]   {Markup.Escape(result.Invitation.Email)}");
                    AnsiConsole.MarkupLine($"  [bold]Role:[/]    {(result.Invitation.IsTenantAdmin ? "tenant-admin" : "member")}");
                    AnsiConsole.MarkupLine($"  [bold]Expires:[/] {Markup.Escape(result.Invitation.ExpiresAt.ToString("u"))}");
                }
                AnsiConsole.MarkupLine($"  [bold]Link:[/]    {Markup.Escape(result.InvitationLink ?? "-")}");
            });
        }
    }

    private sealed class InvitationRevokeCommand : Command
    {
        public InvitationRevokeCommand() : base("revoke", "Revoke a pending tenant invitation")
        {
            var idArg = new Argument<Guid>("id") { Description = "Invitation ID (GUID)" };
            var reasonOption = new Option<string?>("--reason") { Description = "Revocation reason" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(idArg);
            Options.Add(reasonOption);
            Options.Add(confirmOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var reason = parseResult.GetValue(reasonOption);
                var confirm = parseResult.GetValue(confirmOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Revoke invitation {id}?", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var path = string.IsNullOrWhiteSpace(reason)
                    ? $"admin/api/invitations/{id}"
                    : $"admin/api/invitations/{id}?reason={Uri.EscapeDataString(reason)}";

                await CliAdminApiClient.DeleteAsync(config, connection, path).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]Invitation {id} revoked.[/]");
            });
        }
    }
}

public sealed class TenantInvitationCliItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("isTenantAdmin")]
    public bool IsTenantAdmin { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("acceptedAt")]
    public DateTimeOffset? AcceptedAt { get; set; }

    [JsonPropertyName("revokedAt")]
    public DateTimeOffset? RevokedAt { get; set; }

    [JsonPropertyName("invitedByUsername")]
    public string? InvitedByUsername { get; set; }
}

public sealed class TenantInvitationCreatedCliResult
{
    [JsonPropertyName("invitation")]
    public TenantInvitationCliItem? Invitation { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("invitationLink")]
    public string? InvitationLink { get; set; }
}