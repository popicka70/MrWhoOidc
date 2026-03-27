using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Read-only diagnostic command that fetches client configuration from multiple
/// endpoints and flags potential issues: expired secrets, missing redirect URIs
/// for code flows, PKCE disabled for public clients, no scopes assigned, etc.
/// </summary>
public sealed class ClientValidateCommand : Command
{
    public ClientValidateCommand() : base("validate", "Validate client configuration and flag potential issues")
    {
        var idArg = new Argument<Guid>("id") { Description = "Client internal ID (GUID)" };
        var serverOption = new Option<string?>("--server") { Description = "Server URL" };
        var profileOption = new Option<string?>("--profile") { Description = "Authenticated profile to use" };
        var formatOption = new Option<OutputFormat>("--format")
        {
            Description = "Output format: table or json",
            DefaultValueFactory = _ => OutputFormat.Table
        };

        Arguments.Add(idArg);
        Options.Add(serverOption);
        Options.Add(profileOption);
        Options.Add(formatOption);

        this.SetSafeAction(async parseResult =>
        {
            var id = parseResult.GetValue(idArg);
            var server = parseResult.GetValue(serverOption);
            var profile = parseResult.GetValue(profileOption);
            var format = parseResult.GetValue(formatOption);
            await HandleAsync(id, server, profile, format).ConfigureAwait(false);
        });
    }

    private static async Task HandleAsync(Guid id, string? server, string? profileName, OutputFormat format)
    {
        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, server, profileName);

        // Fetch client detail
        var client = await CliAdminApiClient.GetAsync<ValidateClientDetail>(
            config, connection, $"admin/api/clients/{id}").ConfigureAwait(false);

        if (client is null)
        {
            AnsiConsole.MarkupLine("[red]Client not found.[/]");
            return;
        }

        // Fetch secrets
        var secrets = await CliAdminApiClient.GetListAsync<ClientSecretItem>(
            config, connection, $"admin/api/clients/{id}/secrets").ConfigureAwait(false);

        // Fetch scopes
        var scopes = await CliAdminApiClient.GetListAsync<ClientScopeItem>(
            config, connection, $"admin/api/clients/{id}/scopes").ConfigureAwait(false);

        // Run validations
        var findings = new List<ValidationFinding>();

        ValidateSecrets(secrets, findings);
        ValidateScopes(client, scopes, findings);
        ValidateGrantTypes(client, findings);
        ValidateRedirectUris(client, findings);
        ValidatePkce(client, secrets, findings);
        ValidateLogoutUris(client, findings);
        ValidateAuthMethod(client, secrets, findings);

        if (findings.Count == 0)
        {
            findings.Add(new ValidationFinding("ok", "Configuration", "No issues detected."));
        }

        // Output
        if (format == OutputFormat.Json)
        {
            var report = new
            {
                clientId = client.ClientId,
                clientName = client.ClientName,
                internalId = id,
                findings = findings.Select(f => new { f.Severity, f.Category, f.Message })
            };
            AnsiConsole.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        AnsiConsole.MarkupLine($"[bold]Validating client:[/] {Markup.Escape(client.ClientName ?? client.ClientId ?? id.ToString())}");
        AnsiConsole.MarkupLine($"[bold]Client ID:[/]        {Markup.Escape(client.ClientId ?? "-")}");
        AnsiConsole.MarkupLine("");

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Severity")
            .AddColumn("Category")
            .AddColumn("Finding");

        foreach (var f in findings)
        {
            var sevMarkup = f.Severity switch
            {
                "error" => "[red]ERROR[/]",
                "warning" => "[yellow]WARN[/]",
                "info" => "[blue]INFO[/]",
                _ => "[green]OK[/]"
            };
            table.AddRow(sevMarkup, Markup.Escape(f.Category), Markup.Escape(f.Message));
        }

        AnsiConsole.Write(table);

        var errorCount = findings.Count(f => f.Severity == "error");
        var warnCount = findings.Count(f => f.Severity == "warning");
        if (errorCount > 0)
            AnsiConsole.MarkupLine($"\n[red]{errorCount} error(s)[/], [yellow]{warnCount} warning(s)[/]");
        else if (warnCount > 0)
            AnsiConsole.MarkupLine($"\n[yellow]{warnCount} warning(s)[/]");
        else
            AnsiConsole.MarkupLine($"\n[green]All checks passed.[/]");
    }

    private static void ValidateSecrets(IReadOnlyList<ClientSecretItem> secrets, List<ValidationFinding> findings)
    {
        if (secrets.Count == 0)
        {
            findings.Add(new ValidationFinding("info", "Secrets",
                "No secrets configured. This is expected for public clients (SPAs, native apps)."));
            return;
        }

        var activeSecrets = secrets.Where(s =>
            string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Status, "Active", StringComparison.OrdinalIgnoreCase)).ToList();

        if (activeSecrets.Count == 0)
        {
            findings.Add(new ValidationFinding("error", "Secrets",
                "No active secrets. The client cannot authenticate. Create and activate a secret."));
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var expiredActive = activeSecrets.Where(s => s.ExpiresAt.HasValue && s.ExpiresAt.Value < now).ToList();
        if (expiredActive.Count == activeSecrets.Count)
        {
            findings.Add(new ValidationFinding("error", "Secrets",
                "All active secrets have expired. The client cannot authenticate. Rotate secrets immediately."));
        }
        else if (expiredActive.Count > 0)
        {
            findings.Add(new ValidationFinding("warning", "Secrets",
                $"{expiredActive.Count} of {activeSecrets.Count} active secret(s) have expired. Consider revoking them."));
        }

        var expiringWithin30 = activeSecrets
            .Where(s => s.ExpiresAt.HasValue && s.ExpiresAt.Value > now && s.ExpiresAt.Value < now.AddDays(30))
            .ToList();
        if (expiringWithin30.Count > 0)
        {
            findings.Add(new ValidationFinding("warning", "Secrets",
                $"{expiringWithin30.Count} active secret(s) expire within 30 days. Plan rotation now."));
        }
    }

