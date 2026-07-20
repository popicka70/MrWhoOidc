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
/// Lists all delegated access grants created by the current user (delegator view).
/// Shows status, delegate, tenant, purpose, capabilities, and expiry.
/// Includes a 'Revoke' action for revocable grants.
/// </summary>
[Authorize]
public class GrantedByMeModel(
    AuthDbContext db,
    IDelegatedAccessGrantService grantService,
    IDelegableCapabilityCatalog capabilityCatalog,
    IUserAccountService userAccountService,
    IUserTenantMembershipService membershipService,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions) : PageModel
{
    public List<GrantDetail> Grants { get; set; } = new();
    public string? Message { get; set; }
    public bool HasPendingRevoke { get; set; }

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

        // Load all grants where user is delegator
        var grants = await db.DelegatedAccessGrants
            .AsNoTracking()
            .Where(g => g.DelegatorUserAccountId == userId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();

        Grants = grants.Select(g =>
        {
            var delegateUser = await userAccountService.FindByAccountIdAsync(g.DelegateUserAccountId);
            var capabilities = ParseCapabilities(g.CapabilitiesJson);
            var definitions = capabilities.Select(c => capabilityCatalog.GetDefinition(c)).ToList();
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == g.TenantId);

            return new GrantDetail
            {
                Id = g.Id,
                TenantId = g.TenantId,
                TenantName = tenant?.Name ?? "Unknown Tenant",
                TenantSlug = tenant?.Slug ?? string.Empty,
                DelegateId = g.DelegateUserAccountId,
                DelegateName = delegateUser?.Name ?? delegateUser?.Username ?? "Unknown",
                Status = g.Status,
                Capabilities = capabilities,
                CapabilityDefinitions = definitions,
                Purpose = g.Purpose,
                CreatedAt = g.CreatedAt,
                ExpiresAt = g.ExpiresAt,
                AcceptedAt = g.AcceptedAt,
                RevokedAt = g.RevokedAt,
                RevocationReason = g.RevocationReason
            };
        }).ToList();
    }

    public async Task<IActionResult> OnPostRevokeGrantAsync(Guid grantId, string reason)
    {
        var userId = ResolveUserAccountId(User);

        // Validate reason
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "Revocation reason is required.";
            return RedirectToPage();
        }

        try
        {
            await grantService.RevokeGrantAsync(grantId, userId, reason)
                .ConfigureAwait(false);

            TempData["Success"] = "Grant revoked successfully.";
            return RedirectToPage();
        }
        catch (NotFoundError)
        {
            TempData["Error"] = "Grant not found or already revoked.";
            return RedirectToPage();
        }
        catch (AuthorizationError)
        {
            TempData["Error"] = "You do not have permission to revoke this grant.";
            return RedirectToPage();
        }
        catch (ConflictError)
        {
            TempData["Error"] = "Grant is in a terminal state and cannot be revoked.";
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
        public Guid DelegateId { get; set; }
        public string DelegateName { get; set; } = string.Empty;
        public DelegatedAccessGrantStatus Status { get; set; }
        public List<string> Capabilities { get; set; } = new();
        public List<DelegableCapabilityDefinition?> CapabilityDefinitions { get; set; } = new();
        public string Purpose { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset? AcceptedAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public string? RevocationReason { get; set; }
    }
}
