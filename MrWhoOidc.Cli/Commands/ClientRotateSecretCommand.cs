using System.CommandLine;
using System.Text.Json;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Compound operation: create new secret → activate → optionally revoke oldest.
/// Performs safe zero-downtime secret rotation in a single command.
/// Calls existing secret APIs in sequence; no new backend needed.
/// </summary>
public sealed class ClientRotateSecretCommand : Command
{
    public ClientRotateSecretCommand() : base("rotate-secret",
        "Rotate client secret: create new → activate → optionally revoke oldest")
    {
        var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
        var expiresInDaysOption = new Option<int>("--expires-in-days")
        {
            Description = "Expiry for the new secret in days (max 730)",
            DefaultValueFactory = _ => 90
        };
        var revokeOldestOption = new Option<bool>("--revoke-oldest")
        {
            Description = "Automatically revoke the oldest secret after rotation (only if >2 secrets remain)"
        };
        var descriptionOption = new Option<string?>("--description") { Description = "Description for the new secret" };
        var outputOption = new Option<string?>("--output") { Description = "File path for the secret JSON" };
        var overwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite output file if it exists" };
        var confirmOption = new Option<bool>("--confirm") { Description = "Skip confirmation prompts" };
        var serverOption = new Option<string?>("--server") { Description = "Server URL" };
        var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

        Arguments.Add(clientIdArg);
        Options.Add(expiresInDaysOption);
        Options.Add(revokeOldestOption);
        Options.Add(descriptionOption);
        Options.Add(outputOption);
        Options.Add(overwriteOption);
        Options.Add(confirmOption);
        Options.Add(serverOption);
        Options.Add(profileOption);

        this.SetSafeAction(async parseResult =>
        {
            var clientId = parseResult.GetValue(clientIdArg);
            var expiresInDays = parseResult.GetValue(expiresInDaysOption);
            var revokeOldest = parseResult.GetValue(revokeOldestOption);
            var description = parseResult.GetValue(descriptionOption);
            var output = parseResult.GetValue(outputOption);
            var overwrite = parseResult.GetValue(overwriteOption);
            var confirm = parseResult.GetValue(confirmOption);
            var server = parseResult.GetValue(serverOption);
            var profile = parseResult.GetValue(profileOption);

            await HandleAsync(clientId, expiresInDays, revokeOldest, description,
                output, overwrite, confirm, server, profile).ConfigureAwait(false);
        });
    }

    private static async Task HandleAsync(Guid clientId, int expiresInDays, bool revokeOldest,
        string? description, string? output, bool overwrite, bool confirm,
        string? server, string? profileName)
    {
        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profileName);

        // Step 1: Show current secret state
        AnsiConsole.MarkupLine("[bold]Step 1:[/] Checking current secrets...");
        var existingSecrets = await CliAdminApiClient.GetListAsync<ClientSecretItem>(
            config, connection, $"admin/api/clients/{clientId}/secrets").ConfigureAwait(false);

        var activeCount = existingSecrets.Count(s =>
            string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Status, "Active", StringComparison.OrdinalIgnoreCase));
        AnsiConsole.MarkupLine($"  Found {existingSecrets.Count} total secret(s), {activeCount} active.");

        // Step 2: Confirm rotation
        if (!confirm)
        {
            var proceed = AnsiConsole.Confirm(
                $"Create a new secret (expires in {expiresInDays} days) and activate it?",
                defaultValue: true);
            if (!proceed)
            {
                AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                return;
            }
        }

        // Step 3: Create new secret with activate=true
        AnsiConsole.MarkupLine("[bold]Step 2:[/] Creating and activating new secret...");
        var rotationDesc = description ?? $"Rotated on {DateTimeOffset.UtcNow:yyyy-MM-dd}";
        var result = await CliAdminApiClient.PostAsync<SecretCreatedResult>(
            config, connection, $"admin/api/clients/{clientId}/secrets",
            new { expiresInDays, activate = true, description = rotationDesc }).ConfigureAwait(false);

        if (result is null || string.IsNullOrWhiteSpace(result.SecretValue))
        {
            AnsiConsole.MarkupLine("[red]Secret creation failed: server returned an empty response.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"  [green]New secret created:[/] {Markup.Escape(result.Id.ToString())}");
        AnsiConsole.MarkupLine($"  [bold]Status:[/]  {Markup.Escape(result.Status ?? "-")}");
        AnsiConsole.MarkupLine($"  [bold]Expires:[/] {(result.ExpiresAt?.ToString("u") ?? "never")}");

        // Step 4: Write secret to file (NEVER echo to terminal)
        var credentials = new
        {
            clientId,
            secretId = result.Id,
            secretValue = result.SecretValue,
            status = result.Status,
            expiresAt = result.ExpiresAt?.ToString("O"),
            rotatedAt = DateTimeOffset.UtcNow.ToString("O"),
            server = connection.ServerUrl,
            warning = "Store this secret securely. It cannot be retrieved again."
        };
        var credJson = JsonSerializer.Serialize(credentials, new JsonSerializerOptions { WriteIndented = true });
        var suggestedFileName = $"client-{clientId}-secret-{result.Id}.json";
        await CliFileOutput.WriteTextAsync(credJson, suggestedFileName, output, overwrite).ConfigureAwait(false);
        var resolvedPath = CliFileOutput.ResolveOutputPath(suggestedFileName, output);

        AnsiConsole.MarkupLine($"\n[yellow]Secret written to:[/] {Markup.Escape(resolvedPath)}");
        AnsiConsole.MarkupLine("[grey]The credential file has owner-only permissions (600). Keep it safe.[/]");

        // Step 5: Optionally revoke oldest secret
        if (revokeOldest)
        {
            // Refresh secret list
            var refreshedSecrets = await CliAdminApiClient.GetListAsync<ClientSecretItem>(
                config, connection, $"admin/api/clients/{clientId}/secrets").ConfigureAwait(false);

            // Only revoke if we'd leave at least 2 secrets (the new one + one backup)
            if (refreshedSecrets.Count > 2)
            {
                var oldest = refreshedSecrets
                    .Where(s => s.Id != result.Id)
                    .OrderBy(s => s.CreatedAt)
                    .First();

                AnsiConsole.MarkupLine($"\n[bold]Step 3:[/] Revoking oldest secret {Markup.Escape(oldest.Id.ToString())} " +
                    $"(created {oldest.CreatedAt:yyyy-MM-dd})...");

                await CliAdminApiClient.DeleteAsync(
                    config, connection, $"admin/api/clients/{clientId}/secrets/{oldest.Id}").ConfigureAwait(false);

                AnsiConsole.MarkupLine($"  [green]Secret {Markup.Escape(oldest.Id.ToString())} revoked.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("\n[grey]Skipping revocation: only " +
                    $"{refreshedSecrets.Count} secret(s) remain (minimum 2 required for safe rotation).[/]");
            }
        }

        AnsiConsole.MarkupLine("\n[green]Secret rotation complete.[/]");
    }
}
