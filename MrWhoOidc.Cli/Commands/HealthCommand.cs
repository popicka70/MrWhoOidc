using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Consolidated health check: calls all /health/* endpoints and presents a status dashboard.
/// Also provides whoami and status convenience commands.
/// </summary>
public sealed class HealthCommand : Command
{
    public HealthCommand() : base("health", "Show server health status across all subsystems")
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

            var endpoints = new[]
            {
                ("Backchannel Logout", "health/backchannel"),
                ("Client Secrets", "health/client-secrets"),
                ("Global Auth", "health/global-auth"),
                ("Issuer Config", "health/issuer"),
                ("Forwarded Headers", "health/forwarded-headers"),
            };

            var results = new List<HealthCheckResult>();

            foreach (var (name, path) in endpoints)
            {
                try
                {
                    var result = await CliAdminApiClient.GetAsync<HealthPayload>(config, connection, path).ConfigureAwait(false);
                    results.Add(new HealthCheckResult
                    {
                        Subsystem = name,
                        Status = result?.Status ?? "unknown",
                        Details = result?.Description
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new HealthCheckResult
                    {
                        Subsystem = name,
                        Status = "error",
                        Details = ex.Message
                    });
                }
            }

            if (format == OutputFormat.Json)
            {
                AnsiConsole.WriteLine(JsonSerializer.Serialize(results, SharedJsonOptions.IndentedOptions));
                return;
            }

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Subsystem")
                .AddColumn("Status")
                .AddColumn("Details");

            foreach (var r in results)
            {
                var statusMarkup = r.Status?.ToLowerInvariant() switch
                {
                    "healthy" => "[green]Healthy[/]",
                    "degraded" => "[yellow]Degraded[/]",
                    "unhealthy" => "[red]Unhealthy[/]",
                    "error" => "[red]Error[/]",
                    _ => Markup.Escape(r.Status ?? "unknown")
                };

                table.AddRow(
                    Markup.Escape(r.Subsystem),
                    statusMarkup,
                    Markup.Escape(r.Details ?? "-"));
            }

            AnsiConsole.Write(table);
        });
    }
}

/// <summary>
/// Shows current profile identity: username, tenant, roles, token expiry.
/// </summary>
public sealed class WhoAmICommand : Command
{
    public WhoAmICommand() : base("whoami", "Show current profile identity and session info")
    {
        var profileOption = new Option<string?>("--profile") { Description = "Profile to inspect" };

        Options.Add(profileOption);

        this.SetSafeAction(async parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption);

            var config = await CliConfig.LoadAsync().ConfigureAwait(false);

            string resolvedName;
            ProfileConfig profile;

            if (!string.IsNullOrWhiteSpace(profileName))
            {
                if (!config.Profiles.TryGetValue(profileName, out profile!))
                    throw new InvalidOperationException($"Profile '{profileName}' not found.");
                resolvedName = profileName;
            }
            else
            {
                resolvedName = config.CurrentProfile;
                profile = config.GetCurrentProfile()
                    ?? throw new InvalidOperationException("No current profile. Log in first.");
            }

            AnsiConsole.MarkupLine($"[bold]Profile:[/]        {Markup.Escape(resolvedName)}");
            AnsiConsole.MarkupLine($"[bold]Server:[/]         {Markup.Escape(profile.ServerUrl ?? "-")}");
            AnsiConsole.MarkupLine($"[bold]Tenant:[/]         {Markup.Escape(profile.TenantSlug ?? "-")}");
            AnsiConsole.MarkupLine($"[bold]Platform Admin:[/] {(profile.IsPlatformAdmin ? "yes" : "no")}");
            AnsiConsole.MarkupLine($"[bold]Authenticated:[/]  {(profile.IsAuthenticated ? "yes" : "no")}");

            if (profile.TokenExpiry.HasValue)
            {
                var remaining = profile.TokenExpiry.Value - DateTimeOffset.UtcNow;
                var expiryDisplay = remaining.TotalSeconds > 0
                    ? $"{profile.TokenExpiry.Value:u} ({remaining.TotalMinutes:F0} min remaining)"
                    : $"{profile.TokenExpiry.Value:u} [red](expired)[/]";
                AnsiConsole.MarkupLine($"[bold]Token Expiry:[/]   {expiryDisplay}");
            }
            else
            {
                AnsiConsole.MarkupLine("[bold]Token Expiry:[/]   unknown");
            }

            // Try to decode JWT claims for additional identity info
            if (!string.IsNullOrWhiteSpace(profile.AccessToken))
            {
                try
                {
                    var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                    if (handler.CanReadToken(profile.AccessToken))
                    {
                        var jwt = handler.ReadJwtToken(profile.AccessToken);
                        var sub = jwt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
                        var email = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
                        var name = jwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
                        var roles = jwt.Claims.Where(c => c.Type == "roles" || c.Type == "role")
                            .Select(c => c.Value).ToList();

                        if (!string.IsNullOrWhiteSpace(sub))
                            AnsiConsole.MarkupLine($"[bold]Subject:[/]        {Markup.Escape(sub)}");
                        if (!string.IsNullOrWhiteSpace(email))
                            AnsiConsole.MarkupLine($"[bold]Email:[/]          {Markup.Escape(email)}");
                        if (!string.IsNullOrWhiteSpace(name))
                            AnsiConsole.MarkupLine($"[bold]Name:[/]           {Markup.Escape(name)}");
                        if (roles.Count > 0)
                            AnsiConsole.MarkupLine($"[bold]Roles:[/]          {Markup.Escape(string.Join(", ", roles))}");
                    }
                }
                catch
                {
                    // Token may be opaque or expired; skip.
                }
            }
        });
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

internal sealed class HealthPayload
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

internal sealed class HealthCheckResult
{
    [JsonPropertyName("subsystem")]
    public string Subsystem { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("details")]
    public string? Details { get; set; }
}
