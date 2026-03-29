using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// License inspection: view current license, history, usage, and limits.
/// </summary>
public sealed class LicenseCommand : Command
{
    public LicenseCommand() : base("license", "Inspect license information")
    {
        Subcommands.Add(new LicenseShowCommand());
        Subcommands.Add(new LicenseHistoryCommand());
        Subcommands.Add(new LicenseUsageCommand());
        Subcommands.Add(new LicenseLimitsCommand());
    }

    private sealed class LicenseShowCommand : Command
    {
        public LicenseShowCommand() : base("show", "Show current license details")
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
                var license = await CliAdminApiClient.GetAsync<LicenseInfo>(
                    config, connection, "admin/api/license").ConfigureAwait(false);

                if (license is null)
                {
                    AnsiConsole.MarkupLine("[grey]No license information available.[/]");
                    return;
                }

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(license, SharedJsonOptions.IndentedOptions));
                    return;
                }

                AnsiConsole.MarkupLine($"[bold]Tier:[/]       {Markup.Escape(license.Tier ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Scope:[/]      {Markup.Escape(license.Scope ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Valid From:[/]  {Markup.Escape(license.ValidFrom?.ToString("u") ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Valid Until:[/] {Markup.Escape(license.ValidUntil?.ToString("u") ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Status:[/]      {Markup.Escape(license.Status ?? "-")}");
            });
        }
    }

    private sealed class LicenseHistoryCommand : Command
    {
        public LicenseHistoryCommand() : base("history", "Show license change history")
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
                var resp = await CliAdminApiClient.GetAsync<LicenseHistoryResponse>(
                    config, connection, "admin/api/license/history?page=1&pageSize=50").ConfigureAwait(false);
                var history = resp?.Entries ?? [];

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(resp, SharedJsonOptions.IndentedOptions));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("Action")
                    .AddColumn("Old Tier")
                    .AddColumn("New Tier")
                    .AddColumn("Timestamp");

                foreach (var h in history)
                {
                    table.AddRow(
                        Markup.Escape(h.Action ?? "-"),
                        Markup.Escape(h.OldTier ?? "-"),
                        Markup.Escape(h.NewTier ?? "-"),
                        Markup.Escape(h.CreatedAt.ToString("u")));
                }

                AnsiConsole.Write(table);
            });
        }
    }

    private sealed class LicenseUsageCommand : Command
    {
        public LicenseUsageCommand() : base("usage", "Show feature usage report")
        {
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var usage = await CliAdminApiClient.GetAsync<JsonElement>(
                    config, connection, "admin/api/license/usage").ConfigureAwait(false);

                AnsiConsole.WriteLine(JsonSerializer.Serialize(usage, SharedJsonOptions.IndentedOptions));
            });
        }
    }

    private sealed class LicenseLimitsCommand : Command
    {
        public LicenseLimitsCommand() : base("limits", "Show usage limits and current utilization")
        {
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);
                var limits = await CliAdminApiClient.GetAsync<JsonElement>(
                    config, connection, "admin/api/license/limits").ConfigureAwait(false);

                AnsiConsole.WriteLine(JsonSerializer.Serialize(limits, SharedJsonOptions.IndentedOptions));
            });
        }
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public sealed class LicenseInfo
{
    [JsonPropertyName("tier")]
    public string? Tier { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("validFrom")]
    public DateTimeOffset? ValidFrom { get; set; }

    [JsonPropertyName("validUntil")]
    public DateTimeOffset? ValidUntil { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed class LicenseHistoryResponse
{
    [JsonPropertyName("entries")]
    public IReadOnlyList<LicenseHistoryEntryItem>? Entries { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

public sealed class LicenseHistoryEntryItem
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("oldTier")]
    public string? OldTier { get; set; }

    [JsonPropertyName("newTier")]
    public string? NewTier { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}
