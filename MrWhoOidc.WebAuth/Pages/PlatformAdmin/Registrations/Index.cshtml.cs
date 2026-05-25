using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Security.Admin;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Registrations;

[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class IndexModel(
    AuthDbContext db,
    IMultiTenancyOptions multiTenancyOptions,
    IRegistrationWorkflowService registrationService,
    ILogger<IndexModel> logger) : PageModel
{
    public IReadOnlyList<ItemVm> Items { get; private set; } = [];

    public record ItemVm(Guid Id, string Email, string? FirstName, string? LastName, string State, string CreatedAtLocal, string? Decision, bool IsLegacyPlatformCandidate);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var platformTenantId = await GetPlatformTenantIdAsync(ct);
        if (!platformTenantId.HasValue)
        {
            Items = [];
            return;
        }

        var regs = await BuildPlatformRegistrationQuery(platformTenantId.Value)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        Items = regs.Select(r =>
        {
            var created = r.CreatedAt.ToLocalTime().ToString("g");
            string? decision = r.State == "approved"
                ? (r.ApprovedAt.HasValue ? $"Approved {r.ApprovedAt.Value.ToLocalTime():g}" : "Approved")
                : r.State == "rejected"
                    ? (r.RejectedAt.HasValue ? $"Rejected {r.RejectedAt.Value.ToLocalTime():g}" : "Rejected")
                    : null;

            return new ItemVm(
                r.Id,
                r.Email,
                r.FirstName,
                r.LastName,
                r.State,
                created,
                decision,
                IsLegacyPlatformCandidate(r, platformTenantId.Value));
        }).ToList();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken ct)
    {
        var platformTenantId = await GetPlatformTenantIdAsync(ct);
        if (!platformTenantId.HasValue)
        {
            TempData["ErrorMessage"] = "Platform tenant could not be resolved.";
            return RedirectToPage();
        }

        var reg = await BuildPlatformRegistrationQuery(platformTenantId.Value)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reg is null || !string.Equals(reg.State, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToPage();
        }

        try
        {
            await registrationService.ApproveRegistrationAsync(reg, GetCurrentUserId(), ct);
            TempData["SuccessMessage"] = $"Platform registration for {reg.Email} was approved. Confirmation email dispatch has been attempted.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Platform registration approval failed for {RegistrationId}", id);
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken ct)
    {
        var platformTenantId = await GetPlatformTenantIdAsync(ct);
        if (!platformTenantId.HasValue)
        {
            TempData["ErrorMessage"] = "Platform tenant could not be resolved.";
            return RedirectToPage();
        }

        var reg = await BuildPlatformRegistrationQuery(platformTenantId.Value)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reg is null || !string.Equals(reg.State, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToPage();
        }

        reg.State = "rejected";
        reg.RejectedAt = DateTimeOffset.UtcNow;
        reg.RejectedByUserId = GetCurrentUserId();
        await db.SaveChangesAsync(ct);

        TempData["SuccessMessage"] = $"Platform registration for {reg.Email} was rejected.";
        return RedirectToPage();
    }

    private IQueryable<Registration> BuildPlatformRegistrationQuery(Guid platformTenantId)
        => db.Set<Registration>().Where(r => r.IsPlatformRegistration || (r.TenantId == platformTenantId && !r.IsTenantAdmin && r.ClientId == null));

    private static bool IsLegacyPlatformCandidate(Registration registration, Guid platformTenantId)
        => !registration.IsPlatformRegistration && registration.TenantId == platformTenantId && !registration.IsTenantAdmin && registration.ClientId == null;

    private async Task<Guid?> GetPlatformTenantIdAsync(CancellationToken ct)
    {
        var defaultTenantSlug = multiTenancyOptions.DefaultTenantSlug ?? "default";
        var tenantId = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Slug == defaultTenantSlug && t.Status == TenantStatus.Active)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
        return tenantId;
    }

    private Guid? GetCurrentUserId()
    {
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}