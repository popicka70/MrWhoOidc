using System.CommandLine;
using System.Text.Json;
using MrWhoOidc.Cli.Configuration;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Profile management commands - list, show, switch, and remove profiles.
/// </summary>
public sealed class ProfileCommand : Command
{
    public ProfileCommand() : base("profile", "Manage configuration profiles")
    {
        var listCommand = new Command("list", "List all profiles");
        var listFormatOption = CreateFormatOption();
        listCommand.Options.Add(listFormatOption);
        listCommand.SetAction(parseResult => ListProfilesAsync(parseResult.GetValue(listFormatOption)));
        Subcommands.Add(listCommand);

        var showCommand = new Command("show", "Show the current profile or a named profile");
        var showNameArgument = new Argument<string?>("name")
        {
            Description = "Profile name (defaults to current profile)"
        };
        showNameArgument.Arity = ArgumentArity.ZeroOrOne;
        var showFormatOption = CreateFormatOption();
        showCommand.Arguments.Add(showNameArgument);
        showCommand.Options.Add(showFormatOption);
        showCommand.SetAction(async parseResult =>
        {
            var name = parseResult.GetValue(showNameArgument);
            var format = parseResult.GetValue(showFormatOption);
            await ShowProfileAsync(name, format);
        });
        Subcommands.Add(showCommand);
        
        var switchCommand = new Command("switch", "Switch to a different profile");
        var nameArgument = new Argument<string>("name")
        {
            Description = "Profile name to switch to"
        };
        switchCommand.Arguments.Add(nameArgument);
        switchCommand.SetAction(async parseResult =>
        {
            var name = parseResult.GetValue(nameArgument);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("The profile name argument is required.");
            }

            await SwitchProfileAsync(name);
        });
        Subcommands.Add(switchCommand);

        var removeCommand = new Command("remove", "Remove a saved profile");
        var removeNameArgument = new Argument<string>("name")
        {
            Description = "Profile name to remove"
        };
        removeCommand.Arguments.Add(removeNameArgument);
        removeCommand.SetAction(async parseResult =>
        {
            var name = parseResult.GetValue(removeNameArgument);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("The profile name argument is required.");
            }

            await RemoveProfileAsync(name);
        });
        Subcommands.Add(removeCommand);
    }

    private static Option<OutputFormat> CreateFormatOption()
    {
        return new Option<OutputFormat>("--format", "-f")
        {
            Description = "Output format (table or json)",
            DefaultValueFactory = _ => OutputFormat.Table
        };
    }

    private static async Task ListProfilesAsync(OutputFormat format)
    {
        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        if (config.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No saved profiles.[/]");
            return;
        }

        var profiles = config.Profiles
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new
            {
                Name = pair.Key,
                IsCurrent = string.Equals(pair.Key, config.CurrentProfile, StringComparison.Ordinal),
                Server = pair.Value.ServerUrl,
                Tenant = pair.Value.TenantSlug ?? string.Empty,
                Authenticated = pair.Value.IsAuthenticated,
                Expired = pair.Value.IsTokenExpired,
                PlatformAdmin = pair.Value.IsPlatformAdmin
            })
            .ToArray();

        if (format == OutputFormat.Json)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Profile", "Current", "Server", "Tenant", "Auth", "Role");
        foreach (var profile in profiles)
        {
            var authState = profile.Authenticated
                ? (profile.Expired ? "Expired" : "Authenticated")
                : "Signed out";
            var role = profile.PlatformAdmin ? "platform-admin" : "tenant-admin/user";

            table.AddRow(
                Markup.Escape(profile.Name),
                profile.IsCurrent ? "*" : string.Empty,
                Markup.Escape(profile.Server),
                Markup.Escape(string.IsNullOrWhiteSpace(profile.Tenant) ? "-" : profile.Tenant),
                authState,
                role);
        }

        AnsiConsole.Write(table);
    }

    private static async Task ShowProfileAsync(string? name, OutputFormat format)
    {
        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        var profileName = string.IsNullOrWhiteSpace(name) ? config.CurrentProfile : name;
        if (!config.Profiles.TryGetValue(profileName, out var profile))
        {
            throw new InvalidOperationException($"Profile '{profileName}' was not found.");
        }

        var model = new
        {
            Name = profileName,
            IsCurrent = string.Equals(profileName, config.CurrentProfile, StringComparison.Ordinal),
            profile.ServerUrl,
            profile.ClientId,
            profile.TenantSlug,
            profile.IsPlatformAdmin,
            profile.TokenExpiry,
            profile.TokenIntrospectedAt,
            profile.IsAuthenticated,
            profile.IsTokenExpired
        };

        if (format == OutputFormat.Json)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("Name", Markup.Escape(profileName));
        grid.AddRow("Current", model.IsCurrent ? "yes" : "no");
        grid.AddRow("Server", Markup.Escape(profile.ServerUrl));
        grid.AddRow("Client ID", Markup.Escape(string.IsNullOrWhiteSpace(profile.ClientId) ? "-" : profile.ClientId));
        grid.AddRow("Tenant", Markup.Escape(string.IsNullOrWhiteSpace(profile.TenantSlug) ? "-" : profile.TenantSlug));
        grid.AddRow("Role", profile.IsPlatformAdmin ? "platform-admin" : "tenant-admin/user");
        grid.AddRow("Authenticated", profile.IsAuthenticated ? "yes" : "no");
        grid.AddRow("Token expired", profile.IsAuthenticated ? (profile.IsTokenExpired ? "yes" : "no") : "n/a");
        grid.AddRow("Token expiry", profile.TokenExpiry?.ToString("u") ?? "-");
        grid.AddRow("Introspected", profile.TokenIntrospectedAt?.ToString("u") ?? "-");

        AnsiConsole.Write(new Panel(grid).Header($"Profile {Markup.Escape(profileName)}"));
    }

    private static async Task SwitchProfileAsync(string name)
    {
        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        if (!config.Profiles.ContainsKey(name))
        {
            throw new InvalidOperationException($"Profile '{name}' was not found.");
        }

        config.CurrentProfile = name;
        await config.SaveAsync().ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Switched to profile[/] [bold]{Markup.Escape(name)}[/]");
    }

    private static async Task RemoveProfileAsync(string name)
    {
        var config = await CliConfig.LoadAsync().ConfigureAwait(false);
        if (!config.RemoveProfile(name))
        {
            throw new InvalidOperationException($"Profile '{name}' was not found.");
        }

        await config.SaveAsync().ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Removed profile[/] [bold]{Markup.Escape(name)}[/]");
    }
}
