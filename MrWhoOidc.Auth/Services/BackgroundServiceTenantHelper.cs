using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Helper for background services that need tenant context. Background work must cover every tenant:
/// either iterate tenants with <see cref="ForEachActiveTenantAsync"/> or, for tenant-independent set-based
/// maintenance, query with <c>IgnoreQueryFilters()</c>. Never pin a job to the default tenant.
/// </summary>
public static class BackgroundServiceTenantHelper
{
    /// <summary>
    /// Runs <paramref name="work"/> once per active tenant, each in its own DI scope with that tenant's
    /// context set, so tenant-scoped services (key rotation, tenant-filtered queries) cover every tenant
    /// rather than only the default one. A failure in one tenant is logged and does not stop the others.
    /// </summary>
    public static async Task ForEachActiveTenantAsync(
        IServiceScopeFactory scopeFactory,
        string jobName,
        Func<IServiceProvider, CancellationToken, Task> work,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        List<TenantContext> tenants;
        using (var listScope = scopeFactory.CreateScope())
        {
            var db = listScope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var isMultiTenant = listScope.ServiceProvider.GetRequiredService<IMultiTenancyOptions>().Enabled;
            tenants = await db.Tenants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => t.Status == TenantStatus.Active)
                .Select(t => new TenantContext
                {
                    TenantId = t.Id,
                    Slug = t.Slug,
                    Name = t.Name,
                    IssuerUri = t.IssuerUri,
                    IsMultiTenantMode = isMultiTenant
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var tenant in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(tenant);
            try
            {
                await work(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{Job} failed for tenant {TenantSlug}", jobName, tenant.Slug);
            }
        }
    }
}
