using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

public sealed class TenantClaimCommand : Command
{
    public TenantClaimCommand() : base("claim", "Manage tenant domain claims (tenant-admin)")
    {
        Subcommands.Add(new TenantClaimVerifyCommand());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // tenant claim verify <id> --yes
    // tenant claim verify --domain <domain> --yes
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class TenantClaimVerifyCommand : Command
    {
        public TenantClaimVerifyCommand() : base("verify", "Mark a domain claim as verified (bypasses DNS verification)")
        {
            var idArg = new Argument<Guid?>("id") { Description = "Domain claim ID (GUID)" };
            var domainOption = new Option<string?>("--domain") { Description = "Domain name to look up and verify" };
            var yesOption = new Option<bool>("--yes") { Description = "Skip the confirmation prompt" };
            var serverOption = new Option<string?>("--server") { Description = "Server URL" };
            var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };

            Arguments.Add(idArg);
            Options.Add(domainOption);
            Options.Add(yesOption);
            Options.Add(serverOption);
            Options.Add(profileOption);

            this.SetSafeAction(async parseResult =>
            {
                var id = parseResult.GetValue(idArg);
                var domain = parseResult.GetValue(domainOption);
                var yes = parseResult.GetValue(yesOption);
                var server = parseResult.GetValue(serverOption);
                var profile = parseResult.GetValue(profileOption);

                if (id.HasValue && domain is not null)
                    throw new InvalidOperationException("Provide either an ID or --domain, not both.");

                if (!id.HasValue && string.IsNullOrWhiteSpace(domain))
                    throw new InvalidOperationException("Provide a claim ID or use --domain to look it up.");

                var config = await CliConfig.LoadAsync().ConfigureAwait(false);
                var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profile);

                Guid claimId;
                if (id.HasValue)
                {
                    claimId = id.Value;
                }
                else
                {
                    var claims = await CliAdminApiClient.GetListAsync<TenantClaimListItem>(
                        config, connection, "admin/api/domain-claims").ConfigureAwait(false);

                    var match = claims.FirstOrDefault(c => c.Domain.Equals(domain!, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                        throw new InvalidOperationException($"No domain claim found for '{domain}'.");

                    claimId = match.Id;
                }

                if (!yes)
                {
                    if (!AnsiConsole.Confirm($"Verify domain claim {claimId}? This will mark it as verified.", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                        return;
                    }
                }

                var result = await CliAdminApiClient.PostAsync<TenantClaimVerifyResult>(
                    config, connection, $"admin/api/domain-claims/{claimId}/verify",
                    new { }).ConfigureAwait(false);

                if (result is null)
                {
                    AnsiConsole.MarkupLine($"[yellow]Domain claim {claimId} verified (no response body).[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]Domain claim verified:[/] {Markup.Escape(result.Domain)} → {Markup.Escape(result.Status)}");
            });
        }
    }
}

public sealed class TenantClaimListItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

public sealed class TenantClaimVerifyResult
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}
