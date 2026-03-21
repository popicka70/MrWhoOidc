using System.CommandLine;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Extension helpers for System.CommandLine Command objects.
/// </summary>
internal static class CommandExtensions
{
    /// <summary>
    /// Registers an async action that catches all exceptions and prints them
    /// via Spectre.Console instead of letting System.CommandLine emit a raw
    /// "Unhandled exception:" dump with a full stack trace.
    /// Pass --verbose on the command line to also print the full exception.
    /// </summary>
    public static void SetSafeAction(this Command command, Func<ParseResult, Task> action)
    {
        command.SetAction(async (ParseResult parseResult) =>
        {
            try
            {
                await action(parseResult).ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                if (Environment.GetCommandLineArgs().Contains("--verbose"))
                    AnsiConsole.WriteException(ex);
                return 1;
            }
        });
    }
}
