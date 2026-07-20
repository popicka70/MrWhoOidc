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
/// Guided form for creating a new delegated access grant.
/// GET renders the form with available delegates, capabilities, and resource constraints.
/// POST validates inputs and creates the grant via IDelegatedAccessGrantService.
/// </summary>
[Authorize]
public class CreateModel(
    AuthDbContext db,
    IDelegatedAccessGrantService grantService,
    IDelegableCapabilityCatalog capabilityCatalog,
    IUserAccountService userAccountService,
    IUserTenantMembershipService membershipService,
    IMfaService mfaService,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions) : PageModel
{
    public Guid? SelectedDelegateId { get; set; }
    public string? SelectedDelegateName { get; set; }
    public List<CapabilityOption> AvailableCapabilities { get; set; } = new();
    public List<DelegateCandidate> EligibleDelegates { get; set; } = new();
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

        var userId = ResolveUserAccountId(User);

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
                AllowedResourceTypes = d.AllowedResourceTypes.OrderBy(x => x)
            }).ToList();

        // Find eligible delegates: active members in current tenant, not self
        var currentTenant = db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == TenantAccessor.CurrentTenantId);
        var activeMemberships = await db.UserTenantMemberships
            .AsNoTracking()
            .Where(m => m.TenantId == currentTenant?.Id && m.Status == TenantMembershipStatus.Active && m.UserAccountId != userId)
            .ToListAsync();

        EligibleDelegates = activeMemberships.Select(m =>
        {
            var user = await userAccountService.FindByAccountIdAsync(m.UserAccountId);
            return new DelegateCandidate
            {
                Id = m.UserAccountId,
                Name = user?.Name ?? user?.Username ?? "Unknown",
                Username = user?.Username ?? string.Empty
            };
        }).ToList();

        // Check if any selected capability requires MFA step-up
        RequiresMfaStepUp = false; // Will be populated on POST
    }

    public async Task<IActionResult> OnPostCreateGrantAsync(
            Guid delegateId,
            List<string> capabilities,
            string purpose,
            int expiryDays,
            string? resourceConstraintsJson = null)
    {
        var userId = ResolveUserAccountId(User);
        var tenantId = TenantAccessor.CurrentTenantId;

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

        if (expiryDays <= 0 || expiryDays > 30)
        {
            TempData["Error"] = "Expiry must be between 1 and 30 days.";
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
                TempData["Error"] = "Capability '${cap}' is not delegable.";
                return RedirectToPage();
            }
        }

        // Check MFA step-up requirements
        RequiresMfaStepUp = capabilities.Any(c => capabilityCatalog.GetDefinition(c)?.RequiresStepUp ?? false);
        if (RequiresMfaStepUp)
        {
            var mfaResult = await mfaService.IsMfaRequiredAsync(userId, tenantId)
                .ConfigureAwait(false);

            if (mfaResult)
            {
                // Persist pending grant state in TempData so it survives the MFA redirect
                TempData["PendingDelegateId"] = delegateId.ToString();
                TempData["PendingCapabilities"] = capabilities.ToString();
                TempData["PendingPurpose"] = purpose;
                TempData["PendingExpiryDays"] = expiryDays.ToString();
                TempData["PendingResourceConstraints"] = resourceConstraintsJson ?? string.Empty;

                // Redirect to MFA challenge page with return URL.
                // The Mfa/IndexModel accepts Required (SupportsGet) and ReturnUrl (SupportsGet)
                // so the user can complete step-up and come back to finish grant creation.
                // Use the base path; tenant prefix resolution happens on the GET page
                // when the user returns after successful MFA verification.
                var returnUrl = "/account/delegated-access/create";
                return RedirectToPage("/Mfa", new { Required = true, ReturnUrl = returnUrl });
            }
        }

        // Calculate expiry from now
        var expiresAt = DateTimeOffset.UtcNow.AddDays(expiryDays);

        // Create the grant
        try
        {
            var grant = await grantService.CreateGrantAsync(
                tenantId, userId, delegateId,
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
    }

    public async Task<IActionResult> OnPostSelectDelegateAsync(Guid delegateId)
    {
        SelectedDelegateId = delegateId;
        return RedirectToPage();
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
}
