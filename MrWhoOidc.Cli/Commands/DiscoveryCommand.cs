using System.CommandLine;
using System.Text.Json;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

public sealed class DiscoveryCommand : Command
{
    public DiscoveryCommand() : base("discovery", "Inspect server discovery metadata")
    {
        var serverOption = new Option<string?>("--server", "-s")
        {
            Description = "OIDC server URL (defaults to the current profile server)"
        };
        var profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Profile to resolve the server from when --server is omitted"
        };
        var formatOption = new Option<OutputFormat>("--format", "-f")
        {
            Description = "Output format (table or json)",
            DefaultValueFactory = _ => OutputFormat.Table
        };

        Options.Add(serverOption);
        Options.Add(profileOption);
        Options.Add(formatOption);

        this.SetAction(async parseResult =>
        {
            var server = parseResult.GetValue(serverOption);
            var profile = parseResult.GetValue(profileOption);
            var format = parseResult.GetValue(formatOption);
            await HandleAsync(server, profile, format);
        });
    }

    private static async Task HandleAsync(string? server, string? profileName, OutputFormat format)
    {
        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        var normalizedServer = CliServerConnection.ResolveServerUrlOrThrow(config, server, profileName);

        using var httpClient = CliServerConnection.CreateHttpClient(normalizedServer);
        var discovery = await CliServerConnection.FetchDiscoveryAsync(httpClient, normalizedServer).ConfigureAwait(false);

        var payload = new
        {
            Server = normalizedServer,
            discovery.Issuer,
            discovery.AuthorizationEndpoint,
            discovery.DeviceAuthorizationEndpoint,
            discovery.TokenEndpoint,
            discovery.UserInfoEndpoint,
            discovery.JwksUri,
            discovery.RevocationEndpoint,
            discovery.CliClientId,
            discovery.GrantTypesSupported,
            discovery.ResponseTypesSupported,
            discovery.ScopesSupported,
            discovery.TokenEndpointAuthMethodsSupported
        };

        if (format == OutputFormat.Json)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("Server", Markup.Escape(normalizedServer));
        grid.AddRow("Issuer", Markup.Escape(discovery.Issuer ?? "-"));
        grid.AddRow("Authorize", Markup.Escape(discovery.AuthorizationEndpoint ?? "-"));
        grid.AddRow("Device", Markup.Escape(string.IsNullOrWhiteSpace(discovery.DeviceAuthorizationEndpoint) ? "-" : discovery.DeviceAuthorizationEndpoint));
        grid.AddRow("Token", Markup.Escape(discovery.TokenEndpoint));
        grid.AddRow("UserInfo", Markup.Escape(discovery.UserInfoEndpoint ?? "-"));
        grid.AddRow("JWKS", Markup.Escape(discovery.JwksUri ?? "-"));
        grid.AddRow("Revocation", Markup.Escape(discovery.RevocationEndpoint ?? "-"));
        grid.AddRow("CLI client", Markup.Escape(discovery.CliClientId ?? "-"));
        grid.AddRow("Grant types", Markup.Escape(JoinOrDash(discovery.GrantTypesSupported)));
        grid.AddRow("Scopes", Markup.Escape(JoinOrDash(discovery.ScopesSupported)));

        AnsiConsole.Write(new Panel(grid).Header("Discovery"));
    }
    private static string JoinOrDash(IEnumerable<string> values)
    {
        var items = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return items.Length == 0 ? "-" : string.Join(", ", items);
    }
}