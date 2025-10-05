using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Background;

/// <summary>
/// Helper for background services to set tenant context for their operations.
/// </summary>
internal static class BackgroundServiceTenantHelper
{
    /// <summary>
    /// Sets the tenant context to the default tenant for background service operations.
    /// Background services run outside HTTP request context, so we explicitly load and set the default tenant.
    /// </summary>
    /// <param name="scope">The service scope to resolve dependencies from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if tenant context was set successfully, false if default tenant not found.</returns>
    public static async Task<bool> TrySetDefaultTenantContextAsync(
        IServiceScope scope,
        CancellationToken cancellationToken = default)
    {
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
        var multiTenancyOptions = scope.ServiceProvider.GetRequiredService<IMultiTenancyOptions>();

        // Check if tenant context is already set
        if (tenantAccessor.CurrentTenant != null)
        {
            return true;
        }

        // Load default tenant
        var defaultTenant = await db.Tenants
            .Where(t => t.Slug == multiTenancyOptions.DefaultTenantSlug && t.Status == TenantStatus.Active)
            .FirstOrDefaultAsync(cancellationToken);

        if (defaultTenant == null)
        {
            return false;
        }

        // Set tenant context
        var tenantContext = new TenantContext
        {
            TenantId = defaultTenant.Id,
            Slug = defaultTenant.Slug,
            Name = defaultTenant.Name,
            IssuerUri = defaultTenant.IssuerUri,
            IsMultiTenantMode = multiTenancyOptions.Enabled
        };

        tenantAccessor.SetTenant(tenantContext);
        return true;
    }
}
