using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// C5 of the 2026-10-04 assessment: background jobs (key rotation, cleanup, BCL dispatch) were pinned to
/// the default tenant, so other tenants never had keys rotated or logout notifications delivered.
/// </summary>
[TestClass]
public sealed class BackgroundServiceTenantHelperTests
{
    [TestMethod]
    public async Task ForEachActiveTenant_VisitsEveryActiveTenant_AndIsolatesFailures()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection()
            .AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddScoped<ITenantAccessor, TenantAccessor>()
            .AddSingleton<IMultiTenancyOptions>(new MultiTenancyOptions { Enabled = true })
            .BuildServiceProvider();

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Tenants.AddRange(
                new Tenant { Slug = "default", Name = "Default", Status = TenantStatus.Active },
                new Tenant { Slug = "acme", Name = "Acme", Status = TenantStatus.Active },
                new Tenant { Slug = "broken", Name = "Broken", Status = TenantStatus.Active },
                new Tenant { Slug = "gone", Name = "Gone", Status = TenantStatus.Suspended });
            await db.SaveChangesAsync();
        }

        var visited = new List<string>();
        await BackgroundServiceTenantHelper.ForEachActiveTenantAsync(
            services.GetRequiredService<IServiceScopeFactory>(),
            "test",
            (sp, _) =>
            {
                var slug = sp.GetRequiredService<ITenantAccessor>().CurrentTenant!.Slug;
                visited.Add(slug);
                return slug == "broken" ? throw new InvalidOperationException("boom") : Task.CompletedTask;
            },
            NullLogger.Instance);

        CollectionAssert.AreEquivalent(new[] { "default", "acme", "broken" }, visited);
    }
}
