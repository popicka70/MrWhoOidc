using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.WebAuth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account.DelegatedAccess;

/// <summary>
/// POST handler for activating a delegated access grant context.
/// Stores the active grant reference in the ASP.NET session so the
/// dual-identity banner is shown on every subsequent page.
/// </summary>
[Authorize]
public class ActivateModel(
    AuthDbContext db,
    IDelegatedAccessGrantService grantService,
    IDelegableCapabilityCatalog capabilityCatalog,
    IUserAccountService userAccountService,
    IDelegatedAccessContextService contextService,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions) : PageModel
{
    public Guid Id { get; set; }
    public ActivateGrantInfo? ActiveGrantInfo { get; set; }
    public string? Message { get; set; }

    public async Task OnGetAsync(Guid id)
    {
        // Check feature flag: Delegated Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableDelegatedAccess)
        {
            Message = "Feature Disabled: Delegated Access is not enabled.";
            return;
        }

        var userId = ResolveUserAccountId();

        // Load the grant by ID
        var grant = await db.DelegatedAccessGrants
            .AsNoTracking()
            .Where(g => g.Id == id && g.DelegateUserAccountId == userId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (grant is null)
        {
            Message = "Grant not found or you are not the delegate.";
            return;
        }

        var delegatorUser = await db.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == grant.DelegatorUserAccountId);

        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == grant.TenantId);
        var capabilities = ParseCapabilities(grant.CapabilitiesJson);

        var remaining = grant.ExpiresAt - DateTimeOffset.UtcNow;
        string remainingTime;
        if (remaining.TotalMinutes < 1)
        {
            remainingTime = "< 1 min";
        }
        else if (remaining.TotalHours < 1)
        {
            remainingTime = $"{(int)remaining.TotalMinutes} min";
        }
        else
        {
            remainingTime = $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        ActiveGrantInfo = new ActivateGrantInfo
        {
            Id = grant.Id,
            DelegatorName = delegatorUser?.Name ?? delegatorUser?.Username ?? "Unknown",
            TenantName = tenant?.Name ?? "Unknown Tenant",
            Capabilities = capabilities,
            ExpiresAt = grant.ExpiresAt,
            RemainingTime = remainingTime
        };
    }

    public async Task<IActionResult> OnPostActivateAsync(Guid id)
    {
        var userId = ResolveUserAccountId();

        try
        {
            // Verify the grant exists and belongs to this user
            var grant = await db.DelegatedAccessGrants
                .AsNoTracking()
                .Where(g => g.Id == id && g.DelegateUserAccountId == userId)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (grant is null)
            {
                TempData["Error"] = "Grant not found or you are not the delegate.";
                return RedirectToPage();
            }

            // Store active grant reference in session context
            await contextService.SetActiveGrantAsync(HttpContext, id)
                .ConfigureAwait(false);

            TempData["Success"] = "Delegated context activated. You are now acting on behalf of the delegator.";
            return RedirectToPage();
        }
        catch (NotFoundError)
        {
            TempData["Error"] = "Grant not found.";
            return RedirectToPage();
        }
        catch (StatusError)
        {
            TempData["Error"] = "Grant is not active. Accept the grant first.";
            return RedirectToPage();
        }
    }

    private Guid ResolveUserAccountId()
    {
        var sub = HttpContext.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(sub) || !Guid.TryParse(sub, out var userId))
        {
            throw new AuthorizationError("Cannot resolve user account ID from claims.");
        }
        return userId;
    }

    private static List<string> ParseCapabilities(string json)
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
        return parsed ?? new List<string>();
    }

    public class ActivateGrantInfo
    {
        public Guid Id { get; set; }
        public string DelegatorName { get; set; } = string.Empty;
        public string TenantName { get; set; } = string.Empty;
        public List<string> Capabilities { get; set; } = new();
        public DateTimeOffset ExpiresAt { get; set; }
        public string RemainingTime { get; set; } = string.Empty;
    }
}
