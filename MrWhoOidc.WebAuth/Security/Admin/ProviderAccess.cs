using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Who may change an identity provider and its keys, claim mappings and logo: a platform admin any provider, a tenant
/// admin only providers of their own tenant. Platform-wide providers (TenantId == null) are visible in every tenant
/// through the optional tenant filter, so a lookup by id alone let any tenant admin change them (V6).
/// </summary>
public static class ProviderAccess
{
    public static async Task<bool> CanManageAsync(
        Guid providerId,
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var platformAdminResult = await authorizationService.AuthorizeAsync(user, "platform-admin");
        if (platformAdminResult.Succeeded)
        {
            return await db.IdentityProviders.AsNoTracking().AnyAsync(p => p.Id == providerId, ct);
        }

        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return false;
        }

        return await db.IdentityProviders.AsNoTracking()
            .AnyAsync(p => p.Id == providerId && p.TenantId == currentTenantId.Value, ct);
    }
}
