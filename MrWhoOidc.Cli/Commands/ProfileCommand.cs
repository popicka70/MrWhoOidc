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
        listCommand.SetSafeAction(parseResult => ListProfilesAsync(parseResult.GetValue(listFormatOption)));
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
        showCommand.SetSafeAction(async parseResult =>
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
        switchCommand.SetSafeAction(async parseResult =>
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
        removeCommand.SetSafeAction(async parseResult =>
        {
            var name = parseResult.GetValue(removeNameArgument);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("The profile name argument is required.");
            }

            await RemoveProfileAsync(name);
        });
        Subcommands.Add(removeCommand);

        var renameCommand = new Command("rename", "Rename a profile");
        var renameOldArgument = new Argument<string>("old-name")
        {
            Description = "Current profile name"
        };
        var renameNewArgument = new Argument<string>("new-name")
        {
            Description = "New profile name (codename: alphanumeric + hyphens, or the profile's server URL)"
        };
        renameCommand.Arguments.Add(renameOldArgument);
        renameCommand.Arguments.Add(renameNewArgument);
        renameCommand.SetSafeAction(async parseResult =>
        {
            var oldName = parseResult.GetValue(renameOldArgument);
            var newName = parseResult.GetValue(renameNewArgument);
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            {
                throw new InvalidOperationException("Both old-name and new-name arguments are required.");
            }

            await RenameProfileAsync(oldName, newName);
        });
        Subcommands.Add(renameCommand);
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
            AnsiConsole.WriteLine(JsonSerializer.Serialize(profiles, SharedJsonOptions.IndentedOptions));
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
            AnsiConsole.WriteLine(JsonSerializer.Serialize(model, SharedJsonOptions.IndentedOptions));
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

    private static async Task RenameProfileAsync(string oldName, string newName)
    {
        if (!CliConfig.IsValidProfileName(newName))
        {
            throw new InvalidOperationException(
                $"Invalid profile name '{newName}'. Use a codename (alphanumeric and hyphens, e.g. 'my-prod') or the profile's server URL.");
        }

        var config = await CliConfig.LoadAsync().ConfigureAwait(false);

        if (!config.Profiles.ContainsKey(oldName))
        {
            throw new InvalidOperationException($"Profile '{oldName}' was not found.");
        }

        if (config.Profiles.ContainsKey(newName))
        {
            throw new InvalidOperationException($"A profile named '{newName}' already exists.");
        }

        // When the new name is a URL, verify it matches the profile's server URL
        if (Uri.TryCreate(newName, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var profile = config.Profiles[oldName];
            if (!string.Equals(newName.TrimEnd('/'), profile.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"URL-based profile name must match the profile's server URL ({profile.ServerUrl}).");
            }
        }

        if (!config.RenameProfile(oldName, newName))
        {
            throw new InvalidOperationException($"Failed to rename profile '{oldName}' to '{newName}'.");
        }

        await config.SaveAsync().ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Renamed profile[/] [bold]{Markup.Escape(oldName)}[/] → [bold]{Markup.Escape(newName)}[/]");
    }
}
