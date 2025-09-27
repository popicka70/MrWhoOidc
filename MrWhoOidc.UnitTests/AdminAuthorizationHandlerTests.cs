using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AdminAuthorizationHandlerTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public async Task AdminPolicy_Succeeds_For_User_With_Admin_Role_In_Admin_Realm()
    {
        using var db = CreateDb();
        // Seed realm and role
        var realm = new Realm { Name = "admin", DisplayName = "Admin" };
        db.Realms.Add(realm);
        var role = new Role { Name = "admin", RealmId = realm.Id, IsActive = true };
        db.Roles.Add(role);
        // Seed user and assignment
        var user = new User { Username = "test-admin", Name = "Admin" };
        db.Users.Add(user);
        db.UserRoleAssignments.Add(new UserRoleAssignment
        {
            UserId = user.Id,
            RoleId = role.Id,
            ClientId = Guid.NewGuid(),
            RealmId = realm.Id,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var options = Options.Create(new AdminAuthOptions { RealmName = "admin", AdminRoleName = "admin" });
        var handler = new AdminAuthorizationHandler(db, options);
        var requirement = new AdminRequirement();

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var context = new AuthorizationHandlerContext(new[] { requirement }, principal, resource: null);

        await handler.HandleAsync(context);

        Assert.IsTrue(context.HasSucceeded, "Expected admin requirement to succeed for seeded admin user.");
    }

    [TestMethod]
    public async Task AdminPolicy_Fails_For_User_Without_Assignment()
    {
        using var db = CreateDb();
        // Seed realm/role but no assignment
        var realm = new Realm { Name = "admin", DisplayName = "Admin" };
        db.Realms.Add(realm);
        var role = new Role { Name = "admin", RealmId = realm.Id, IsActive = true };
        db.Roles.Add(role);
        var user = new User { Username = "user", Name = "User" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var options = Options.Create(new AdminAuthOptions { RealmName = "admin", AdminRoleName = "admin" });
        var handler = new AdminAuthorizationHandler(db, options);
        var requirement = new AdminRequirement();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
        }, "test"));
        var context = new AuthorizationHandlerContext(new[] { requirement }, principal, resource: null);

        await handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded, "Expected admin requirement to fail for user without assignment.");
    }
}
