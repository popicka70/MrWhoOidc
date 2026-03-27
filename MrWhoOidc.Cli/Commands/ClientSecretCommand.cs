using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Client secret lifecycle management: list, create, activate, set-primary, revoke.
/// Registered as nested subcommands under the existing ClientCommand.
/// </summary>
public sealed class ClientSecretCommand : Command
{
    public ClientSecretCommand() : base("secret", "Manage client secrets")
    {
        Subcommands.Add(new SecretListCommand());
        Subcommands.Add(new SecretCreateCommand());
        Subcommands.Add(new SecretActivateCommand());
        Subcommands.Add(new SecretSetPrimaryCommand());
        Subcommands.Add(new SecretRevokeCommand());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client secret list <clientId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class SecretListCommand : Command
    {
        public SecretListCommand() : base("list", "List all secrets for a client")
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
                var secrets = await CliAdminApiClient.GetListAsync<ClientSecretItem>(
                    config, connection, $"admin/api/clients/{clientId}/secrets").ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(secrets, new JsonSerializerOptions { WriteIndented = true }));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("ID")
                    .AddColumn("Description")
                    .AddColumn("Status")
                    .AddColumn("Expires At")
                    .AddColumn("Created At");

                foreach (var s in secrets)
                {
                    table.AddRow(
                        Markup.Escape(s.Id.ToString()),
                        Markup.Escape(s.Description ?? "-"),
                        Markup.Escape(s.Status ?? "-"),
                        Markup.Escape(s.ExpiresAt?.ToString("u") ?? "never"),
                        Markup.Escape(s.CreatedAt.ToString("u")));
                }

                AnsiConsole.Write(table);
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client secret create <clientId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class SecretCreateCommand : Command
    {
        public SecretCreateCommand() : base("create", "Create a new client secret (written to file, never printed)")
        {
            var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
            var expiresInDaysOption = new Option<int?>("--expires-in-days") { Description = "Expiry in days from now (max 730)" };
            var activateOption = new Option<bool>("--activate") { Description = "Immediately activate the new secret" };
            var descriptionOption = new Option<string?>("--description") { Description = "Human-readable description" };
            var outputOption = new Option<string?>("--output") { Description = "File path for the secret JSON" };
            var overwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite output file if it exists" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(clientIdArg);
            Options.Add(expiresInDaysOption);
            Options.Add(activateOption);
            Options.Add(descriptionOption);
            Options.Add(outputOption);
            Options.Add(overwriteOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdArg);
                var expiresInDays = parseResult.GetValue(expiresInDaysOption);
                var activate = parseResult.GetValue(activateOption);
                var description = parseResult.GetValue(descriptionOption);
                var output = parseResult.GetValue(outputOption);
                var overwrite = parseResult.GetValue(overwriteOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var result = await CliAdminApiClient.PostAsync<SecretCreatedResult>(
                    config, connection, $"admin/api/clients/{clientId}/secrets",
                    new { expiresInDays, activate, description }).ConfigureAwait(false);

                if (result is null || string.IsNullOrWhiteSpace(result.SecretValue))
                {
                    AnsiConsole.MarkupLine("[red]Secret creation failed: server returned an empty response.[/]");
                    return;
                }

                var credentials = new
                {
                    clientId,
                    secretId = result.Id,
                    secretValue = result.SecretValue,
                    status = result.Status,
                    expiresAt = result.ExpiresAt?.ToString("O"),
                    createdAt = DateTimeOffset.UtcNow.ToString("O"),
                    server = connection.ServerUrl,
                    warning = "Store this secret securely. It cannot be retrieved again."
                };

                var credJson = JsonSerializer.Serialize(credentials, new JsonSerializerOptions { WriteIndented = true });
                var suggestedFileName = $"client-{clientId}-secret-{result.Id}.json";
                await CliFileOutput.WriteTextAsync(credJson, suggestedFileName, output, overwrite).ConfigureAwait(false);
                var resolvedPath = CliFileOutput.ResolveOutputPath(suggestedFileName, output);

                AnsiConsole.MarkupLine("[green]Secret created successfully.[/]");
                AnsiConsole.MarkupLine($"  [bold]Secret ID:[/] {Markup.Escape(result.Id.ToString())}");
                AnsiConsole.MarkupLine($"  [bold]Status:[/]    {Markup.Escape(result.Status ?? "-")}");
                AnsiConsole.MarkupLine($"");
                AnsiConsole.MarkupLine($"[yellow]Secret written to:[/] {Markup.Escape(resolvedPath)}");
                AnsiConsole.MarkupLine("[grey]The credential file has owner-only permissions (600). Keep it safe.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client secret activate <clientId> <secretId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class SecretActivateCommand : Command
    {
        public SecretActivateCommand() : base("activate", "Activate a client secret")
        {
            var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
            var secretIdArg = new Argument<Guid>("secret-id") { Description = "Secret ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(clientIdArg);
            Arguments.Add(secretIdArg);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdArg);
                var secretId = parseResult.GetValue(secretIdArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PostAsync<object>(
                    config, connection, $"admin/api/clients/{clientId}/secrets/{secretId}/activate",
                    new { }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Secret {secretId} activated.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client secret set-primary <clientId> <secretId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class SecretSetPrimaryCommand : Command
    {
        public SecretSetPrimaryCommand() : base("set-primary", "Set a client secret as the primary secret")
        {
            var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
            var secretIdArg = new Argument<Guid>("secret-id") { Description = "Secret ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(clientIdArg);
            Arguments.Add(secretIdArg);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdArg);
                var secretId = parseResult.GetValue(secretIdArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                await CliAdminApiClient.PostAsync<object>(
                    config, connection, $"admin/api/clients/{clientId}/secrets/{secretId}/set-primary",
                    new { }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Secret {secretId} set as primary.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // client secret revoke <clientId> <secretId>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class SecretRevokeCommand : Command
    {
        public SecretRevokeCommand() : base("revoke", "Revoke (delete) a client secret")
        {
            var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
            var secretIdArg = new Argument<Guid>("secret-id") { Description = "Secret ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var confirmOption = new Option<bool>("--confirm") { Description = "Skip the confirmation prompt" };

            Arguments.Add(clientIdArg);
            Arguments.Add(secretIdArg);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(confirmOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdArg);
                var secretId = parseResult.GetValue(secretIdArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var confirm = parseResult.GetValue(confirmOption);

                if (!confirm)
                {
                    if (!AnsiConsole.Confirm($"Revoke secret {secretId}? This cannot be undone.", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.DeleteAsync(
                    config, connection, $"admin/api/clients/{clientId}/secrets/{secretId}").ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Secret {secretId} revoked.[/]");
            });
        }
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public sealed class ClientSecretItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SecretCreatedResult
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("secretValue")]
    public string? SecretValue { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}
