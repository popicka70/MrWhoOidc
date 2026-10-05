using Microsoft.EntityFrameworkCore;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// ClientScopes.ScopeName is a FK to Scopes.Name. The seeder does not create 'mrwho:admin', so enabling CLI access
/// on PostgreSQL failed the FK (the AdminApiClientFlag migration hit the same violation in production). The InMemory
/// provider does not enforce FKs, so these tests assert the scope rows exist instead.
/// </summary>
[TestClass]
public sealed class CliClientScopeProvisioningTests
{
    [TestMethod]
    public async Task EnableCliAccess_Creates_Missing_Scope_Rows_With_Admin_Scope_Not_Exposed()
    {
        var tenantId = Guid.NewGuid();
        var accessor = MockTenantAccessor.CreateWithTenant(tenantId, "acme");
        await using var db = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, accessor, null);
        db.Realms.Add(new Realm { TenantId = tenantId, Name = "default" });
        // Only what the seeder creates: no 'mrwho:admin'.
        db.Scopes.AddRange(new[] { "openid", "profile", "email", "offline_access", "roles", "tenants" }
            .Select(n => new Scope { Name = n, IsGlobal = true, IsExposed = true }));
        await db.SaveChangesAsync();

        var service = new CliClientService(db, Mock.Of<IClientStore>());
        var client = await service.EnableCliAccessAsync(tenantId, "acme");

        var scopeNames = await db.Scopes.IgnoreQueryFilters().Select(s => s.Name).ToListAsync();
        var assigned = await db.ClientScopes.Where(cs => cs.ClientId == client.Id).Select(cs => cs.ScopeName).ToListAsync();
        CollectionAssert.IsSubsetOf(assigned, scopeNames, "every assigned scope must exist in Scopes (FK)");
        CollectionAssert.Contains(assigned, AdminApiAccess.Scope);

        var adminScope = await db.Scopes.IgnoreQueryFilters().SingleAsync(s => s.Name == AdminApiAccess.Scope);
        Assert.IsTrue(adminScope.IsGlobal);
        Assert.IsNull(adminScope.TenantId);
        Assert.IsFalse(adminScope.IsExposed, "the restricted admin scope must not be advertised in discovery");

        // Idempotent: enabling again neither duplicates scopes nor fails.
        await service.EnableCliAccessAsync(tenantId, "acme");
        Assert.AreEqual(1, await db.Scopes.IgnoreQueryFilters().CountAsync(s => s.Name == AdminApiAccess.Scope));
    }
}
