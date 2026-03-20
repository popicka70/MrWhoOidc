using System.CommandLine;
using MrWhoOidc.Cli.Configuration;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Logout command - clears stored authentication tokens.
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
        this.SetAction(parseResult => HandleAsync(parseResult.GetValue(profileOption)));
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
}
