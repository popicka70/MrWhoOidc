using System.CommandLine;
using MrWhoOidc.Cli.Services;
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

    /// <summary>
    /// Writes a dim server/profile header line to stderr so it never pollutes
    /// JSON output on stdout. Call this after resolving an authenticated connection.
    /// </summary>
    public static void WriteServerHeader(AuthenticatedConnection connection)
    {
        Console.Error.WriteLine($"Server: {connection.ServerUrl}  (profile: {connection.ProfileName})");
    }
}
