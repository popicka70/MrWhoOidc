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
/// Main landing page for delegated access grant management.
/// Provides an overview of all grants and the user's delegated context status.
/// </summary>
[Authorize]
public class IndexModel(
    AuthDbContext db,
    IDelegatedAccessGrantService grantService,
    IDelegableCapabilityCatalog capabilityCatalog,
    IDelegatedAccessContextService contextService,
    IUserAccountService userAccountService,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions) : PageModel
{
    public List<GrantSummary> GrantedByMe { get; set; } = new();
    public List<GrantSummary> DelegatedToMe { get; set; } = new();
    public MrWhoOidc.WebAuth.Services.DelegatedAccessContextInfo? ActiveContext { get; set; }
    public string? Message { get; set; }

    public async Task OnGetAsync()
    {
        // Check feature flag: Delegated Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableDelegatedAccess)
        {
            TempData["Error"] = "Feature Disabled: Delegated Access is not enabled.";
            return;
        }

        var userId = ResolveUserAccountId(User);

        // Load all grants where user is delegator
        var grantsAsDelegator = await db.DelegatedAccessGrants
            .AsNoTracking()
            .Where(g => g.DelegatorUserAccountId == userId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();

        GrantedByMe = grantsAsDelegator.Select(g => new GrantSummary
        {
            Id = g.Id,
            TenantId = g.TenantId,
            DelegateId = g.DelegateUserAccountId,
            Status = g.Status,
            Capabilities = ParseCapabilities(g.CapabilitiesJson),
            Purpose = g.Purpose,
            CreatedAt = g.CreatedAt,
            ExpiresAt = g.ExpiresAt,
            AcceptedAt = g.AcceptedAt
        }).ToList();

        // Load all grants where user is delegate
        var grantsAsDelegate = await db.DelegatedAccessGrants
            .AsNoTracking()
            .Where(g => g.DelegateUserAccountId == userId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();

        DelegatedToMe = grantsAsDelegate.Select(g => new GrantSummary
        {
            Id = g.Id,
            TenantId = g.TenantId,
            DelegatorId = g.DelegatorUserAccountId,
            Status = g.Status,
            Capabilities = ParseCapabilities(g.CapabilitiesJson),
            Purpose = g.Purpose,
            CreatedAt = g.CreatedAt,
            ExpiresAt = g.ExpiresAt,
            AcceptedAt = g.AcceptedAt,
            DeclinedAt = g.DeclinedAt
        }).ToList();

        // Check for active delegated context
        ActiveContext = await contextService.GetActiveContextAsync(HttpContext);
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

    public class GrantSummary
    {
        public Guid Id { get; set; }
        public Guid? TenantId { get; set; }
        public Guid? DelegatorId { get; set; }
        public Guid? DelegateId { get; set; }
        public DelegatedAccessGrantStatus Status { get; set; }
        public List<string> Capabilities { get; set; } = new();
        public string Purpose { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset? AcceptedAt { get; set; }
        public DateTimeOffset? DeclinedAt { get; set; }
    }

}
