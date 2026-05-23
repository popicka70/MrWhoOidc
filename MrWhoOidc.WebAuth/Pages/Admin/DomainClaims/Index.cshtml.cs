using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.DomainClaims;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    ITenantDomainClaimService domainClaims) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public sealed class ClaimInput
    {
        [Required, StringLength(253)]
        public string Domain { get; set; } = string.Empty;

        public TenantDomainEnrollmentMode EnrollmentMode { get; set; } = TenantDomainEnrollmentMode.AutoJoin;
    }

    [BindProperty]
    public ClaimInput Input { get; set; } = new();

    public IReadOnlyList<TenantDomainClaimListItem> Claims { get; private set; } = Array.Empty<TenantDomainClaimListItem>();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        var tenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!tenantId.HasValue)
        {
            TempData["Error"] = "Unable to determine current tenant context.";
            return TenantAwareRedirect("/admin/domain-claims");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        try
        {
            var result = await domainClaims.CreateClaimAsync(
                tenantId.Value,
                Input.Domain,
                Input.EnrollmentMode,
                GetCurrentUserId(),
                User.Identity?.Name,
                HttpContext.RequestAborted);

            TempData["Success"] = $"Domain claim created for {result.Claim.Domain}.";
            return TenantAwareRedirect("/admin/domain-claims");
        }
        catch (Exception ex) when (ex is ValidationException or ArgumentException or InvalidOperationException or DbUpdateException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id)
    {
        var tenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!tenantId.HasValue)
        {
            TempData["Error"] = "Unable to determine current tenant context.";
            return TenantAwareRedirect("/admin/domain-claims");
        }

        var revoked = await domainClaims.RevokeClaimAsync(
            tenantId.Value,
            id,
            GetCurrentUserId(),
            "Revoked by tenant admin",
            HttpContext.RequestAborted);

        TempData[revoked ? "Success" : "Error"] = revoked ? "Domain claim revoked." : "Domain claim could not be revoked.";
        return TenantAwareRedirect("/admin/domain-claims");
    }

    private async Task LoadAsync()
    {
        var tenantId = TenantAccessor.CurrentTenant?.TenantId;
        Claims = tenantId.HasValue
            ? await domainClaims.ListClaimsAsync(tenantId.Value, HttpContext.RequestAborted)
            : Array.Empty<TenantDomainClaimListItem>();
    }

    private Guid? GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}