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
public class ProgramSurfaceSnapshotTests
{
    private record EndpointInfo(string Pattern, string Methods, string? RateLimiter, string? Authz, bool HasAntiforgery, bool HasCors, bool IsAnonymous);

    [TestMethod, TestCategory("SafetySurface")]
    public void Endpoint_Manifest_Snapshot_Is_Stable()
    {
    var factory = (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();
        _ = factory.Server; // ensure boot
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        var list = new List<EndpointInfo>();
    foreach (var e in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = e.RoutePattern.RawText ?? string.Join('/', e.RoutePattern.PathSegments.Select(s => string.Concat(s.Parts.Select(p => p.ToString()))));
            var methods = string.Join(',', e.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods ?? Array.Empty<string>());
            string? rateLimiter = e.Metadata.FirstOrDefault(m => m.GetType().FullName?.Contains("RateLimiter") == true)?.GetType().Name;
            string? authz = e.Metadata.OfType<AuthorizeAttribute>().FirstOrDefault()?.Policy;
            bool anon = e.Metadata.OfType<AllowAnonymousAttribute>().Any();
            bool anti = e.Metadata.Any(m => m.GetType().Name.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase));
            bool cors = e.Metadata.Any(m => m.GetType().Name.Contains("EnableCors", StringComparison.OrdinalIgnoreCase));
            list.Add(new EndpointInfo(pattern, methods, rateLimiter, authz, anti, cors, anon));
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
            var existing = File.ReadAllText(snapshotFile);
            Assert.AreEqual(existing, json, "Endpoint manifest changed. If intentional, update the snapshot file to approve new surface.");
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
    const int baseline = 1036; // update if file intentionally shrinks later; failing if grows unexpectedly
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
}
