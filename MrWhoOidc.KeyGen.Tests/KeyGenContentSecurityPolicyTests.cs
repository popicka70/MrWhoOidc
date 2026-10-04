using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.KeyGen.Persistence;

namespace MrWhoOidc.KeyGen.Tests;

/// <summary>
/// R21: the KeyGen CSP must not allow inline script or inline style, so the rendered pages
/// must carry no inline executable script, no on* handler attributes and no style attributes.
/// </summary>
[TestClass]
public sealed partial class KeyGenContentSecurityPolicyTests
{
    private const string AdminRole = "platform-admin";

    private static readonly string[] Pages =
    [
        "/",
        "/Privacy",
        "/KeyGeneration/Generate",
        "/KeyGeneration/List",
        "/LicenseGeneration/PlatformLicense",
        "/LicenseGeneration/TenantLicense",
        "/LicenseGeneration/Generate",
        "/LicenseGeneration/List"
    ];

    [TestMethod]
    public async Task Csp_forbids_inline_script_and_inline_style()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc(useTestUserScheme: true);
        using var client = CreateAdminClient(factory);

        using var response = await client.GetAsync("/KeyGeneration/Generate");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var directives = ParseCsp(response);

        Assert.IsTrue(directives.TryGetValue("script-src", out var scriptSrc), "CSP has no script-src");
        CollectionAssert.DoesNotContain(scriptSrc, "'unsafe-inline'");
        CollectionAssert.DoesNotContain(scriptSrc, "'unsafe-eval'");
        Assert.IsTrue(directives.TryGetValue("style-src", out var styleSrc), "CSP has no style-src");
        CollectionAssert.DoesNotContain(styleSrc, "'unsafe-inline'");
        Assert.IsFalse(directives.ContainsKey("script-src-attr"), "script-src-attr would re-open inline handlers");
        Assert.IsFalse(directives.ContainsKey("style-src-attr"), "style-src-attr would re-open inline styles");
        CollectionAssert.AreEqual(new[] { "'none'" }, directives["object-src"]);
        CollectionAssert.AreEqual(new[] { "'none'" }, directives["frame-ancestors"]);
    }

    [TestMethod]
    public async Task Csp_is_also_sent_to_anonymous_requests()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc();
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.GetAsync("/health");

        var directives = ParseCsp(response);
        CollectionAssert.DoesNotContain(directives["script-src"], "'unsafe-inline'");
    }

    [TestMethod]
    public async Task Main_pages_have_no_inline_script_handlers_or_styles()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc(useTestUserScheme: true);
        using var client = CreateAdminClient(factory);

        foreach (var path in Pages)
        {
            using var response = await client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
            AssertCspCompatible(path, await response.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    public async Task Result_and_detail_views_have_no_inline_script_handlers_or_styles()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc(useTestUserScheme: true);
        using var client = CreateAdminClient(factory);

        // Key pair result view (download buttons) and the pages that list/show the key (revoke + copy).
        var keyResult = await PostFormAsync(client, "/KeyGeneration/Generate", new Dictionary<string, string>
        {
            ["Algorithm"] = "ES256",
            ["Curve"] = "P-256"
        });
        StringAssert.Contains(keyResult, "data-download-source=\"privateKeyData\"");
        AssertCspCompatible("POST /KeyGeneration/Generate", keyResult);

        string kid;
        await using (var db = OpenDb(factory))
        {
            kid = (await db.KeyPairMetadata.SingleAsync()).Kid;
        }

        var keyList = await client.GetStringAsync("/KeyGeneration/List");
        StringAssert.Contains(keyList, "data-confirm=");
        AssertCspCompatible("/KeyGeneration/List (with key)", keyList);

        var details = await client.GetStringAsync($"/KeyGeneration/Details/{kid}");
        StringAssert.Contains(details, "data-copy-text=");
        StringAssert.Contains(details, "data-confirm=");
        AssertCspCompatible("/KeyGeneration/Details", details);

        // License result views (download button) and the license list (copy button).
        var platformResult = await PostFormAsync(client, "/LicenseGeneration/PlatformLicense", new Dictionary<string, string>
        {
            ["Tier"] = "community",
            ["DeploymentMode"] = "single_tenant",
            ["Organization"] = "Acme",
            ["NotBefore"] = DateTime.UtcNow.Date.ToString("yyyy-MM-dd"),
            ["ExpiresAt"] = DateTime.UtcNow.Date.AddYears(1).ToString("yyyy-MM-dd")
        });
        StringAssert.Contains(platformResult, "data-download-source=\"licenseTokenData\"");
        AssertCspCompatible("POST /LicenseGeneration/PlatformLicense", platformResult);

        var legacyResult = await PostFormAsync(client, "/LicenseGeneration/Generate", new Dictionary<string, string>
        {
            ["Tier"] = "community",
            ["Scope"] = "platform",
            ["Organization"] = "Acme",
            ["NotBefore"] = DateTime.UtcNow.Date.ToString("yyyy-MM-dd"),
            ["ExpiresAt"] = DateTime.UtcNow.Date.AddYears(1).ToString("yyyy-MM-dd")
        });
        AssertCspCompatible("POST /LicenseGeneration/Generate", legacyResult);

        var licenseList = await client.GetStringAsync("/LicenseGeneration/List");
        StringAssert.Contains(licenseList, "data-copy-text=");
        AssertCspCompatible("/LicenseGeneration/List (with license)", licenseList);
    }

    [TestMethod]
    public async Task Every_referenced_script_is_served_from_the_app()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc(useTestUserScheme: true);
        using var client = CreateAdminClient(factory);

        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Pages)
        {
            var html = await client.GetStringAsync(path);
            foreach (Match match in ScriptSrcRegex().Matches(html))
            {
                sources.Add(WebUtility.HtmlDecode(match.Groups[1].Value));
            }
        }

        // asp-append-version with static asset endpoints renders fingerprinted names (/js/site.{hash}.js).
        foreach (var name in new[] { "site", "key-generate", "license-generate", "platform-license", "tenant-license" })
        {
            var pattern = new Regex($@"^/js/{Regex.Escape(name)}(\.[a-z0-9]+)?\.js(\?|$)");
            Assert.IsTrue(sources.Any(pattern.IsMatch), $"{name}.js is not referenced by any page: {string.Join(", ", sources)}");
        }

        foreach (var src in sources)
        {
            Assert.IsTrue(src.StartsWith('/') && !src.StartsWith("//", StringComparison.Ordinal), $"Script {src} is not same-origin");
            using var response = await client.GetAsync(src);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, src);
        }
    }

    private static void AssertCspCompatible(string page, string html)
    {
        foreach (Match match in ScriptTagRegex().Matches(html))
        {
            var attributes = match.Groups[1].Value;
            var isExternal = ScriptSrcAttributeRegex().IsMatch(attributes);
            var isJsonDataBlock = attributes.Contains("type=\"application/json\"", StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(isExternal || isJsonDataBlock, $"{page}: inline executable script <script{attributes}>");
        }

        var handler = InlineHandlerRegex().Match(html);
        Assert.IsFalse(handler.Success, $"{page}: inline event handler in {handler.Value}");

        var style = InlineStyleRegex().Match(html);
        Assert.IsFalse(style.Success, $"{page}: inline style attribute in {style.Value}");

        StringAssert.DoesNotMatch(html, StyleElementRegex(), $"{page}: inline <style> element");
        StringAssert.DoesNotMatch(html, JavascriptUrlRegex(), $"{page}: javascript: URL");
    }

    private static Dictionary<string, string[]> ParseCsp(HttpResponseMessage response)
    {
        Assert.IsTrue(response.Headers.TryGetValues("Content-Security-Policy", out var values), "No CSP header");
        var policy = Assert.ContainsSingle(values);
        return policy
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts[1..], StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> PostFormAsync(HttpClient client, string path, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await KeyGenAppFactory.GetAntiforgeryTokenAsync(client, path);
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(fields));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
        return await response.Content.ReadAsStringAsync();
    }

    private static HttpClient CreateAdminClient(KeyGenAppFactory factory)
    {
        var client = factory.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Add("X-Test-Sub", "admin-1");
        client.DefaultRequestHeaders.Add("X-Test-Roles", AdminRole);
        return client;
    }

    private static KeyGenDbContext OpenDb(KeyGenAppFactory factory) =>
        new(new DbContextOptionsBuilder<KeyGenDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = factory.DatabasePath }.ToString())
            .Options);

    [GeneratedRegex(@"<script\b([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"\ssrc\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptSrcAttributeRegex();

    [GeneratedRegex(@"<script\b[^>]*\ssrc=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptSrcRegex();

    [GeneratedRegex(@"<[a-zA-Z][^>]*\son[a-zA-Z]+\s*=[^>]*>")]
    private static partial Regex InlineHandlerRegex();

    [GeneratedRegex(@"<[a-zA-Z][^>]*\sstyle\s*=[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineStyleRegex();

    [GeneratedRegex(@"<style\b", RegexOptions.IgnoreCase)]
    private static partial Regex StyleElementRegex();

    [GeneratedRegex(@"(href|src|action)\s*=\s*""\s*javascript:", RegexOptions.IgnoreCase)]
    private static partial Regex JavascriptUrlRegex();
}
