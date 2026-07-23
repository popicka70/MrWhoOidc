using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account.DelegatedAccess;

/// <summary>
/// Invitation review page for the delegate to see exactly what is being granted
/// before explicitly accepting or declining.
/// GET renders the grant details for review.
/// POST handles accept or decline actions.
/// </summary>
[Authorize]
public class InvitationsModel(
    AuthDbContext db,
    IDelegatedAccessGrantService grantService,
    IDelegableCapabilityCatalog capabilityCatalog,
    IUserAccountService userAccountService,
    IUserTenantMembershipService membershipService,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions) : PageModel
{
    public string Token { get; set; } = string.Empty;
    public GrantDetail? GrantDetails { get; set; }
    public string? Message { get; set; }

    public async Task OnGetAsync(string token)
    {
        Token = token;

        // Check feature flag: Delegated Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableDelegatedAccess)
        {
            Message = "Feature Disabled: Delegated Access is not enabled.";
            return;
        }

        var userId = ResolveUserAccountId(User);

        // Resolve the invitation token to find the grant
        var tokenHash = CryptoHelper.ComputeSha256Base64(token);
        var invitationToken = await db.DelegatedAccessInvitationTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash)
            .ConfigureAwait(false);

        if (invitationToken is null)
        {
            Message = "Invitation not found. The token may be invalid or expired.";
            return;
        }
        if (invitationToken.ConsumedAt is not null
            || invitationToken.RevokedAt is not null
            || invitationToken.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Message = "Invitation not found. The token may be invalid or expired.";
            return;
        }

        // Load the associated grant
        var grant = await db.DelegatedAccessGrants
            .AsNoTracking()
            .Where(g => g.Id == invitationToken.GrantId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (grant is null)
        {
            Message = "Grant associated with this invitation no longer exists.";
            return;
        }
        if (grant.DelegateUserAccountId != userId)
        {
            Message = "Invitation not found. The token may be invalid or expired.";
            return;
        }

        // Resolve names for display
        var delegatorUser = await userAccountService.GetByIdAsync(grant.DelegatorUserAccountId);
        var delegateUser = await userAccountService.GetByIdAsync(grant.DelegateUserAccountId);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == grant.TenantId);
        var client = grant.ClientId.HasValue
            ? await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == grant.ClientId.Value && c.TenantId == grant.TenantId)
            : null;
        if (client is null)
        {
            Message = "This legacy grant has no valid client binding and cannot be accepted.";
            return;
        }

        GrantDetails = new GrantDetail
        {
            Id = grant.Id,
            TenantId = grant.TenantId,
            TenantName = tenant?.Name ?? "Unknown Tenant",
            TenantSlug = tenant?.Slug ?? string.Empty,
            ClientId = client.Id,
            OidcClientId = client.ClientId,
            ClientName = client.ClientName ?? client.ClientId,
            DelegatorId = grant.DelegatorUserAccountId,
            DelegatorName = delegatorUser?.Name ?? delegatorUser?.Username ?? "Unknown",
            DelegateId = grant.DelegateUserAccountId,
            DelegateName = delegateUser?.Name ?? delegateUser?.Username ?? "Unknown",
            Status = grant.Status,
            Capabilities = ParseCapabilities(grant.CapabilitiesJson),
            Purpose = grant.Purpose,
            CreatedAt = grant.CreatedAt,
            ExpiresAt = grant.ExpiresAt,
            AcceptanceExpiresAt = grant.AcceptanceExpiresAt,
            IsExpired = grant.AcceptanceExpiresAt < DateTimeOffset.UtcNow,
            IsConsumed = invitationToken.ConsumedAt is not null,
            IsRevoked = invitationToken.RevokedAt is not null
        };
    }

    public async Task<IActionResult> OnPostAcceptGrantAsync(string token)
    {
        var userId = ResolveUserAccountId(User);

        try
        {
            var grant = await grantService.AcceptGrantAsync(token, userId)
                .ConfigureAwait(false);

            TempData["Success"] = "Grant accepted. You can now activate it to act on behalf of the delegator.";
            return RedirectToPage();
        }
        catch (NotFoundError)
        {
            TempData["Error"] = "Invitation not found or already consumed.";
            return RedirectToPage();
        }
        catch (ConflictError)
        {
            TempData["Error"] = "Grant is already accepted or in a terminal state.";
            return RedirectToPage();
        }
        catch (ExpiredError)
        {
            TempData["Error"] = "Invitation has expired.";
            return RedirectToPage();
        }
        catch (MismatchError)
        {
            TempData["Error"] = "You are not the intended delegate for this grant.";
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostDeclineGrantAsync(string token)
    {
        var userId = ResolveUserAccountId(User);

        try
        {
            var grant = await grantService.DeclineGrantAsync(token, userId)
                .ConfigureAwait(false);

            TempData["Success"] = "Grant declined. No further action needed.";
            return RedirectToPage();
        }
        catch (NotFoundError)
        {
            TempData["Error"] = "Invitation not found or already consumed.";
            return RedirectToPage();
        }
        catch (ConflictError)
        {
            TempData["Error"] = "Grant is already accepted or declined.";
            return RedirectToPage();
        }
        catch (ExpiredError)
        {
            TempData["Error"] = "Invitation has expired.";
            return RedirectToPage();
        }
        catch (MismatchError)
        {
            TempData["Error"] = "You are not the intended delegate for this grant.";
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
        public Guid ClientId { get; set; }
        public string OidcClientId { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
        public Guid DelegatorId { get; set; }
        public string DelegatorName { get; set; } = string.Empty;
        public Guid DelegateId { get; set; }
        public string DelegateName { get; set; } = string.Empty;
        public DelegatedAccessGrantStatus Status { get; set; }
        public List<string> Capabilities { get; set; } = new();
        public string Purpose { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset AcceptanceExpiresAt { get; set; }
        public bool IsExpired { get; set; }
        public bool IsConsumed { get; set; }
        public bool IsRevoked { get; set; }
    }
}
