using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.ProviderClaimMappings;

[Authorize(Policy = "tenant-admin")]
public class EditModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : ReadOnlyAdminPageModel
{
    [BindProperty]
    public InputModel? Input { get; set; }

    public Guid ProviderId { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        // Load claim mapping with JOIN to provider for tenant filtering
        var query = from mapping in db.IdentityProviderClaimMappings.AsNoTracking()
                    join provider in db.IdentityProviders on mapping.IdentityProviderId equals provider.Id
                    where mapping.Id == id
                    select new { Mapping = mapping, Provider = provider };
        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }
        query = query.Where(x => x.Provider.TenantId == currentTenantId.Value);

        var result = await query.FirstOrDefaultAsync();
        if (result is null) return NotFound();

        var entity = result.Mapping;
        ProviderId = entity.IdentityProviderId;
        Input = new InputModel
        {
            Id = entity.Id,
            ExternalClaim = entity.ExternalClaim,
            LocalClaim = entity.LocalClaim,
            Transform = entity.Transform,
            Order = entity.Order
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!ModelState.IsValid || Input is null) return Page();

        // Load claim mapping with JOIN to provider for tenant filtering
        var query = from mapping in db.IdentityProviderClaimMappings
                    join provider in db.IdentityProviders on mapping.IdentityProviderId equals provider.Id
                    where mapping.Id == id
                    select new { Mapping = mapping, Provider = provider };
        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }
        query = query.Where(x => x.Provider.TenantId == currentTenantId.Value);

        var result = await query.FirstOrDefaultAsync();
        if (result is null) return NotFound();

        var entity = result.Mapping;
        ProviderId = entity.IdentityProviderId;

        entity.ExternalClaim = Input.ExternalClaim.Trim();
        entity.LocalClaim = Input.LocalClaim.Trim();
        entity.Transform = string.IsNullOrWhiteSpace(Input.Transform) ? null : Input.Transform.Trim();
        entity.Order = Input.Order;
        await db.SaveChangesAsync();
        var url = MrWhoOidc.WebAuth.Extensions.TenantAwareUrlBuilder.BuildTenantPath(
            "/Admin/ProviderClaimMappings",
            tenantAccessor,
            multiTenancyOptions,
            ("providerId", ProviderId.ToString()));
        return Redirect(url);
    }

    public sealed class InputModel
    {
        public Guid Id { get; set; }
        [Required, StringLength(200)]
        public string ExternalClaim { get; set; } = string.Empty;
        [Required, StringLength(200)]
        public string LocalClaim { get; set; } = string.Empty;
        [StringLength(200)]
        public string? Transform { get; set; }
        public int Order { get; set; } = 0;
    }
}
