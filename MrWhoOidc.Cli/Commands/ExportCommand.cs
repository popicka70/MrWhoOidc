using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

public sealed class ExportCommand : Command
{
    public ExportCommand() : base("export", "Export IdP configuration manifests to files")
    {
        Subcommands.Add(new ExportTenantCommand());
        Subcommands.Add(new ExportRealmCommand());
        Subcommands.Add(new ExportClientCommand());
        Subcommands.Add(new ExportProviderCommand());
    }

    private sealed class ExportTenantCommand : Command
    {
        public ExportTenantCommand() : base("tenant", "Export a tenant manifest to a file")
        {
            var slugArgument = new Argument<string>("slug") { Description = "Tenant slug to export" };
            var serverOption = new Option<string?>("--server") { Description = "Platform server URL (defaults to the saved profile server)" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var modeOption = CreateModeOption();
            var outputOption = CreateOutputOption();
            var overwriteOption = CreateOverwriteOption();

            Arguments.Add(slugArgument);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(modeOption);
            Options.Add(outputOption);
            Options.Add(overwriteOption);

            this.SetSafeAction(async parseResult =>
            {
                await HandleTenantAsync(
                    parseResult.GetValue(slugArgument) ?? throw new InvalidOperationException("Tenant slug is required."),
                    parseResult.GetValue(serverOption),
                    parseResult.GetValue(profileOption),
                    parseResult.GetValue(modeOption) ?? "obfuscated",
                    parseResult.GetValue(outputOption),
                    parseResult.GetValue(overwriteOption)).ConfigureAwait(false);
            });
        }
    }

    private sealed class ExportRealmCommand : Command
    {
        public ExportRealmCommand() : base("realm", "Export a realm manifest to a file")
        {
            ConfigureEntityExport(this, HandleRealmAsync, "Realm database ID to export");
        }
    }

    private sealed class ExportClientCommand : Command
    {
        public ExportClientCommand() : base("client", "Export a client manifest to a file")
        {
            ConfigureEntityExport(this, HandleClientAsync, "Client database ID to export");
        }
    }

    private sealed class ExportProviderCommand : Command
    {
        public ExportProviderCommand() : base("provider", "Export an identity provider manifest to a file")
        {
            ConfigureEntityExport(this, HandleProviderAsync, "Provider database ID to export");
        }
    }

    private delegate Task EntityExportHandler(Guid id, string? server, string? profile, string mode, string? outputPath, bool overwrite);

    private static void ConfigureEntityExport(Command command, EntityExportHandler handler, string idDescription)
    {
        var idArgument = new Argument<Guid>("id") { Description = idDescription };
        var serverOption = new Option<string?>("--server") { Description = "Tenant-aware server URL (defaults to the saved profile server)" };
        var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
        var modeOption = CreateModeOption();
        var outputOption = CreateOutputOption();
        var overwriteOption = CreateOverwriteOption();

        command.Arguments.Add(idArgument);
        command.Options.Add(serverOption);
        command.Options.Add(profileOption);
        command.Options.Add(modeOption);
        command.Options.Add(outputOption);
        command.Options.Add(overwriteOption);

        command.SetSafeAction(async parseResult =>
        {
            await handler(
                parseResult.GetValue(idArgument),
                parseResult.GetValue(serverOption),
                parseResult.GetValue(profileOption),
                parseResult.GetValue(modeOption) ?? "obfuscated",
                parseResult.GetValue(outputOption),
                parseResult.GetValue(overwriteOption)).ConfigureAwait(false);
        });
    }

    private static Option<string> CreateModeOption()
    {
        return new Option<string>("--mode")
        {
            Description = "Export mode: obfuscated or full",
            DefaultValueFactory = _ => "obfuscated"
        };
    }

    private static Option<string?> CreateOutputOption()
    {
        return new Option<string?>("--output") { Description = "Output file path or directory (defaults to ~/.mrwhooidc/exports)" };
    }

    private static Option<bool> CreateOverwriteOption()
    {
        return new Option<bool>("--overwrite") { Description = "Overwrite the output file if it already exists" };
    }

    private static async Task HandleTenantAsync(string slug, string? server, string? profileName, string mode, string? outputPath, bool overwrite)
    {
        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profileName);
        if (!connection.Profile.IsPlatformAdmin)
        {
            throw new InvalidOperationException("Tenant export requires a platform-admin profile.");
        }

        var platformServer = CliServerConnection.GetPlatformServerUrl(connection.ServerUrl);
        var platformConnection = new AuthenticatedConnection(connection.ProfileName, platformServer, connection.Profile);
        var relativePath = $"admin/api/platform/tenants/{Uri.EscapeDataString(slug)}/export?mode={Uri.EscapeDataString(NormalizeMode(mode))}";
        await DownloadManifestAsync(config, platformConnection, relativePath, outputPath, overwrite).ConfigureAwait(false);
    }

    private static async Task HandleRealmAsync(Guid id, string? server, string? profileName, string mode, string? outputPath, bool overwrite)
    {
        await HandleEntityExportAsync(id, server, profileName, mode, outputPath, overwrite, "admin/api/realms").ConfigureAwait(false);
    }

    private static async Task HandleClientAsync(Guid id, string? server, string? profileName, string mode, string? outputPath, bool overwrite)
    {
        await HandleEntityExportAsync(id, server, profileName, mode, outputPath, overwrite, "admin/api/clients").ConfigureAwait(false);
    }

    private static async Task HandleProviderAsync(Guid id, string? server, string? profileName, string mode, string? outputPath, bool overwrite)
    {
        await HandleEntityExportAsync(id, server, profileName, mode, outputPath, overwrite, "admin/api/providers").ConfigureAwait(false);
    }

    private static async Task HandleEntityExportAsync(Guid id, string? server, string? profileName, string mode, string? outputPath, bool overwrite, string resourceBasePath)
    {
        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profileName);
        var relativePath = $"{resourceBasePath}/{id:D}/export?mode={Uri.EscapeDataString(NormalizeMode(mode))}";
        await DownloadManifestAsync(config, connection, relativePath, outputPath, overwrite).ConfigureAwait(false);
    }

    private static async Task DownloadManifestAsync(CliConfig config, AuthenticatedConnection connection, string relativePath, string? outputPath, bool overwrite)
    {
        var accessToken = await CliServerConnection.GetValidAccessTokenAsync(config, connection).ConfigureAwait(false);
        using var httpClient = CliServerConnection.CreateAuthenticatedHttpClient(connection, accessToken);
        using var response = await httpClient.GetAsync(CliServerConnection.CombineRelativePath(connection.ServerUrl, relativePath)).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Export failed with HTTP {(int)response.StatusCode}. {payload}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var content = Encoding.UTF8.GetString(bytes);
        var suggestedFileName = GetSuggestedFileName(response.Content.Headers.ContentDisposition);
        var writtenPath = await CliFileOutput.WriteTextAsync(content, suggestedFileName, outputPath, overwrite).ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[green]Export completed.[/] Wrote manifest to [bold]{Markup.Escape(writtenPath)}[/]");
    }

    private static string GetSuggestedFileName(ContentDispositionHeaderValue? contentDisposition)
    {
        var fileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return $"export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        }

        return fileName.Trim('"');
    }

    private static string NormalizeMode(string mode)
    {
        return string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase) ? "full" : "obfuscated";
    }
}
