using System.CommandLine;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Protocols;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Logout command - revokes the stored tokens and clears them.
/// </summary>
public sealed class LogoutCommand : Command
{
    public LogoutCommand() : base("logout", "Clear authentication tokens")
    {
        var profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Profile to sign out (defaults to current profile)"
        };

        Options.Add(profileOption);
        this.SetSafeAction(parseResult => HandleAsync(parseResult.GetValue(profileOption)));
    }

    private static async Task HandleAsync(string? profileName)
    {
        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        var resolvedProfileName = string.IsNullOrWhiteSpace(profileName) ? config.CurrentProfile : profileName;

        if (!config.Profiles.TryGetValue(resolvedProfileName, out var profile))
        {
            throw new InvalidOperationException($"Profile '{resolvedProfileName}' was not found.");
        }

        var hadTokens = profile.IsAuthenticated;

        // The tokens are revoked on the server first: clearing them locally would leave a live refresh token
        // behind that keeps working until it expires.
        await RevokeTokensAsync(profile).ConfigureAwait(false);

        profile.AccessToken = null;
        profile.RefreshToken = null;
        profile.TokenExpiry = null;
        profile.TokenIntrospectedAt = null;
        config.Profiles[resolvedProfileName] = profile;

        await config.SaveAsync().ConfigureAwait(false);

        if (hadTokens)
        {
            AnsiConsole.MarkupLine($"[green]Signed out profile[/] [bold]{Markup.Escape(resolvedProfileName)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Profile[/] [bold]{Markup.Escape(resolvedProfileName)}[/] [yellow]was already signed out.[/]");
        }
    }

    /// <summary>
    /// Revokes the stored tokens on the server (RFC 7009). A failure is reported but does not keep the local
    /// tokens: the user asked to sign out.
    /// </summary>
    private static async Task RevokeTokensAsync(ProfileConfig profile)
    {
        var refreshToken = profile.RefreshToken;
        var token = !string.IsNullOrWhiteSpace(refreshToken) ? refreshToken : profile.AccessToken;
        if (string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(profile.ServerUrl)
            || string.IsNullOrWhiteSpace(profile.ClientId))
        {
            return;
        }

        var server = CliServerConnection.NormalizeServerUrl(profile.ServerUrl);
        try
        {
            using var httpClient = CliServerConnection.CreateHttpClient(server);
            var discovery = await CliServerConnection.FetchDiscoveryAsync(httpClient, server).ConfigureAwait(false);
            var revocationEndpoint = string.IsNullOrWhiteSpace(discovery.RevocationEndpoint)
                ? $"{server}/revoke"
                : discovery.RevocationEndpoint;

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [OAuthConstants.Parameters.Token] = token!,
                [OAuthConstants.Parameters.TokenTypeHint] = string.IsNullOrWhiteSpace(refreshToken) ? "access_token" : "refresh_token",
                [OAuthConstants.Parameters.ClientId] = profile.ClientId
            });

            using var response = await httpClient.PostAsync(revocationEndpoint, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(
                    $"WARNING: the server refused to revoke the tokens (HTTP {(int)response.StatusCode}); the local tokens were cleared anyway.");
            }
        }
        catch (Exception ex)
        {
            // The local sign-out must happen even when the server cannot be reached.
            Console.Error.WriteLine(
                $"WARNING: the tokens could not be revoked ({ex.Message}); the local tokens were cleared anyway.");
        }
    }
}
