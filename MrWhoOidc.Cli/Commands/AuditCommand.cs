using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Configuration audit log inspection.
/// </summary>
public sealed class AuditCommand : Command
{
    public AuditCommand() : base("audit", "Inspect configuration audit logs")
    {
        Subcommands.Add(new AuditListCommand());
        Subcommands.Add(new AuditGetCommand());
    }

    private sealed class AuditListCommand : Command
    {
        public AuditListCommand() : base("list", "List recent configuration audit events")
        {
            var skipOption = new Option<int?>("--skip") { Description = "Number of entries to skip" };
            var takeOption = new Option<int?>("--take") { Description = "Number of entries to retrieve (default: 50)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Options.Add(skipOption);
            Options.Add(takeOption);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var skip = parseResult.GetValue(skipOption);
                var take = parseResult.GetValue(takeOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var format = parseResult.GetValue(formatOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var queryParams = new List<string>();
                if (skip.HasValue) queryParams.Add($"skip={skip.Value}");
                if (take.HasValue) queryParams.Add($"take={take.Value}");
                var query = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";

                var resp = await CliAdminApiClient.GetAsync<AuditListResponse>(
                    config, connection, $"admin/api/configuration-audit{query}").ConfigureAwait(false);
                var entries = resp?.Items ?? [];

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(resp, SharedJsonOptions.IndentedOptions));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("ID")
                    .AddColumn("Operation")
                    .AddColumn("Entity")
                    .AddColumn("User")
                    .AddColumn("Timestamp");

                foreach (var e in entries)
                {
                    table.AddRow(
                        Markup.Escape(e.Id.ToString()),
                        Markup.Escape(e.Operation ?? "-"),
                        Markup.Escape(e.EntityIdentifier ?? e.EntityType ?? "-"),
                        Markup.Escape(e.PerformedBy ?? "-"),
                        Markup.Escape(e.Timestamp.ToString("u")));
                }

                AnsiConsole.Write(table);
            });
        }
    }

    private sealed class AuditGetCommand : Command
    {
        public AuditGetCommand() : base("get", "Get a specific audit log entry")
        {
            var idArg = new Argument<Guid>("id") { Description = "Audit entry ID (GUID)" };
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
                var entry = await CliAdminApiClient.GetAsync<AuditEntry>(
                    config, connection, $"admin/api/configuration-audit/{id}").ConfigureAwait(false);

                if (entry is null)
                {
                    AnsiConsole.MarkupLine("[red]Audit entry not found.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[bold]ID:[/]        {Markup.Escape(entry.Id.ToString())}");
                AnsiConsole.MarkupLine($"[bold]Operation:[/] {Markup.Escape(entry.Operation ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Entity:[/]    {Markup.Escape(entry.EntityIdentifier ?? entry.EntityType ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]User:[/]      {Markup.Escape(entry.PerformedBy ?? "-")}");
                AnsiConsole.MarkupLine($"[bold]Timestamp:[/] {Markup.Escape(entry.Timestamp.ToString("u"))}");
                AnsiConsole.MarkupLine($"[bold]Result:[/]    {Markup.Escape(entry.Result ?? "-")}");
            });
        }
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public sealed class AuditListResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<AuditEntry> Items { get; set; } = [];

    [JsonPropertyName("pagination")]
    public JsonElement? Pagination { get; set; }
}

public sealed class AuditEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("entityType")]
    public string? EntityType { get; set; }

    [JsonPropertyName("entityIdentifier")]
    public string? EntityIdentifier { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("performedBy")]
    public string? PerformedBy { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }
}
