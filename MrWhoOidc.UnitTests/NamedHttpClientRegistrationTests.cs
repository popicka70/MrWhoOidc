using System.Text.RegularExpressions;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Guards against IHttpClientFactory.CreateClient("literal") calls whose name was never registered.
/// An unregistered name silently yields a default client, bypassing SSRF handlers configured on the
/// intended named client (this is how DCR sector_identifier_uri fetches lost their private-IP guard).
/// </summary>
[TestClass]
public sealed class NamedHttpClientRegistrationTests
{
    [TestMethod]
    public void CreateClient_StringLiterals_AreNotUsedInProductionCode()
    {
        var root = FindRepoRoot();
        var offenders = new[] { "MrWhoOidc.Auth", "MrWhoOidc.WebAuth" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains("graphify-out"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Regex.IsMatch(x.line, @"\.CreateClient\(\s*""", RegexOptions.CultureInvariant))
            .Select(x => $"{Path.GetRelativePath(root, x.f)}:{x.i + 1}")
            .ToList();

        Assert.AreEqual(0, offenders.Count,
            "Use the registering type's name constant instead of a string literal: " + string.Join(", ", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MrWhoOidc.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}
