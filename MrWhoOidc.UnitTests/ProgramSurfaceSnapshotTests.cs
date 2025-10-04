using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using MrWhoOidc.WebAuth; // for Program partial
using Microsoft.Extensions.Configuration;
using MrWhoOidc.UnitTests.Testing;

namespace MrWhoOidc.UnitTests;

[TestClass]
[DoNotParallelize]
public class ProgramSurfaceSnapshotTests
{
    // Updated model captures multiple rate limiter policies & whether CORS/authorization metadata present.
    private record EndpointInfo(string Pattern, string Methods, string[] RateLimiters, string? Authz, bool HasAntiforgery, bool HasCors, bool IsAnonymous);

    [TestMethod, TestCategory("SafetySurface")]
    public void Endpoint_Manifest_Snapshot_Is_Stable()
    {
        var factory = (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();
        _ = factory.Server; // ensure boot
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        static bool ShouldIgnorePattern(string pattern, string methods)
        {
            // Ignore conditional static assets catch-all
            if (pattern.Contains("{**path:file}", StringComparison.OrdinalIgnoreCase)) return true;
            // Heuristic: static asset endpoints produced by MapStaticAssets() when enabled.
            // They (a) do NOT start with '/', (b) contain a '.', (c) are not the well-known OIDC configuration path.
            // This excludes things like '/.well-known/openid-configuration' (starts with '/').
            if (!pattern.StartsWith('/'))
            {
                if (pattern.Contains('.') && !pattern.StartsWith(".well-known", StringComparison.OrdinalIgnoreCase))
                {
                    // Methods usually GET,HEAD but we don't strictly rely on that.
                    return true;
                }
            }
            return false;
        }

        var list = new List<EndpointInfo>();
        foreach (var e in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = e.RoutePattern.RawText ?? string.Join('/', e.RoutePattern.PathSegments.Select(s => string.Concat(s.Parts.Select(p => p.ToString()))));
            if (ShouldIgnorePattern(pattern, string.Empty)) continue;
            var methods = string.Join(',', e.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods ?? Array.Empty<string>());
            // Collect all rate limiting policy names applied (could be multiple per endpoint)
            var rlPolicies = new List<string>();
            foreach (var md in e.Metadata)
            {
                var t = md.GetType();
                if (t.FullName?.Contains("RateLimiting") == true || t.FullName?.Contains("RateLimiter") == true)
                {
                    var nameProp = t.GetProperty("PolicyName") ?? t.GetProperty("PolicyNames");
                    if (nameProp != null)
                    {
                        var val = nameProp.GetValue(md);
                        if (val is string s && !string.IsNullOrWhiteSpace(s)) rlPolicies.Add(s);
                        else if (val is IEnumerable<string> arr) rlPolicies.AddRange(arr.Where(x => !string.IsNullOrWhiteSpace(x))!);
                    }
                }
            }
            var authz = e.Metadata.OfType<AuthorizeAttribute>().FirstOrDefault()?.Policy;
            bool anon = e.Metadata.OfType<AllowAnonymousAttribute>().Any();
            bool anti = e.Metadata.Any(m => m.GetType().Name.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase));
            bool cors = e.Metadata.Any(m => m.GetType().Name.Contains("Cors", StringComparison.OrdinalIgnoreCase));
            list.Add(new EndpointInfo(pattern, methods, rlPolicies.Distinct().OrderBy(x => x).ToArray(), authz, anti, cors, anon));
        }
        // Sort for deterministic snapshot
        list = list.OrderBy(l => l.Pattern).ThenBy(l => l.Methods).ToList();
        if (list.Count == 0)
        {
            Assert.Inconclusive("No endpoints discovered yet; snapshot deferred until endpoint mapping is modularized.");
        }
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });

        // Baseline snapshot path (committed file under UnitTests/Snapshots)
        var snapshotDir = Path.Combine(GetSolutionRoot(), "MrWhoOidc.UnitTests", "Snapshots");
        var diffFile = Path.Combine(snapshotDir, "endpoint-manifest.diff.json");
        if (File.Exists(diffFile))
        {
            try
            {
                File.Delete(diffFile);
            }
            catch
            {
                // best effort cleanup; ignore if locked
            }
        }
        Directory.CreateDirectory(snapshotDir);
        var snapshotFile = Path.Combine(snapshotDir, "endpoint-manifest.snapshot.json");
        var isPlaceholder = false;
        if (File.Exists(snapshotFile))
        {
            var existing = File.ReadAllText(snapshotFile);
            try
            {
                var existingList = JsonSerializer.Deserialize<List<EndpointInfo>>(existing) ?? new();
                if (existingList.Count == 1 && string.IsNullOrEmpty(existingList[0].Pattern) && string.IsNullOrEmpty(existingList[0].Methods))
                {
                    isPlaceholder = true;
                }
            }
            catch { }
        }

        if (!File.Exists(snapshotFile) || isPlaceholder)
        {
            File.WriteAllText(snapshotFile, json);
            Assert.Inconclusive("Endpoint snapshot (re)generated from real host. Commit the updated snapshot; future diffs will then fail.");
        }
        else
        {
            var existingRaw = File.ReadAllText(snapshotFile);
            // Sanitize legacy-corrupted snapshot (historically multiple JSON arrays + appended junk)
            existingRaw = SanitizeSnapshot(existingRaw, TestContext);
            try
            {
                var existingList = JsonSerializer.Deserialize<List<EndpointInfo>>(existingRaw) ?? new();
                // Detect snapshot hygiene issues BEFORE applying ignore rules
                var duplicateGroups = existingList
                    .GroupBy(e => e.Pattern + "|" + e.Methods)
                    .Where(g => g.Count() > 1)
                    .ToList();
                var ignoredEntries = existingList.Where(e => ShouldIgnorePattern(e.Pattern, e.Methods)).ToList();

                if (duplicateGroups.Any())
                {
                    TestContext.WriteLine($"[Snapshot Hygiene] Duplicate endpoint entries detected: {duplicateGroups.Count}. Examples (Pattern|Methods -> Count):");
                    foreach (var g in duplicateGroups.Take(5))
                    {
                        TestContext.WriteLine("  " + g.Key + " -> " + g.Count());
                    }
                }
                if (ignoredEntries.Any())
                {
                    TestContext.WriteLine($"[Snapshot Hygiene] {ignoredEntries.Count} ignored/filtered static or asset endpoints still present in snapshot (they are filtered out during comparison).");
                }

                // Normalize existing snapshot by applying same ignore rules & sorting
                existingList = existingList
                    .Where(e => !ShouldIgnorePattern(e.Pattern, e.Methods))
                    .GroupBy(e => e.Pattern + "|" + e.Methods)
                    .Select(g => NormalizeExisting(g.First()))
                    .OrderBy(l => l.Pattern).ThenBy(l => l.Methods).ToList();
                var existingJsonNormalized = JsonSerializer.Serialize(existingList, new JsonSerializerOptions { WriteIndented = true });
                if (existingJsonNormalized != json)
                {
                    // Produce a focused diff for developer ergonomics
                    var currentList = JsonSerializer.Deserialize<List<EndpointInfo>>(json)!; // current already normalized & sorted
                    var existingMap = existingList.ToDictionary(e => e.Pattern + "|" + e.Methods);
                    var currentMap = currentList.ToDictionary(e => e.Pattern + "|" + e.Methods);

                    var added = currentMap.Keys.Except(existingMap.Keys).Select(k => currentMap[k]).ToList();
                    var removed = existingMap.Keys.Except(currentMap.Keys).Select(k => existingMap[k]).ToList();
                    var maybeChanged = currentMap.Keys.Intersect(existingMap.Keys)
                        .Where(k => !Equivalent(existingMap[k], currentMap[k]))
                        .Select(k => (Old: existingMap[k], New: currentMap[k]))
                        .ToList();

                    TestContext.WriteLine("==== Endpoint Snapshot Diff ====");
                    if (added.Any())
                    {
                        TestContext.WriteLine($"Added ({added.Count}):");
                        foreach (var a in added.Take(10)) TestContext.WriteLine("  + " + Describe(a));
                        if (added.Count > 10) TestContext.WriteLine("  ... (truncated)");
                    }
                    if (removed.Any())
                    {
                        TestContext.WriteLine($"Removed ({removed.Count}):");
                        foreach (var r in removed.Take(10)) TestContext.WriteLine("  - " + Describe(r));
                        if (removed.Count > 10) TestContext.WriteLine("  ... (truncated)");
                    }
                    if (maybeChanged.Any())
                    {
                        TestContext.WriteLine($"Changed ({maybeChanged.Count}):");
                        foreach (var c in maybeChanged.Take(10))
                        {
                            TestContext.WriteLine("  * " + c.Old.Pattern + "|" + c.Old.Methods);
                            TestContext.WriteLine("      Old: " + Describe(c.Old));
                            TestContext.WriteLine("      New: " + Describe(c.New));
                        }
                        if (maybeChanged.Count > 10) TestContext.WriteLine("  ... (truncated)");
                    }
                    TestContext.WriteLine("===============================");

                    // Write a diff artifact file for deeper inspection
                    try
                    {
                        var diff = new
                        {
                            Added = added,
                            Removed = removed,
                            Changed = maybeChanged.Select(c => new { Old = c.Old, New = c.New }),
                            DuplicateGroups = duplicateGroups.Select(g => new { Key = g.Key, Count = g.Count() }),
                            IgnoredPresent = ignoredEntries.Count
                        };
                        var diffJson = JsonSerializer.Serialize(diff, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(Path.Combine(snapshotDir, "endpoint-manifest.diff.json"), diffJson);
                        TestContext.WriteLine("Diff artifact written: endpoint-manifest.diff.json");
                    }
                    catch (Exception ex)
                    {
                        TestContext.WriteLine("Failed to write diff artifact: " + ex.Message);
                    }

                    Assert.Fail("Endpoint manifest changed (after normalization). See diff output above and diff artifact.");
                }
                else
                {
                    // Optional snapshot cleanup (pretty formatting & de-dup) when env var set
                    if (Environment.GetEnvironmentVariable("CLEAN_ENDPOINT_SNAPSHOT") == "1")
                    {
                        File.WriteAllText(snapshotFile, existingJsonNormalized);
                        TestContext.WriteLine("Snapshot cleaned and pretty-formatted (CLEAN_ENDPOINT_SNAPSHOT=1).");
                    }
                }
            }
            catch
            {
                // Fallback to legacy behavior if parse fails
                Assert.AreEqual(existingRaw, json, "Endpoint manifest changed. If intentional, update the snapshot file to approve new surface.");
            }
        }
    }

    [TestMethod, TestCategory("SafetySurface")]
    public void Program_LineCount_Has_Not_Grown()
    {
        var solutionRoot = GetSolutionRoot();
        var programPath = Path.Combine(solutionRoot, "MrWhoOidc.WebAuth", "Program.cs");
        Assert.IsTrue(File.Exists(programPath), "Program.cs not found");
        var lines = File.ReadAllLines(programPath).Length;
        // Baseline captured now (after Phase 1 & partial Phase 2). We assert it does not exceed this by > 5 lines.
        const int baseline = 855; // updated baseline after Phase 1 & partial Phase 2 extractions (2025-09-27)
        Assert.IsTrue(lines <= baseline + 5, $"Program.cs line count grew unexpectedly: {lines} > {baseline}+5");
    }

    // WebApplicationFactory drives real Program.cs; no manual host builder is needed now.

    public TestContext TestContext { get; set; } = null!;

    private static string GetSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MrWhoOidc.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Solution root not found");
    }
    // Local helpers
    private static bool Equivalent(EndpointInfo a, EndpointInfo b)
        => a.Pattern == b.Pattern && a.Methods == b.Methods &&
           a.Authz == b.Authz && a.HasAntiforgery == b.HasAntiforgery &&
           a.HasCors == b.HasCors && a.IsAnonymous == b.IsAnonymous &&
           a.RateLimiters.SequenceEqual(b.RateLimiters);

    private static string Describe(EndpointInfo o)
        => $"{o.Pattern} [{o.Methods}] authz={o.Authz ?? "-"} anti={(o.HasAntiforgery ? "Y" : "N")} cors={(o.HasCors ? "Y" : "N")} anon={(o.IsAnonymous ? "Y" : "N")} limiters={(o.RateLimiters.Length == 0 ? "-" : string.Join('|', o.RateLimiters))}";

    private static EndpointInfo NormalizeExisting(EndpointInfo e)
        => new EndpointInfo(e.Pattern, e.Methods, e.RateLimiters?.Distinct().OrderBy(x => x).ToArray() ?? Array.Empty<string>(), e.Authz, e.HasAntiforgery, e.HasCors, e.IsAnonymous);

    [TestMethod, TestCategory("SafetySurface")]
    public void Defined_Rate_Limiting_Policy_Names_Are_Exact_Set()
    {
        using var factory = (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            foreach (var md in e.Metadata)
            {
                var t = md.GetType();
                if (t.FullName?.Contains("RateLimiting") == true || t.FullName?.Contains("RateLimiter") == true)
                {
                    var nameProp = t.GetProperty("PolicyName") ?? t.GetProperty("PolicyNames");
                    if (nameProp != null)
                    {
                        var val = nameProp.GetValue(md);
                        if (val is string s && !string.IsNullOrWhiteSpace(s)) names.Add(s);
                        else if (val is IEnumerable<string> arr)
                        {
                            foreach (var x in arr) if (!string.IsNullOrWhiteSpace(x)) names.Add(x);
                        }
                    }
                }
            }
        }
        var namesOrdered = names.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var expected = new[] { "rl-admin", "rl-authorize", "rl-introspect", "rl-jwks", "rl-par", "rl-qr-cancel", "rl-qr-confirm", "rl-qr-poll", "rl-token", "rl-token-exchange", "rl-userinfo" }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected, namesOrdered, "Rate limiting policy name set drifted.");
    }

    [TestMethod, TestCategory("SafetySurface")]
    public void AdminAuthorizationHandler_Is_Scoped()
    {
        using var factory = (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();
        using var scope1 = factory.Services.CreateScope();
        var handlers1 = scope1.ServiceProvider.GetServices<IAuthorizationHandler>().Where(h => h.GetType().Name == "AdminAuthorizationHandler").ToList();
        Assert.IsTrue(handlers1.Count == 1, $"Expected exactly one AdminAuthorizationHandler in scope1, found {handlers1.Count}");
        var h1a = handlers1[0];
        var h1b = scope1.ServiceProvider.GetServices<IAuthorizationHandler>().First(h => h.GetType().Name == "AdminAuthorizationHandler");
        // Scoped => same instance within scope
        Assert.AreSame(h1a, h1b, "AdminAuthorizationHandler not scoped (different instances within one scope)");
        using var scope2 = factory.Services.CreateScope();
        var h2 = scope2.ServiceProvider.GetServices<IAuthorizationHandler>().First(h => h.GetType().Name == "AdminAuthorizationHandler");
        // Scoped => different instance across scopes
        Assert.AreNotSame(h1a, h2, "AdminAuthorizationHandler appears singleton (same instance across scopes)");
    }

    // Trims any trailing garbage after the first well-formed top-level JSON array.
    private static string SanitizeSnapshot(string raw, TestContext ctx)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        int firstBracket = raw.IndexOf('[');
        if (firstBracket < 0) return raw; // not JSON
        int depth = 0;
        bool inString = false;
        char prev = '\0';
        for (int i = firstBracket; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '"' && prev != '\\') inString = !inString;
            if (!inString)
            {
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        // Reached end of outer array
                        var candidate = raw.Substring(firstBracket, i - firstBracket + 1);
                        // If there's only whitespace after this, nothing to do
                        var remainder = raw.AsSpan(i + 1).ToString();
                        if (string.IsNullOrWhiteSpace(remainder)) return candidate; // already clean
                        // If remainder has non-whitespace content, log hygiene warning and return trimmed array
                        if (remainder.Any(ch => !char.IsWhiteSpace(ch)))
                        {
                            ctx.WriteLine($"[Snapshot Hygiene] Trailing {remainder.Length} chars removed from corrupted snapshot (multiple arrays / junk detected). Consider committing cleaned file.");
                            return candidate + Environment.NewLine; // newline terminate
                        }
                        return candidate;
                    }
                }
            }
            prev = c;
        }
        return raw; // fallback (malformed but let existing parse attempt handle)
    }
}
