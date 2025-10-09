using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Default implementation of tenant branding service.
/// </summary>
public class TenantBrandingService : ITenantBrandingService
{
    private readonly AuthDbContext _db;
    private readonly ITenantAccessor _tenantAccessor;

    public TenantBrandingService(AuthDbContext db, ITenantAccessor tenantAccessor)
    {
        _db = db;
        _tenantAccessor = tenantAccessor;
    }

    public async Task<TenantBranding?> GetBrandingAsync(Guid tenantId)
    {
        var tenant = await _db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => new TenantBranding
            {
                LogoUrl = t.LogoUrl,
                PrimaryColor = t.PrimaryColor,
                AccentColor = t.AccentColor,
                TenantName = t.Name
            })
            .FirstOrDefaultAsync();

        return tenant;
    }

    public async Task<TenantBranding> GetCurrentTenantBrandingAsync()
    {
        var currentTenant = _tenantAccessor.CurrentTenant;
        if (currentTenant == null)
        {
            // Return default branding if no tenant context
            return new TenantBranding
            {
                TenantName = "MrWhoOidc"
            };
        }

        var branding = await GetBrandingAsync(currentTenant.TenantId);

        // Return tenant name even if no custom branding
        return branding ?? new TenantBranding
        {
            TenantName = currentTenant.Name
        };
    }
}