    private static void ValidateScopes(ValidateClientDetail client, IReadOnlyList<ClientScopeItem> scopes, List<ValidationFinding> findings)
    {
        var hasConfiguredScope = !string.IsNullOrWhiteSpace(client.Scope);
        if (scopes.Count == 0 && !hasConfiguredScope)
        {
            findings.Add(new ValidationFinding("warning", "Scopes",
                "No scopes assigned. Tokens will have no scopes which may cause authorization failures."));
        }
    }

    private static void ValidateGrantTypes(ValidateClientDetail client, List<ValidationFinding> findings)
    {
        var grantTypes = ParseJsonArray(client.GrantTypesJson);
        if (grantTypes.Count == 0)
        {
            findings.Add(new ValidationFinding("warning", "Grant Types",
                "No grant types configured. The client cannot obtain tokens."));
        }
    }

    private static void ValidateRedirectUris(ValidateClientDetail client, List<ValidationFinding> findings)
    {
        var grantTypes = ParseJsonArray(client.GrantTypesJson);
        var redirectUris = ParseJsonArray(client.AllowedLoginRedirectUrisJson);

        var needsRedirect = grantTypes.Any(g =>
            g.Contains("authorization_code", StringComparison.OrdinalIgnoreCase) ||
            g.Contains("implicit", StringComparison.OrdinalIgnoreCase));

        if (needsRedirect && redirectUris.Count == 0)
        {
            findings.Add(new ValidationFinding("error", "Redirect URIs",
                "Grant type requires redirect URIs but none are configured. Authorization flow will fail."));
        }

        // Check for localhost in redirect URIs (potentially dev-only configuration)
        var localhostUris = redirectUris.Where(u =>
            u.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)).ToList();
        if (localhostUris.Count > 0 && redirectUris.Count == localhostUris.Count)
        {
            findings.Add(new ValidationFinding("info", "Redirect URIs",
                "All redirect URIs point to localhost. This is suitable for development only."));
        }
    }

    private static void ValidatePkce(ValidateClientDetail client, IReadOnlyList<ClientSecretItem> secrets, List<ValidationFinding> findings)
    {
        // Public client (no secrets) without PKCE is insecure
        if (secrets.Count == 0 && !client.RequirePkce)
        {
            var grantTypes = ParseJsonArray(client.GrantTypesJson);
            if (grantTypes.Any(g => g.Contains("authorization_code", StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(new ValidationFinding("error", "PKCE",
                    "Public client using authorization_code without PKCE. This is insecure. Enable RequirePkce."));
            }
        }
    }

    private static void ValidateLogoutUris(ValidateClientDetail client, List<ValidationFinding> findings)
    {
        var logoutRedirects = ParseJsonArray(client.AllowedLogoutRedirectUrisJson);
        var grantTypes = ParseJsonArray(client.GrantTypesJson);

        // If the client has interactive grant types but no logout redirect URIs, that's a potential issue
        var isInteractive = grantTypes.Any(g =>
            g.Contains("authorization_code", StringComparison.OrdinalIgnoreCase) ||
            g.Contains("implicit", StringComparison.OrdinalIgnoreCase));

        if (isInteractive && logoutRedirects.Count == 0)
        {
            findings.Add(new ValidationFinding("info", "Logout",
                "No post-logout redirect URIs configured. Users will see a generic logout page."));
        }
    }

    private static void ValidateAuthMethod(ValidateClientDetail client, IReadOnlyList<ClientSecretItem> secrets, List<ValidationFinding> findings)
    {
        // If token auth method is client_secret_post/basic but no secrets exist
        var method = client.TokenEndpointAuthMethod;
        if (!string.IsNullOrWhiteSpace(method) &&
            (method.Contains("secret", StringComparison.OrdinalIgnoreCase)) &&
            secrets.Count == 0)
        {
            findings.Add(new ValidationFinding("error", "Auth Method",
                $"Token endpoint auth method is '{method}' but no secrets are configured. Token requests will fail."));
        }
    }

    private static List<string> ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed record ValidationFinding(string Severity, string Category, string Message);
}

// Extended client detail DTO for validation (includes grant types, redirect URIs, etc.)
public sealed class ValidateClientDetail
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    [JsonPropertyName("clientName")]
    public string? ClientName { get; set; }

    [JsonPropertyName("realmId")]
    public Guid RealmId { get; set; }

    [JsonPropertyName("realmName")]
    public string? RealmName { get; set; }

    [JsonPropertyName("requirePkce")]
    public bool RequirePkce { get; set; }

    [JsonPropertyName("requireConsent")]
    public bool RequireConsent { get; set; }

    [JsonPropertyName("requirePar")]
    public bool RequirePar { get; set; }

    [JsonPropertyName("isSystemClient")]
    public bool IsSystemClient { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("grantTypesJson")]
    public string? GrantTypesJson { get; set; }

    [JsonPropertyName("allowedLoginRedirectUrisJson")]
    public string? AllowedLoginRedirectUrisJson { get; set; }

    [JsonPropertyName("allowedLogoutRedirectUrisJson")]
    public string? AllowedLogoutRedirectUrisJson { get; set; }

    [JsonPropertyName("tokenEndpointAuthMethod")]
    public string? TokenEndpointAuthMethod { get; set; }
}
