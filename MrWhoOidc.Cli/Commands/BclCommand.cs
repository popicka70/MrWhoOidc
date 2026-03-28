using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Back-channel logout outbox management: view queue, retry failed, check alerts.
/// </summary>
public sealed class BclCommand : Command
{
    public BclCommand() : base("bcl", "Manage back-channel logout notifications")
    {
        Subcommands.Add(new BclOutboxCommand());
        Subcommands.Add(new BclRetryCommand());
        Subcommands.Add(new BclAlertsCommand());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // bcl outbox
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class BclOutboxCommand : Command
    {
        public BclOutboxCommand() : base("outbox", "List back-channel logout notification queue")
        {
            var statusOption = new Option<string?>("--status") { Description = "Filter by status: pending, failed, succeeded, dead_letter" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
            var formatOption = new Option<OutputFormat>("--format")
            {
                Description = "Output format: table or json",
                DefaultValueFactory = _ => OutputFormat.Table
            };

            Options.Add(statusOption);
            Options.Add(serverOption);
            Options.Add(profileOption);
            Options.Add(formatOption);

            this.SetSafeAction(async parseResult =>
            {
                var status = parseResult.GetValue(statusOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);
                var format = parseResult.GetValue(formatOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var query = !string.IsNullOrWhiteSpace(status)
                    ? $"?status={Uri.EscapeDataString(status)}"
                    : "";

                var resp = await CliAdminApiClient.GetAsync<BclOutboxResponse>(
                    config, connection, $"admin/api/bcl/outbox{query}").ConfigureAwait(false);
                var entries = resp?.Items ?? [];

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(resp, SharedJsonOptions.IndentedOptions));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("ID")
                    .AddColumn("Client")
                    .AddColumn("Status")
                    .AddColumn("Retries")
                    .AddColumn("Created At");

                foreach (var e in entries)
                {
                    table.AddRow(
                        Markup.Escape(e.Id.ToString()),
                        Markup.Escape(e.ClientId ?? "-"),
                        Markup.Escape(e.Status ?? "-"),
                        e.AttemptCount.ToString(),
                        Markup.Escape(e.CreatedAt.ToString("u")));
                }

                AnsiConsole.Write(table);
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // bcl retry <id>
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class BclRetryCommand : Command
    {
        public BclRetryCommand() : base("retry", "Retry a failed back-channel logout notification")
        {
            var idArg = new Argument<Guid>("id") { Description = "Notification ID (GUID)" };
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

                await CliAdminApiClient.PostAsync<object>(
                    config, connection, $"admin/api/bcl/outbox/{id}/retry",
                    new { }).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Retry queued for notification {id}.[/]");
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // bcl alerts
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class BclAlertsCommand : Command
    {
        public BclAlertsCommand() : base("alerts", "Show back-channel logout alert snapshot")
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

                var snapshot = await CliAdminApiClient.GetAsync<BclAlertSnapshot>(
                    config, connection, "admin/api/bcl/alerts/snapshot").ConfigureAwait(false);

                if (snapshot is null)
                {
                    AnsiConsole.MarkupLine("[grey]No alert data available.[/]");
                    return;
                }

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(snapshot, SharedJsonOptions.IndentedOptions));
                    return;
                }

                AnsiConsole.MarkupLine($"[bold]Pending:[/]     {snapshot.PendingCount}");
                AnsiConsole.MarkupLine($"[bold]Failed:[/]      {snapshot.FailedCount}");
                AnsiConsole.MarkupLine($"[bold]Dead Letter:[/] {snapshot.DeadLetterCount}");
                AnsiConsole.MarkupLine($"[bold]Succeeded:[/]   {snapshot.SucceededCount}");
            });
        }
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public sealed class BclOutboxResponse
{
    [JsonPropertyName("backlog")]
    public int Backlog { get; set; }

    [JsonPropertyName("items")]
    public IReadOnlyList<BclOutboxEntry>? Items { get; set; }
}

public sealed class BclOutboxEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("attemptCount")]
    public int AttemptCount { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BclAlertSnapshot
{
    [JsonPropertyName("pendingCount")]
    public int PendingCount { get; set; }

    [JsonPropertyName("failedCount")]
    public int FailedCount { get; set; }

    [JsonPropertyName("deadLetterCount")]
    public int DeadLetterCount { get; set; }

    [JsonPropertyName("succeededCount")]
    public int SucceededCount { get; set; }
}
