using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

public sealed class RegistrationCommand : Command
{
    public RegistrationCommand() : base("registration", "Manage tenant user registration settings")
    {
        Subcommands.Add(new RegistrationGetCommand());
        Subcommands.Add(new RegistrationSetCommand());
    }

    private sealed class RegistrationGetCommand : Command
    {
        public RegistrationGetCommand() : base("get", "Show tenant user registration settings")
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
                var settings = await CliAdminApiClient.GetAsync<TenantRegistrationSettingsCliModel>(
                    config, connection, "admin/api/registration-settings").ConfigureAwait(false);

                if (settings is null)
                {
                    AnsiConsole.MarkupLine("[red]Registration settings could not be loaded.[/]");
                    return;
                }

                if (format == OutputFormat.Json)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(settings, SharedJsonOptions.IndentedOptions));
                    return;
                }

                WriteSettings(settings);
            });
        }
    }

    private sealed class RegistrationSetCommand : Command
    {
        public RegistrationSetCommand() : base("set", "Update tenant user registration settings")
        {
            var modeOption = new Option<string?>("--mode")
            {
                Description = "Registration mode: platform-only, tenant-only, or both (required)"
            };
            var headlineOption = new Option<string?>("--headline") { Description = "Tenant registration page heading" };
            var introOption = new Option<string?>("--intro") { Description = "Tenant registration page intro text" };
            var heroImageUrlOption = new Option<string?>("--hero-image-url") { Description = "Tenant registration page image URL" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Options.Add(modeOption);
            Options.Add(headlineOption);
            Options.Add(introOption);
            Options.Add(heroImageUrlOption);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var mode = NormalizeMode(parseResult.GetValue(modeOption));
                var headline = parseResult.GetValue(headlineOption);
                var intro = parseResult.GetValue(introOption);
                var heroImageUrl = parseResult.GetValue(heroImageUrlOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var format = parseResult.GetValue(formatOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                await CliAdminApiClient.PutAsync(
                    config,
                    connection,
                    "admin/api/registration-settings",
                    new { mode, headline, introText = intro, heroImageUrl }).ConfigureAwait(false);

                var settings = await CliAdminApiClient.GetAsync<TenantRegistrationSettingsCliModel>(
                    config, connection, "admin/api/registration-settings").ConfigureAwait(false);

                if (settings is null)
                {
                    AnsiConsole.MarkupLine("[green]Registration settings updated.[/]");
                    return;
                }

                if (format == OutputFormat.Json)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(settings, SharedJsonOptions.IndentedOptions));
                    return;
                }

                AnsiConsole.MarkupLine("[green]Registration settings updated.[/]");
                WriteSettings(settings);
            });
        }

        private static string NormalizeMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("--mode is required. Use platform-only, tenant-only, or both.");
            }

            var normalized = value.Trim().ToLowerInvariant();
            return normalized switch
            {
                "platform-only" or "platformonly" => "platform-only",
                "tenant-only" or "tenantonly" => "tenant-only",
                "both" or "platform-and-tenant" or "platformandtenant" => "both",
                _ => throw new InvalidOperationException("--mode must be platform-only, tenant-only, or both.")
            };
        }
    }

    private static void WriteSettings(TenantRegistrationSettingsCliModel settings)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Setting")
            .AddColumn("Value");

        table.AddRow("Tenant", Markup.Escape(settings.TenantSlug ?? "-"));
        table.AddRow("Mode", Markup.Escape(settings.Mode ?? "platform-only"));
        table.AddRow("Tenant URL", Markup.Escape(settings.TenantRegistrationUrl ?? "-"));
        table.AddRow("Headline", Markup.Escape(settings.Headline ?? "-"));
        table.AddRow("Intro", Markup.Escape(settings.IntroText ?? "-"));
        table.AddRow("Image URL", Markup.Escape(settings.HeroImageUrl ?? "-"));

        AnsiConsole.Write(table);
    }
}

public sealed class TenantRegistrationSettingsCliModel
{
    [JsonPropertyName("tenantSlug")]
    public string? TenantSlug { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("tenantRegistrationUrl")]
    public string? TenantRegistrationUrl { get; set; }

    [JsonPropertyName("headline")]
    public string? Headline { get; set; }

    [JsonPropertyName("introText")]
    public string? IntroText { get; set; }

    [JsonPropertyName("heroImageUrl")]
    public string? HeroImageUrl { get; set; }

    [JsonPropertyName("overrides")]
    public TenantRegistrationSettingsOverridesCliModel? Overrides { get; set; }
}

public sealed class TenantRegistrationSettingsOverridesCliModel
{
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("headline")]
    public string? Headline { get; set; }

    [JsonPropertyName("introText")]
    public string? IntroText { get; set; }

    [JsonPropertyName("heroImageUrl")]
    public string? HeroImageUrl { get; set; }
}