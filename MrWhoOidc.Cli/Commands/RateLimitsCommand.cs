using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Rate-limiting inspection: overview, events, per-client usage.
/// </summary>
public sealed class RateLimitsCommand : Command
{
    public RateLimitsCommand() : base("rate-limits", "Inspect rate-limiting policies and events")
    {
        Subcommands.Add(new OverviewCommand());
        Subcommands.Add(new EventsCommand());
        Subcommands.Add(new ClientRateLimitCommand());
    }

    private sealed class OverviewCommand : Command
    {
        public OverviewCommand() : base("overview", "Show rate-limiting policies overview")
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

                var overview = await CliAdminApiClient.GetAsync<JsonElement>(
                    config, connection, "admin/api/rate-limits/overview").ConfigureAwait(false);

                AnsiConsole.WriteLine(JsonSerializer.Serialize(overview, new JsonSerializerOptions { WriteIndented = true }));
            });
        }
    }

    private sealed class EventsCommand : Command
    {
        public EventsCommand() : base("events", "Show recent rate-limit events")
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

                var events = await CliAdminApiClient.GetListAsync<RateLimitEvent>(
                    config, connection, "admin/api/rate-limits/events").ConfigureAwait(false);

                if (format == OutputFormat.Json)
                {
                    AnsiConsole.WriteLine(JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("Endpoint")
                    .AddColumn("Client")
                    .AddColumn("Limit")
                    .AddColumn("Remaining")
                    .AddColumn("Timestamp");

                foreach (var e in events)
                {
                    table.AddRow(
                        Markup.Escape(e.Endpoint ?? "-"),
                        Markup.Escape(e.ClientId ?? "-"),
                        e.Limit.ToString(),
                        e.Remaining.ToString(),
                        Markup.Escape(e.Timestamp.ToString("u")));
                }

                AnsiConsole.Write(table);
            });
        }
    }

    private sealed class ClientRateLimitCommand : Command
    {
        public ClientRateLimitCommand() : base("client", "Show rate-limit usage for a specific client")
        {
            var clientIdArg = new Argument<Guid>("client-id") { Description = "Client internal ID (GUID)" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(clientIdArg);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var clientId = parseResult.GetValue(clientIdArg);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                var result = await CliAdminApiClient.GetAsync<JsonElement>(
                    config, connection, $"admin/api/rate-limits/client/{clientId}").ConfigureAwait(false);

                AnsiConsole.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            });
        }
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public sealed class RateLimitEvent
{
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("remaining")]
    public int Remaining { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }
}
