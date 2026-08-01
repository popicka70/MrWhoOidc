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
    public MrWhoOidc.WebAuth.Services.DelegatedAccessContextInfo? ActiveContext { get; set; }
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

        foreach (var grant in grants)
        {
            var delegatorUser = await userAccountService.GetByIdAsync(grant.DelegatorUserAccountId);
            var capabilities = ParseCapabilities(grant.CapabilitiesJson);
            var definitions = capabilities.Select(c => capabilityCatalog.GetDefinition(c)).ToList();
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == grant.TenantId);
            var client = grant.ClientId.HasValue
                ? await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == grant.ClientId.Value)
                : null;

            Grants.Add(new GrantDetail
            {
                Id = grant.Id,
                TenantId = grant.TenantId,
                TenantName = tenant?.Name ?? "Unknown Tenant",
                TenantSlug = tenant?.Slug ?? string.Empty,
                ClientName = client?.ClientName ?? client?.ClientId ?? "Legacy unbound grant",
                OidcClientId = client?.ClientId,
                DelegatorId = grant.DelegatorUserAccountId,
                DelegatorName = delegatorUser?.Name ?? delegatorUser?.Username ?? "Unknown",
                Status = grant.Status,
                Capabilities = capabilities,
                CapabilityDefinitions = definitions,
                Purpose = grant.Purpose,
                CreatedAt = grant.CreatedAt,
                ExpiresAt = grant.ExpiresAt,
                AcceptedAt = grant.AcceptedAt,
                DeclinedAt = grant.DeclinedAt,
                RevokedAt = grant.RevokedAt,
                RevocationReason = grant.RevocationReason
            });
        }

        // Check for active delegated context
        ActiveContext = await contextService.GetActiveContextAsync(HttpContext);
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

    private static Guid ResolveUserAccountId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
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
        public string ClientName { get; set; } = string.Empty;
        public string? OidcClientId { get; set; }
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

}
