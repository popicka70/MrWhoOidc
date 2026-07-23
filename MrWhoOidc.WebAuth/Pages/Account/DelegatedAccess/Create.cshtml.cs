using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.WebAuth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account.DelegatedAccess;

/// <summary>
/// Guided form for creating a new delegated access grant.
/// GET renders the form with available delegates, capabilities, and resource constraints.
/// POST validates inputs and creates the grant via IDelegatedAccessGrantService.
/// </summary>
[Authorize]
public class CreateModel(
    AuthDbContext db,
    IDelegatedAccessGrantService grantService,
    IDelegableCapabilityCatalog capabilityCatalog,
    ITenantAccessor tenantAccessor,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions) : PageModel
{
    public Guid? SelectedDelegateId { get; set; }
    public string? SelectedDelegateName { get; set; }
    public List<CapabilityOption> AvailableCapabilities { get; set; } = new();
    public List<DelegateCandidate> EligibleDelegates { get; set; } = new();
    public List<ClientOption> AvailableClients { get; set; } = new();
    public string? Message { get; set; }
    public bool RequiresMfaStepUp { get; set; }

    public async Task OnGetAsync()
    {
        // Check feature flag: Delegated Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableDelegatedAccess)
        {
            TempData["Error"] = "Feature Disabled: Delegated Access is not enabled.";
            return;
        }

        // Build list of delegable capabilities from catalog
        var allDefinitions = capabilityCatalog.AllDefinitions();
        AvailableCapabilities = allDefinitions.Values
            .Where(d => d.IsDelegable)
             .OrderBy(d => d.Name)
             .Select(d => new CapabilityOption
             {
                 Name = d.Name,
                 DisplayName = d.DisplayName,
                 Description = d.Description,
                 MaximumGrantLifetime = d.MaximumGrantLifetime,
                 AllowedResourceTypes = d.AllowedResourceTypes.ToList()
             }).ToList();

        // Find eligible delegates: active members in current tenant, not self
        var userIdClaim = HttpContext.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid currentUserId;
        if (!string.IsNullOrWhiteSpace(userIdClaim) && Guid.TryParse(userIdClaim, out var parsedId))
        {
            currentUserId = parsedId;
        }
        else
        {
            throw new AuthorizationError("Cannot resolve user account ID from claims.");
        }

        // Get current tenant from user's memberships or default tenant
        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        var currentUserMembership = await db.UserTenantMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserAccountId == currentUserId
                && m.Status == TenantMembershipStatus.Active
                && (currentTenantId == null || m.TenantId == currentTenantId));

        currentTenantId = currentUserMembership?.TenantId;

        if (currentTenantId is null)
        {
            Message = "You do not have an active membership in any tenant.";
            return;
        }

        AvailableClients = await db.Clients.AsNoTracking()
            .Where(client => client.TenantId == currentTenantId.Value)
            .OrderBy(client => client.ClientName ?? client.ClientId)
            .Select(client => new ClientOption
            {
                Id = client.Id,
                ClientId = client.ClientId,
                Name = client.ClientName ?? client.ClientId
            })
            .ToListAsync();

        var activeMemberships = await db.UserTenantMemberships
            .AsNoTracking()
            .Where(m => m.TenantId == currentTenantId.Value
                && m.Status == TenantMembershipStatus.Active
                && (m.ExpiresAt == null || m.ExpiresAt > DateTimeOffset.UtcNow)
                && m.UserAccountId != currentUserId)
            .ToListAsync();

        EligibleDelegates = new List<DelegateCandidate>();
        foreach (var membership in activeMemberships)
        {
            var user = await db.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == membership.UserAccountId);

            EligibleDelegates.Add(new DelegateCandidate
            {
                Id = membership.UserAccountId,
                Name = user?.Name ?? user?.Username ?? "Unknown",
                Username = user?.Username ?? string.Empty
            });
        }

        // Check if any selected capability requires MFA step-up
        RequiresMfaStepUp = false;
    }

    public async Task<IActionResult> OnPostCreateGrantAsync(
            Guid clientId,
            Guid delegateId,
            List<string> capabilities,
            string purpose,
            int expiryDays)
    {
        var userId = ResolveUserAccountId();

        // Get current tenant from user's memberships
        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        var currentUserMembership = await db.UserTenantMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserAccountId == userId
                && m.Status == TenantMembershipStatus.Active
                && (m.ExpiresAt == null || m.ExpiresAt > DateTimeOffset.UtcNow)
                && (currentTenantId == null || m.TenantId == currentTenantId));

        if (currentUserMembership is null)
        {
            TempData["Error"] = "You do not have an active membership in any tenant.";
            return RedirectToPage();
        }

        var tenantId = currentUserMembership.TenantId;

        // Validate inputs
        if (string.IsNullOrWhiteSpace(purpose))
        {
            TempData["Error"] = "Purpose is required.";
            return RedirectToPage();
        }

        if (capabilities.Count == 0)
        {
            TempData["Error"] = "At least one capability must be selected.";
            return RedirectToPage();
        }

        if (expiryDays <= 0 || expiryDays > 7)
        {
            TempData["Error"] = "Expiry must be between 1 and 7 days.";
            return RedirectToPage();
        }

        // Validate delegate is not self
        if (delegateId == userId)
        {
            TempData["Error"] = "You cannot delegate to yourself.";
            return RedirectToPage();
        }

        // Validate all capabilities are delegable
        foreach (var cap in capabilities)
        {
            if (!capabilityCatalog.IsDelegable(cap))
            {
                TempData["Error"] = $"Capability '{cap}' is not delegable.";
                return RedirectToPage();
            }
        }

        // Check MFA step-up requirements (stubbed - MFA service not available)
        RequiresMfaStepUp = capabilities.Any(c => capabilityCatalog.GetDefinition(c)?.RequiresStepUp ?? false);
        // Note: MFA step-up redirect logic is currently disabled as IMfaService is not available.

        // Calculate expiry from now
        var expiresAt = DateTimeOffset.UtcNow.AddDays(expiryDays);

        // Create the grant
        try
        {
            var grant = await grantService.CreateGrantAsync(
                tenantId, clientId, userId, delegateId,
                capabilities, purpose, expiresAt)
                .ConfigureAwait(false);

            TempData["Success"] = "Grant created successfully. An invitation has been sent to the delegate.";
            return RedirectToPage();
        }
        catch (ArgumentError e)
        {
            TempData["Error"] = e.Message;
            return RedirectToPage();
        }
        catch (NotFoundError e)
        {
            TempData["Error"] = e.Message;
            return RedirectToPage();
        }
        catch (MembershipError e)
        {
            TempData["Error"] = e.Message;
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

    public class CapabilityOption
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public TimeSpan MaximumGrantLifetime { get; set; }
        public List<string> AllowedResourceTypes { get; set; } = new();
    }

    public class DelegateCandidate
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
    }

    public class ClientOption
    {
        public Guid Id { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
