using System.CommandLine;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Imports IdP configuration manifests that were previously exported.
/// Supports tenant, realm, client, and identity-provider manifest files.
/// </summary>
public sealed class ImportCommand : Command
{
    public ImportCommand() : base("import", "Import IdP configuration manifests from files")
    {
        Subcommands.Add(new ImportPreviewCommand());
        Subcommands.Add(new ImportApplyCommand());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Sub-commands
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class ImportPreviewCommand : Command
    {
        public ImportPreviewCommand() : base("preview",
            "Preview a manifest import: show what will be created/updated and list conflicts — does NOT apply changes")
        {
            ConfigureShared(this, async (file, conflictResolution, _, realmId, clientSecret, server, profile) =>
                await HandleAsync(file, dryRun: true, conflictResolution, realmId, clientSecret, server, profile)
                    .ConfigureAwait(false));
        }
    }

    private sealed class ImportApplyCommand : Command
    {
        public ImportApplyCommand() : base("apply",
            "Apply a manifest import, creating or updating entities as directed. Use preview first to inspect conflicts.")
        {
            ConfigureShared(this, async (file, conflictResolution, dryRun, realmId, clientSecret, server, profile) =>
                await HandleAsync(file, dryRun, conflictResolution, realmId, clientSecret, server, profile)
                    .ConfigureAwait(false));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Shared option wiring
    // ──────────────────────────────────────────────────────────────────────────

    private delegate Task ImportHandler(
        FileInfo file,
        string conflictResolution,
        bool dryRun,
        Guid? realmId,
        string? clientSecret,
        string? server,
        string? profile);

    private static void ConfigureShared(Command command, ImportHandler handler)
    {
        var fileArgument = new Argument<FileInfo>("file")
        {
            Description = "Path to the manifest JSON file to import"
        };
        var conflictOption = new Option<string>("--conflict-resolution")
        {
            Description = "How to handle conflicts: skip, overwrite, or rename. Default: skip",
            DefaultValueFactory = _ => "skip"
        };
        var serverOption = new Option<string?>("--server",
            "Server URL to import into (defaults to the current profile server)");
        var profileOption = new Option<string?>("--profile", "Authenticated profile to use");
        var realmIdOption = new Option<Guid?>("--realm-id",
            "Target realm ID (required when importing a client manifest into a specific realm)");
        var clientSecretOption = new Option<string?>("--client-secret",
            "Plaintext secret to supply for an obfuscated client or provider credential");
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate and report, but do NOT persist any changes (same as preview)"
        };

        command.Arguments.Add(fileArgument);
        command.Options.Add(conflictOption);
        command.Options.Add(serverOption);
        command.Options.Add(profileOption);
        command.Options.Add(realmIdOption);
        command.Options.Add(clientSecretOption);

        // dry-run only shown on apply
        if (command.Name == "apply")
        {
            command.Options.Add(dryRunOption);
        }

        command.SetAction(async parseResult =>
        {
            var file = parseResult.GetValue(fileArgument)
                       ?? throw new InvalidOperationException("A manifest file path is required.");
            await handler(
                file,
                parseResult.GetValue(conflictOption) ?? "skip",
                parseResult.GetValue(dryRunOption),
                parseResult.GetValue(realmIdOption),
                parseResult.GetValue(clientSecretOption),
                parseResult.GetValue(serverOption),
                parseResult.GetValue(profileOption)).ConfigureAwait(false);
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Core logic
    // ──────────────────────────────────────────────────────────────────────────

    private static async Task HandleAsync(
        FileInfo file,
        bool dryRun,
        string conflictResolution,
        Guid? realmId,
        string? clientSecret,
        string? server,
        string? profileName)
    {
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Manifest file not found: {file.FullName}");
        }

        var manifestJson = await File.ReadAllTextAsync(file.FullName).ConfigureAwait(false);
        var exportType = ReadExportType(manifestJson);

        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profileName);

        switch (exportType?.ToLowerInvariant())
        {
            case "tenant":
                await ImportTenantManifestAsync(config, connection, manifestJson, dryRun, conflictResolution)
                    .ConfigureAwait(false);
                break;

            case "realm":
                await ImportRealmManifestAsync(config, connection, manifestJson, dryRun, conflictResolution)
                    .ConfigureAwait(false);
                break;

            case "client":
                await ImportClientManifestAsync(config, connection, manifestJson, dryRun, conflictResolution,
                    realmId, clientSecret).ConfigureAwait(false);
                break;

            case "provider":
                await ImportProviderManifestAsync(config, connection, manifestJson, dryRun, conflictResolution,
                    clientSecret).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unrecognised or missing exportType '{exportType}' in manifest. " +
                    "Supported types: tenant, realm, client, provider.");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Per-type import helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static async Task ImportTenantManifestAsync(
        CliConfig config,
        AuthenticatedConnection connection,
        string manifestJson,
        bool dryRun,
        string conflictResolution)
    {
        if (!connection.Profile.IsPlatformAdmin)
        {
            throw new InvalidOperationException("Tenant import requires a platform-admin profile.");
        }

        var platformServer = CliServerConnection.GetPlatformServerUrl(connection.ServerUrl);
        var platformConnection = new AuthenticatedConnection(connection.ProfileName, platformServer, connection.Profile);

        var endpoint = dryRun
            ? "admin/api/platform/tenants/import/preview"
            : "admin/api/platform/tenants/import/";

        var body = dryRun
            ? (object)new { manifest = manifestJson, defaultConflictResolution = NormalizeConflict(conflictResolution) }
            : (object)new { manifest = manifestJson, dryRun = false, defaultConflictResolution = NormalizeConflict(conflictResolution) };

        await PostAndDisplayAsync(config, platformConnection, endpoint, body, dryRun, "tenant").ConfigureAwait(false);
    }

    private static async Task ImportRealmManifestAsync(
        CliConfig config,
        AuthenticatedConnection connection,
        string manifestJson,
        bool dryRun,
        string conflictResolution)
    {
        var realmDefinitionJson = ExtractFirstEntity(manifestJson, "realms");
        var endpoint = dryRun
            ? "admin/api/realms/import/preview"
            : "admin/api/realms/import/";

        var body = new
        {
            realmJson = realmDefinitionJson,
            dryRun,
            conflictResolution = NormalizeConflict(conflictResolution)
        };

        await PostAndDisplayAsync(config, connection, endpoint, body, dryRun, "realm").ConfigureAwait(false);
    }

    private static async Task ImportClientManifestAsync(
        CliConfig config,
        AuthenticatedConnection connection,
        string manifestJson,
        bool dryRun,
        string conflictResolution,
        Guid? realmId,
        string? clientSecret)
    {
        if (!dryRun && realmId == null)
        {
            // Attempt to find the target realm automatically from manifest data
            realmId = TryReadRealmId(manifestJson);

            if (realmId == null)
            {
                throw new InvalidOperationException(
                    "Client import requires --realm-id <guid> to specify the target realm. " +
                    "Use 'mrwho-cli client list' to look up realm IDs.");
            }
        }

        var clientDefinitionJson = ExtractFirstEntity(manifestJson, "clients");
        var endpoint = dryRun
            ? "admin/api/clients/import/preview"
            : "admin/api/clients/import/";

        var body = new
        {
            clientJson = clientDefinitionJson,
            targetRealmId = realmId,
            dryRun,
            conflictResolution = NormalizeConflict(conflictResolution),
            clientSecret
        };

        await PostAndDisplayAsync(config, connection, endpoint, body, dryRun, "client").ConfigureAwait(false);
    }

    private static async Task ImportProviderManifestAsync(
        CliConfig config,
        AuthenticatedConnection connection,
        string manifestJson,
        bool dryRun,
        string conflictResolution,
        string? clientSecret)
    {
        var providerDefinitionJson = ExtractFirstEntity(manifestJson, "identityProviders");
        var endpoint = dryRun
            ? "admin/api/providers/import/preview"
            : "admin/api/providers/import/";

        var body = new
        {
            providerJson = providerDefinitionJson,
            dryRun,
            conflictResolution = NormalizeConflict(conflictResolution),
            clientSecret
        };

        await PostAndDisplayAsync(config, connection, endpoint, body, dryRun, "provider").ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // HTTP + display helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static async Task PostAndDisplayAsync(
        CliConfig config,
        AuthenticatedConnection connection,
        string relativeEndpoint,
        object body,
        bool dryRun,
        string entityType)
    {
        var accessToken = await CliServerConnection.GetValidAccessTokenAsync(config, connection).ConfigureAwait(false);
        using var httpClient = CliServerConnection.CreateAuthenticatedHttpClient(connection, accessToken);

        var url = CliServerConnection.CombineRelativePath(connection.ServerUrl, relativeEndpoint);
        using var response = await httpClient.PostAsJsonAsync(url, body).ConfigureAwait(false);

        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            AnsiConsole.MarkupLine($"[red]Import failed[/] (HTTP {(int)response.StatusCode}):");
            PrintResponseSummary(responseText);
            throw new InvalidOperationException($"Server returned HTTP {(int)response.StatusCode}.");
        }

        if (dryRun)
        {
            AnsiConsole.MarkupLine($"[yellow]Preview[/] ({entityType} manifest):");
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]Import applied[/] ({entityType} manifest):");
        }

        PrintResponseSummary(responseText);
    }

    private static void PrintResponseSummary(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Conflicts
            if (root.TryGetProperty("conflicts", out var conflicts) && conflicts.GetArrayLength() > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Conflicts ({conflicts.GetArrayLength()}):[/]");
                foreach (var c in conflicts.EnumerateArray())
                {
                    var msg = c.TryGetProperty("message", out var m) ? m.GetString() : null;
                    var id = c.TryGetProperty("identifier", out var ident) ? ident.GetString() : null;
                    var res = c.TryGetProperty("suggestedResolution", out var sr) ? sr.GetString() : null;
                    AnsiConsole.MarkupLine($"  [yellow]·[/] {Markup.Escape(id ?? "(unknown)")} — {Markup.Escape(msg ?? "")} [dim](suggested: {res ?? "skip"})[/]");
                }
            }

            // Validation errors
            if (root.TryGetProperty("validationErrors", out var valErrors) && valErrors.GetArrayLength() > 0)
            {
                AnsiConsole.MarkupLine($"[red]Validation errors ({valErrors.GetArrayLength()}):[/]");
                foreach (var e in valErrors.EnumerateArray())
                {
                    var msg = e.TryGetProperty("message", out var m) ? m.GetString() : null;
                    var path = e.TryGetProperty("path", out var p) ? p.GetString() : null;
                    AnsiConsole.MarkupLine($"  [red]·[/] {Markup.Escape(path ?? "")} {Markup.Escape(msg ?? "")}");
                }
            }

            // Counts
            static void TryPrintCount(JsonElement root, string field, string label, string colour= "green")
            {
                if (root.TryGetProperty(field, out var val) && val.TryGetInt32(out var n) && n > 0)
                {
                    AnsiConsole.MarkupLine($"  [{colour}]{label}:[/] {n}");
                }
            }

            TryPrintCount(root, "entitiesCreated", "Created");
            TryPrintCount(root, "entitiesUpdated", "Updated");
            TryPrintCount(root, "entitiesSkipped", "Skipped", "yellow");
            TryPrintCount(root, "realmCount", "Realms in manifest");
            TryPrintCount(root, "clientCount", "Clients in manifest");
            TryPrintCount(root, "providerCount", "Providers in manifest");
            TryPrintCount(root, "roleCount", "Roles in manifest");
            TryPrintCount(root, "scopeCount", "Scopes in manifest");

            // Sensitive secret warning
            if (root.TryGetProperty("hasObfuscatedSecrets", out var obf) && obf.GetBoolean())
            {
                if (root.TryGetProperty("obfuscatedSecretCount", out var cnt))
                {
                    AnsiConsole.MarkupLine($"  [yellow]⚠[/]  {cnt.GetInt32()} obfuscated secret(s) detected. " +
                        "Supply values via --client-secret when applying.");
                }
            }

            // Warnings
            if (root.TryGetProperty("warnings", out var warnings) && warnings.GetArrayLength() > 0)
            {
                foreach (var w in warnings.EnumerateArray())
                {
                    AnsiConsole.MarkupLine($"  [yellow]⚠[/]  {Markup.Escape(w.GetString() ?? "")}");
                }
            }

            // Summary sub-object
            if (root.TryGetProperty("summary", out var summary))
            {
                foreach (var prop in summary.EnumerateObject())
                {
                    AnsiConsole.MarkupLine($"  {Markup.Escape(prop.Name)}: {Markup.Escape(prop.Value.ToString())}");
                }
            }

            // Success bool
            if (root.TryGetProperty("success", out var success))
            {
                var ok = success.GetBoolean();
                AnsiConsole.MarkupLine(ok ? "[green]✓ Success[/]" : "[red]✗ Failed[/]");
            }

            // isValid for preview
            if (root.TryGetProperty("isValid", out var isValid))
            {
                AnsiConsole.MarkupLine(isValid.GetBoolean() ? "[green]✓ Manifest is valid[/]" : "[red]✗ Manifest is invalid[/]");
            }
        }
        catch
        {
            // Non-JSON response – print raw
            AnsiConsole.WriteLine(json);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // JSON parse helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static string? ReadExportType(string manifestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (doc.RootElement.TryGetProperty("exportType", out var et))
                return et.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Extracts the first element from the <paramref name="arrayKey"/> in the manifest's <c>data</c> object.
    /// </summary>
    private static string ExtractFirstEntity(string manifestJson, string arrayKey)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty(arrayKey, out var arr) &&
                arr.ValueKind == JsonValueKind.Array &&
                arr.GetArrayLength() > 0)
            {
                return arr[0].GetRawText();
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse manifest JSON: {ex.Message}", ex);
        }

        throw new InvalidOperationException(
            $"Manifest does not contain a '{arrayKey}' array under 'data', or the array is empty.");
    }

    /// <summary>
    /// Tries to read a realm ID from the manifest's first client record's associated realm context.
    /// Only used as a last-resort fallback if <c>--realm-id</c> was not supplied.
    /// </summary>
    private static Guid? TryReadRealmId(string manifestJson)
    {
        try
        {
            var node = JsonNode.Parse(manifestJson);
            var first = node?["data"]?["clients"]?[0];
            if (first?["realmId"] is JsonNode rid && Guid.TryParse(rid.GetValue<string>(), out var g))
            {
                return g;
            }
        }
        catch { }
        return null;
    }

    private static string NormalizeConflict(string strategy) =>
        strategy.ToLowerInvariant() switch
        {
            "overwrite" => "Overwrite",
            "rename" => "Rename",
            "merge" => "Merge",
            _ => "Skip"
        };
}
