using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.Invitations;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    ITenantEnrollmentService tenantEnrollment) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public sealed class InviteInput
    {
        [Required, EmailAddress, StringLength(256)]
        public string Email { get; set; } = string.Empty;

        [StringLength(200)]
        public string? DisplayName { get; set; }

        public bool IsTenantAdmin { get; set; }

        [Range(1, 90)]
        public int ValidDays { get; set; } = 7;
    }

    [BindProperty]
    public InviteInput Input { get; set; } = new();

    public IReadOnlyList<TenantInvitationListItem> Invitations { get; private set; } = Array.Empty<TenantInvitationListItem>();

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
            return TenantAwareRedirect("/Admin/Invitations");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        try
        {
            var result = await tenantEnrollment.CreateInvitationAsync(
                tenantId.Value,
                Input.Email,
                Input.DisplayName,
                Input.IsTenantAdmin,
                TimeSpan.FromDays(Input.ValidDays),
                GetCurrentUserId(),
                User.Identity?.Name,
                HttpContext.RequestAborted);

            TempData["Success"] = $"Invitation created for {result.Invitation.Email}.";
            TempData["InvitationLink"] = BuildInvitationLink(result.Token);
            return TenantAwareRedirect("/Admin/Invitations");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DbUpdateException)
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
            return TenantAwareRedirect("/Admin/Invitations");
        }

        var revoked = await tenantEnrollment.RevokeInvitationAsync(
            tenantId.Value,
            id,
            GetCurrentUserId(),
            "Revoked by tenant admin",
            HttpContext.RequestAborted);

        TempData[revoked ? "Success" : "Error"] = revoked ? "Invitation revoked." : "Invitation could not be revoked.";
        return TenantAwareRedirect("/Admin/Invitations");
    }

    private async Task LoadAsync()
    {
        var tenantId = TenantAccessor.CurrentTenant?.TenantId;
        Invitations = tenantId.HasValue
            ? await tenantEnrollment.ListInvitationsAsync(tenantId.Value, HttpContext.RequestAborted)
            : Array.Empty<TenantInvitationListItem>();
    }

    private string BuildInvitationLink(string token)
    {
        var request = HttpContext.Request;
        return $"{request.Scheme}://{request.Host}/invitations/{Uri.EscapeDataString(token)}";
    }

    private Guid? GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}