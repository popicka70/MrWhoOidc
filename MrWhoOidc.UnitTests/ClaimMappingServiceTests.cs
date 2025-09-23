using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class ClaimMappingServiceTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [TestMethod]
    public async Task ApplyAsync_Transforms_Copy_Trim_Case_Prefix_Suffix_Regex_Concat()
    {
        using var db = CreateDb();
        var provider = new IdentityProvider { Name = "oidc", DisplayName = "OIDC" };
        db.IdentityProviders.Add(provider);
        await db.SaveChangesAsync();

        // Create mappings
        db.IdentityProviderClaimMappings.AddRange(
            new IdentityProviderClaimMapping { IdentityProviderId = provider.Id, ExternalClaim = "given_name", LocalClaim = "first_name", Transform = "copy", Order = 1 },
            new IdentityProviderClaimMapping { IdentityProviderId = provider.Id, ExternalClaim = "sn", LocalClaim = "last_name", Transform = "trim", Order = 2 },
            new IdentityProviderClaimMapping { IdentityProviderId = provider.Id, ExternalClaim = "dept", LocalClaim = "dept_lower", Transform = "case:lower", Order = 3 },
            new IdentityProviderClaimMapping { IdentityProviderId = provider.Id, ExternalClaim = "dept", LocalClaim = "dept_upper", Transform = "case:upper", Order = 4 },
            new IdentityProviderClaimMapping { IdentityProviderId = provider.Id, ExternalClaim = "uid", LocalClaim = "uid_pref", Transform = "prefix:ID-", Order = 5 },
            new IdentityProviderClaimMapping { IdentityProviderId = provider.Id, ExternalClaim = "uid", LocalClaim = "uid_suf", Transform = "suffix:-X", Order = 6 },
            new IdentityProviderClaimMapping { IdentityProviderId = provider.Id, ExternalClaim = "email", LocalClaim = "email_user", Transform = "regex:/@.*$//", Order = 7 },
            new IdentityProviderClaimMapping { IdentityProviderId = provider.Id, ExternalClaim = "ignore", LocalClaim = "full_name", Transform = "concat:given_name,sn|sep= ", Order = 8 }
        );
        await db.SaveChangesAsync();

        var svc = new ClaimMappingService(db);
        var src = new Dictionary<string, string?>
        {
            ["given_name"] = "Alice",
            ["sn"] = "  Smith ",
            ["dept"] = "Sales",
            ["uid"] = "42",
            ["email"] = "alice@example.com"
        };
        var result = await svc.ApplyAsync(provider.Id, src);

        Assert.AreEqual("Alice", result["first_name"]);
        Assert.AreEqual("Smith", result["last_name"]);
        Assert.AreEqual("sales", result["dept_lower"]);
        Assert.AreEqual("SALES", result["dept_upper"]);
        Assert.AreEqual("ID-42", result["uid_pref"]);
        Assert.AreEqual("42-X", result["uid_suf"]);
        Assert.AreEqual("alice", result["email_user"]);
        Assert.AreEqual("Alice Smith", result["full_name"]);
    }
}
