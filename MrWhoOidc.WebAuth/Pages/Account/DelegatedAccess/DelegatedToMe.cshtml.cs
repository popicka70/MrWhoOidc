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
/// Lists all delegated access grants assigned to the current user (delegate view).
/// Shows delegator name, tenant, purpose, capabilities, and expiry.
/// Includes 'Activate', 'Relinquish' actions, and status indicators.
/// </summary>
[Authorize]
public class DelegatedToMeModel(
    AuthDbContext db,
    IDelegatedAccessGrantService grantService,
    IDelegableCapabilityCatalog capabilityCatalog,
    IUserAccountService userAccountService,
    IUserTenantMembershipService membershipService,
    IDelegatedAccessContextService contextService,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions) : PageModel
{
    public List<GrantDetail> Grants { get; set; } = new();
    public DelegatedAccessContextInfo? ActiveContext { get; set; }
    public string? Message { get; set; }

    public async Task OnGetAsync()
    {
        // Check feature flag: Delegated Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableDelegatedAccess)
        {
            Message = "Feature Disabled: Delegated Access is not enabled.";
            return;
        }

        var userId = ResolveUserAccountId(User);

        // Load all grants where user is delegate
        var grants = await db.DelegatedAccessGrants
            .AsNoTracking()
            .Where(g => g.DelegateUserAccountId == userId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();

        Grants = grants.Select(g =>
        {
            var delegatorUser = await userAccountService.FindByAccountIdAsync(g.DelegatorUserAccountId);
            var capabilities = ParseCapabilities(g.CapabilitiesJson);
            var definitions = capabilities.Select(c => capabilityCatalog.GetDefinition(c)).ToList();
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == g.TenantId);

            return new GrantDetail
            {
                Id = g.Id,
                TenantId = g.TenantId,
                TenantName = tenant?.Name ?? "Unknown Tenant",
                TenantSlug = tenant?.Slug ?? string.Empty,
                DelegatorId = g.DelegatorUserAccountId,
                DelegatorName = delegatorUser?.Name ?? delegatorUser?.Username ?? "Unknown",
                Status = g.Status,
                Capabilities = capabilities,
                CapabilityDefinitions = definitions,
                Purpose = g.Purpose,
                CreatedAt = g.CreatedAt,
                ExpiresAt = g.ExpiresAt,
                AcceptedAt = g.AcceptedAt,
                DeclinedAt = g.DeclinedAt,
                RevokedAt = g.RevokedAt,
                RevocationReason = g.RevocationReason
            };
        }).ToList();

        // Check for active delegated context
        ActiveContext = await contextService.GetActiveContextAsync(HttpContext);
    }

    public async Task<IActionResult> OnPostActivateGrantAsync(Guid grantId)
    {
        var userId = ResolveUserAccountId(User);

        try
        {
            await grantService.AcceptGrantAsync("", userId)
                .ConfigureAwait(false);

            // Store active grant reference in session
            TempData["Success"] = "Grant activated. You can now act on behalf of the delegator.";
            return RedirectToPage();
        }
        catch (NotFoundError)
        {
            TempData["Error"] = "Grant not found.";
            return RedirectToPage();
        }
        catch (ConflictError)
        {
            TempData["Error"] = "Grant is already active or in a terminal state.";
            return RedirectToPage();
        }
        catch (ExpiredError)
        {
            TempData["Error"] = "Grant invitation has expired.";
            return RedirectToPage();
        }
        catch (MismatchError)
        {
            TempData["Error"] = "You are not the delegate for this grant.";
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostRelinquishGrantAsync(Guid grantId, string reason)
    {
        var userId = ResolveUserAccountId(User);

        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "Relinquishment reason is required.";
            return RedirectToPage();
        }

        try
        {
            await grantService.RevokeGrantAsync(grantId, userId, reason)
                .ConfigureAwait(false);

            TempData["Success"] = "Grant relinquished successfully.";
            return RedirectToPage();
        }
        catch (NotFoundError)
        {
            TempData["Error"] = "Grant not found.";
            return RedirectToPage();
        }
        catch (AuthorizationError)
        {
            TempData["Error"] = "You do not have permission to relinquish this grant.";
            return RedirectToPage();
        }
        catch (ConflictError)
        {
            TempData["Error"] = "Grant is in a terminal state.";
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostAcceptGrantAsync(Guid grantId)
    {
        var userId = ResolveUserAccountId(User);

        try
        {
            // Find the invitation token for this grant
            var invitationToken = await db.DelegatedAccessInvitationTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.GrantId == grantId && t.ConsumedAt == null && t.RevokedAt == null)
                .ConfigureAwait(false);

            if (invitationToken is null)
            {
                TempData["Error"] = "No active invitation found for this grant.";
                return RedirectToPage();
        }

            await grantService.AcceptGrantAsync("", userId)
                .ConfigureAwait(false);

            TempData["Success"] = "Grant accepted. You can now activate it to act on behalf of the delegator.";
            return RedirectToPage();
        }
        catch (NotFoundError)
        {
            TempData["Error"] = "Grant not found.";
            return RedirectToPage();
        }
        catch (ConflictError)
        {
            TempData["Error"] = "Grant is already accepted or in a terminal state.";
            return RedirectToPage();
        }
        catch (ExpiredError)
        {
            TempData["Error"] = "Grant invitation has expired.";
            return RedirectToPage();
        }
        catch (MismatchError)
        {
            TempData["Error"] = "You are not the delegate for this grant.";
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostDeclineGrantAsync(Guid grantId)
    {
        var userId = ResolveUserAccountId(User);

        try
        {
            var invitationToken = await db.DelegatedAccessInvitationTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.GrantId == grantId && t.ConsumedAt == null && t.RevokedAt == null)
                .ConfigureAwait(false);

            if (invitationToken is null)
            {
                TempData["Error"] = "No active invitation found for this grant.";
                return RedirectToPage();
        }

            await grantService.DeclineGrantAsync("", userId)
                .ConfigureAwait(false);

            TempData["Success"] = "Grant declined.";
            return RedirectToPage();
        }
        catch (NotFoundError)
        {
            TempData["Error"] = "Grant not found.";
            return RedirectToPage();
        }
        catch (ConflictError)
        {
            TempData["Error"] = "Grant is already accepted or declined.";
            return RedirectToPage();
        }
        catch (ExpiredError)
        {
            TempData["Error"] = "Grant invitation has expired.";
            return RedirectToPage();
        }
        catch (MismatchError)
        {
            TempData["Error"] = "You are not the delegate for this grant.";
            return RedirectToPage();
        }
    }

    private static Guid ResolveUserAccountId(ClaimsPrincipal principal)
    {
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
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

    public class GrantDetail
    {
        public Guid Id { get; set; }
        public Guid? TenantId { get; set; }
        public string TenantName { get; set; } = string.Empty;
        public string TenantSlug { get; set; } = string.Empty;
        public Guid DelegatorId { get; set; }
        public string DelegatorName { get; set; } = string.Empty;
        public DelegatedAccessGrantStatus Status { get; set; }
        public List<string> Capabilities { get; set; } = new();
        public List<DelegableCapabilityDefinition?> CapabilityDefinitions { get; set; } = new();
        public string Purpose { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset? AcceptedAt { get; set; }
        public DateTimeOffset? DeclinedAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public string? RevocationReason { get; set; }
    }

    public class DelegatedAccessContextInfo
    {
        public Guid GrantId { get; set; }
        public Guid DelegatorId { get; set; }
        public string DelegatorName { get; set; } = string.Empty;
        public string TenantName { get; set; } = string.Empty;
        public List<string> ActiveCapabilities { get; set; } = new();
        public DateTimeOffset ExpiresAt { get; set; }
        public TimeSpan Remaining { get; set; }
    }
}
