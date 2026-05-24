# Research: External IdP Registration

**Feature**: 013-external-idp-registration  
**Date**: 2025-12-25  
**Status**: Complete

## Research Summary

This document consolidates findings from technical research needed to implement external IdP registration on the user registration page.

---

## R1: Existing External Authentication Flow

### Decision

Reuse the existing `ExternalOidcHandler` flow with a registration-specific mode indicator.

### Rationale

- The external OIDC authentication flow is already implemented and battle-tested
- `ExternalOidcHandler.StartAsync()` accepts `returnUrl` parameter that can encode registration context
- `ExternalOidcUserProvisioner.ProvisionOrLinkUserAsync()` already creates users from external IdP claims
- Adding a query parameter (e.g., `mode=register`) to the flow allows the callback to distinguish registration from login

### Alternatives Considered

| Alternative | Rejected Because |
|-------------|------------------|
| New separate registration endpoint | Duplicates existing OIDC flow logic; maintenance burden |
| Cookie-based mode tracking | Vulnerable to state loss; less reliable than URL parameter |
| Separate Razor Page for IdP registration | Unnecessary complexity; existing flow handles user creation |

### Key Findings

From `ExternalOidcHandler.cs`:

```csharp
// Start flow captures returnUrl which can include mode parameter
var returnUrl = http.Request.Query["returnUrl"].ToString();

// StateModel preserves context through external IdP redirect
var stateModel = new StateModel
{
    Provider = providerName,
    ReturnUrl = returnUrl,  // Can include ?mode=register
    // ...
};
```

From `ExternalOidcUserProvisioner.cs`:

```csharp
// Already provisions new users when external identity not found
// Uses RegistrationService with isExternalIdp=true
var userId = await registrationService.CreateAndMaybeApproveRegistrationAsync(
    email: userEmail,
    isExternalIdp: true,
    autoApprove: shouldAutoApprove,
    // ...
);
```

---

## R2: IdentityProvider Entity Extension

### Decision

Add `AllowRegistration` boolean property to `IdentityProvider` entity with default value `false`.

### Rationale

- Simple boolean flag aligns with existing `Enabled` property pattern
- Default `false` ensures existing IdPs don't suddenly appear on registration page
- Can be toggled independently of `Enabled` (an IdP can be enabled for login but not registration)
- EF Core migration adds nullable column with default, making it backward-compatible

### Schema Change

```csharp
public class IdentityProvider
{
    // Existing properties...
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; } = false;
    
    // NEW: Controls visibility on registration page
    public bool AllowRegistration { get; set; } = false;
}
```

### Migration Strategy

- Use EF Core `dotnet ef migrations add AddAllowRegistrationToIdentityProvider`
- Column: `allow_registration BOOLEAN NOT NULL DEFAULT FALSE`
- No data migration needed—all existing IdPs default to not showing on registration

---

## R3: Registration Page IdP Query

### Decision

Query IdPs in the default tenant that have both `Enabled=true` AND `AllowRegistration=true`, ordered by `SortOrder`.

### Rationale

- Registration page serves the default tenant (per existing implementation)
- Only enabled IdPs should be shown (disabled IdPs are turned off entirely)
- `AllowRegistration` provides additional granular control
- Existing `SortOrder` property determines display order

### Query Pattern

```csharp
// In Index.cshtml.cs OnGet
var defaultTenantId = await db.Tenants
    .Where(t => t.IsDefault)
    .Select(t => t.Id)
    .FirstOrDefaultAsync();

var registrationIdps = await db.IdentityProviders
    .AsNoTracking()
    .Where(p => p.TenantId == defaultTenantId 
             && p.Enabled 
             && p.AllowRegistration)
    .OrderBy(p => p.SortOrder)
    .Select(p => new { p.Name, p.DisplayName, p.LogoUrl })
    .ToListAsync();
```

---

## R4: Duplicate Email Handling

### Decision

Leverage existing duplicate detection in `RegistrationService.CreateAndMaybeApproveRegistrationAsync()`.

### Rationale

- `RegistrationService` already checks for existing users by normalized email
- Throws `InvalidOperationException` with message "A user with this email already exists."
- Registration page can catch this and show appropriate message with login link

### Existing Code

```csharp
// In RegistrationService.cs
var userExists = await _db.Users.AsNoTracking()
    .AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken);
if (userExists)
{
    throw new InvalidOperationException("A user with this email already exists.");
}
```

### UI Handling

- Catch the exception in the callback handling
- Display: "An account with this email already exists. Would you like to sign in instead?"
- Provide link to login page with email pre-filled

---

## R5: Tenant Creation with External IdP

### Decision

Support tenant creation after successful external IdP authentication by passing tenant parameters through the flow.

### Rationale

- Current registration form has "Create new tenant" option
- Users registering via IdP should have the same capability
- Can be implemented by showing tenant creation form after IdP callback (two-step flow)

### Implementation Approach

1. User clicks external IdP button on registration page
2. After successful IdP authentication, redirect back to registration page with:
   - `mode=register_idp_success`
   - IdP claims stored in session/temp data
3. Show tenant creation form if user wants to create tenant
4. Complete registration with stored claims + tenant info

### Current Behavior

- External IdP registration can create a new tenant when the registration flow asks for one.
- Invitation links and verified `AutoJoin` tenant domain claims can target a specific tenant.
- Platform external login stays separate from tenant membership provisioning.

**Status update (2026-05-23)**: The earlier default-tenant-only simplification has been superseded by invitation and domain-claim enrollment flows.

---

## R6: Admin UI for AllowRegistration

### Decision

Add checkbox toggle to existing Provider Edit page (`/Admin/Providers/Edit`).

### Rationale

- Follows existing pattern for `Enabled` and `IsDefault` toggles
- Administrators already configure IdPs here
- No new admin pages needed

### UI Location

In `Edit.cshtml`, add after existing toggles:

```html
<div class="form-check mb-3">
    <input asp-for="Input.AllowRegistration" class="form-check-input" />
    <label asp-for="Input.AllowRegistration" class="form-check-label">
        Allow Registration
    </label>
    <small class="form-text text-muted d-block">
        When enabled, this provider will appear on the public registration page.
    </small>
</div>
```

---

## R7: External IdP Flow Return URL Construction

### Decision

Construct registration-specific start URL with mode indicator and registration return URL.

### Rationale

- Need to distinguish registration flow from login flow in callback
- Return URL should point back to registration success page or login page
- Must preserve any original `returnUrl` for post-registration redirect

### URL Pattern

```text
/Auth/External/Start
  ?provider={idpName}
  &clientId={defaultClientId}
  &returnUrl=/Registrations?mode=idp_callback&originalReturn={encodedOriginalReturnUrl}
```

### Flow

1. Registration page renders IdP button with constructed URL
2. External flow completes, returns to `/Registrations?mode=idp_callback&...`
3. Registration page detects `mode=idp_callback`, shows success message
4. If `originalReturn` present, offers "Continue to application" link

---

## Unresolved Items

*None—all technical questions resolved.*

---

## References

- [ExternalOidcHandler.cs](../../MrWhoOidc.WebAuth/Handlers/ExternalOidcHandler.cs) - External OIDC flow
- [ExternalOidcUserProvisioner.cs](../../MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs) - User provisioning
- [RegistrationService.cs](../../MrWhoOidc.WebAuth/Services/RegistrationService.cs) - Registration workflow
- [Index.cshtml](../../MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml) - Current registration page
- [AuthDbContext.cs](../../MrWhoOidc.Auth/Persistence/AuthDbContext.cs) - IdentityProvider entity
