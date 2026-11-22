using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin;

/// <summary>
/// Tenant manager view of assistance requests and sessions. Visibility and revocation are gated by the
/// tenant's configured manager roles inside the service, not by a hardcoded role name.
/// </summary>
[Authorize]
public class AssistanceModel(
    IUserAssistanceService assistanceService,
    IOptions<AuthOptions> authOptions,
    ITenantAccessor tenantAccessor) : PageModel
{
    public bool FeatureEnabled { get; private set; }
    public string? Message { get; private set; }
    public List<UserAssistanceInfo> Sessions { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public string TenantSlug { get; set; } = string.Empty;

    [BindProperty]
    public Guid SessionId { get; set; }

    [BindProperty]
    [StringLength(500)]
    public string? Reason { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync().ConfigureAwait(false);
    }

    public async Task<IActionResult> OnPostRevokeAsync()
    {
        var result = await assistanceService.EndAsync(
            HttpContext,
            User,
            SessionId,
            string.IsNullOrWhiteSpace(Reason) ? "Revoked by tenant manager" : Reason,
            HttpContext.RequestAborted).ConfigureAwait(false);

        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? "Assistance session revoked. The helper's access and tokens were revoked."
            : result.Error ?? "The session could not be revoked.";
        return RedirectToPage("/Admin/Assistance", new { tenantSlug = TenantSlug });
    }

    private async Task LoadAsync()
    {
        FeatureEnabled = authOptions.Value.EnableUserAssistance;
        if (!FeatureEnabled)
        {
            Message = "User assistance is disabled.";
            return;
        }

        var tenant = tenantAccessor.CurrentTenant;
        if (tenant is null)
        {
            Message = "No tenant context is available.";
            return;
        }

        Sessions = (await assistanceService.ListForManagerAsync(
                User,
                tenant.TenantId,
                HttpContext.RequestAborted).ConfigureAwait(false))
            .ToList();

        if (Sessions.Count == 0)
        {
            Message = "No assistance requests or sessions are visible to you in this tenant.";
        }
    }
}
