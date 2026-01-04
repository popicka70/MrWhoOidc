# Phase 4: User Self-Service Portal Implementation

**Date:** October 6, 2025  
**Branch:** master  
**Status:** 🚧 In Progress

## Executive Summary

Phase 4 introduces a unified **User Self-Service Portal** at `/Account/*` routes, providing authenticated users with comprehensive self-management capabilities without requiring admin privileges.

### Scope

**New Features:**
- ✅ Unified account portal dashboard (`/Account/Index`)
- ✅ Profile management (`/Account/Profile`)
- ✅ Password management (move from `/Password` → `/Account/Password`)
- ✅ MFA/TOTP management (move from `/Mfa` → `/Account/Security`)
- ✅ Active sessions viewer (new)
- ✅ Consent/authorization history (new)
- ✅ Linked external identities (new)
- ✅ Alternative emails management (new)

**Authorization:**
- Simple `[Authorize]` - no admin role required
- All authenticated users can access their own account portal
- Tenant-aware using `ITenantAccessor`

## Architecture

### Route Structure

```
/Account/
├── Index                    # Dashboard (overview)
├── Profile                  # Name, email, username
├── Password                 # Change password
├── Security                 # MFA/TOTP management
├── Sessions                 # Active sessions + revocation
├── Consents                 # Authorized applications
├── LinkedAccounts           # External identity links
└── Emails                   # Alternative emails
```

### Authorization Model

```csharp
[Authorize] // No admin role required
public class IndexModel : PageModel
{
    // Only access own data
    private async Task<User?> GetCurrentUserAsync()
    {
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId)) return null;
        
        return await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId);
    }
}
```

### UI Navigation

**Sidebar Section:**
```cshtml
<div class="list-group-item fw-semibold text-uppercase small">My Account</div>
<a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Index">
    <i class="bi bi-person-circle me-2"></i>Account
</a>
<a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Profile">
    <i class="bi bi-person me-2"></i>Profile
</a>
<a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Password">
    <i class="bi bi-key me-2"></i>Password
</a>
<a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Security">
    <i class="bi bi-shield-lock me-2"></i>Security (MFA)
</a>
<a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Sessions">
    <i class="bi bi-phone me-2"></i>Sessions
</a>
<a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Consents">
    <i class="bi bi-check2-square me-2"></i>Consents
</a>
<a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/LinkedAccounts">
    <i class="bi bi-link-45deg me-2"></i>Linked Accounts
</a>
<a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Emails">
    <i class="bi bi-envelope me-2"></i>Emails
</a>
```

## Implementation Plan

### Stage 1: Create Directory Structure & Shared Components ✅

**Tasks:**
1. Create `/Pages/Account/` directory
2. Create shared partial for account navigation tabs
3. Create base page model if needed

**Files:**
- `Pages/Account/_AccountTabs.cshtml` - Navigation tabs
- `Pages/Account/_AccountLayout.cshtml` (optional) - Shared layout

### Stage 2: Dashboard (Index) ✅

**Route:** `/Account/Index` or `/Account`

**Features:**
- Overview cards with quick stats
- Recent activity summary
- Quick links to all sections

**Data Displayed:**
- Profile summary (name, email, username)
- MFA status badge
- Active sessions count
- Active consents count
- Linked accounts count
- Alternative emails count

**UI Pattern:**
```cshtml
<div class="row g-3">
    <div class="col-md-6 col-lg-4">
        <div class="card">
            <div class="card-body">
                <h5 class="card-title"><i class="bi bi-shield-lock text-success"></i> Security</h5>
                <p class="card-text">MFA: <strong>@(Model.MfaEnabled ? "Enabled" : "Disabled")</strong></p>
                <a href="@(tenantPrefix)/Account/Security" class="btn btn-sm btn-outline-primary">Manage</a>
            </div>
        </div>
    </div>
    <!-- More cards... -->
</div>
```

### Stage 3: Profile Management ✅

**Route:** `/Account/Profile`

**Features:**
- View/edit name
- View/edit email (with verification)
- Display username (read-only)
- Display tenant info
- Display created date

**Form Fields:**
```csharp
public class ProfileInput
{
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;
    
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; set; } = string.Empty;
}
```

**Business Logic:**
- Validate email format
- Check for duplicate email in same tenant
- If email changes, set EmailVerified = false
- Update NormalizedEmail = Email.ToUpperInvariant()

### Stage 4: Password Management ✅

**Route:** `/Account/Password`

**Action:** Move existing `/Password/Index` → `/Account/Password`

**Changes:**
- Update route to `/Account/Password`
- Keep all existing logic (already works)
- Update navigation links
- Add tenant-aware redirect

**Already Implemented:**
- Current password verification
- New password validation
- Password strength requirements
- Argon2id hashing

### Stage 5: Security/MFA Management ✅

**Route:** `/Account/Security`

**Action:** Move existing `/Mfa/Index` → `/Account/Security`

**Changes:**
- Rename from "MFA" to "Security" (broader scope)
- Update route to `/Account/Security`
- Keep all existing TOTP logic
- Update navigation links
- Add tenant-aware redirect

**Already Implemented:**
- TOTP QR code generation
- Secret generation
- Code verification
- Enable/disable flow

### Stage 6: Sessions Management (NEW) 🆕

**Route:** `/Account/Sessions`

**Features:**
- List active authentication sessions
- Show: Browser, OS, IP, Last Active, Created
- Revoke individual sessions
- "Revoke All Other Sessions" button

**Data Model (NEW):**
```csharp
// Option 1: Use existing Token table
var sessions = await _db.Tokens.AsNoTracking()
    .Where(t => t.UserId == userId && t.RevokedAt == null)
    .Where(t => t.ExpiresAt > DateTimeOffset.UtcNow)
    .OrderByDescending(t => t.IssuedAt)
    .ToListAsync();

// Option 2: Create new UserSession table (future enhancement)
public class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActiveAt { get; set; }
}
```

**UI:**
```cshtml
<div class="list-group">
    @foreach (var session in Model.Sessions)
    {
        <div class="list-group-item d-flex justify-content-between align-items-center">
            <div>
                <h6 class="mb-1">
                    <i class="bi bi-@(session.IsCurrent ? "check-circle-fill text-success" : "circle")"></i>
                    @session.Browser (@session.OS)
                </h6>
                <small class="text-muted">
                    IP: @session.IpAddress | Last active: @session.LastActiveAt.Humanize()
                </small>
            </div>
            @if (!session.IsCurrent)
            {
                <button class="btn btn-sm btn-outline-danger" onclick="revokeSession('@session.Id')">
                    Revoke
                </button>
            }
        </div>
    }
</div>
```

### Stage 7: Consents Management (NEW) 🆕

**Route:** `/Account/Consents`

**Features:**
- List all granted consents (authorized apps)
- Show: Client name, scopes, granted date
- Revoke individual consents
- Filter: Active / Revoked

**Query:**
```csharp
var consents = await _db.Consents.AsNoTracking()
    .Where(c => c.UserId == userId)
    .Where(c => c.TenantId == tenantId) // Tenant-scoped
    .Join(_db.Clients, c => c.ClientId, cl => cl.ClientId, (c, cl) => new
    {
        Consent = c,
        ClientName = cl.ClientName
    })
    .OrderByDescending(x => x.Consent.CreatedAt)
    .Select(x => new ConsentViewModel
    {
        Id = x.Consent.Id,
        ClientName = x.ClientName,
        Scopes = JsonSerializer.Deserialize<string[]>(x.Consent.ScopesJson) ?? Array.Empty<string>(),
        GrantedAt = x.Consent.CreatedAt,
        IsRevoked = x.Consent.RevokedAt != null
    })
    .ToListAsync();
```

**Revocation Logic:**
```csharp
public async Task<IActionResult> OnPostRevokeAsync(Guid consentId)
{
    var userId = GetUserId();
    var consent = await _db.Consents.FirstOrDefaultAsync(c => c.Id == consentId && c.UserId == userId);
    
    if (consent == null) return NotFound();
    
    consent.RevokedAt = DateTimeOffset.UtcNow;
    await _db.SaveChangesAsync();
    
    return RedirectToPage();
}
```

### Stage 8: Linked Accounts Management (NEW) 🆕

**Route:** `/Account/LinkedAccounts`

**Features:**
- List all linked external identities
- Show: Provider, external ID, linked date
- Unlink account (if password set or other links exist)
- Link new provider (redirect to external auth flow)

**Query:**
```csharp
var linkedAccounts = await _db.ExternalIdentities.AsNoTracking()
    .Where(e => e.UserId == userId)
    .Join(_db.IdentityProviders, e => e.ProviderName, p => p.Name, (e, p) => new
    {
        ExternalIdentity = e,
        Provider = p
    })
    .OrderBy(x => x.Provider.DisplayName)
    .Select(x => new LinkedAccountViewModel
    {
        Id = x.ExternalIdentity.Id,
        ProviderName = x.Provider.DisplayName,
        Issuer = x.ExternalIdentity.Issuer,
        Subject = x.ExternalIdentity.Subject,
        LinkedAt = x.ExternalIdentity.CreatedAt
    })
    .ToListAsync();
```

**Unlink Logic:**
```csharp
public async Task<IActionResult> OnPostUnlinkAsync(Guid linkId)
{
    var userId = GetUserId();
    var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
    
    // Check if user has password or other linked accounts
    var linkedCount = await _db.ExternalIdentities.CountAsync(e => e.UserId == userId);
    var hasPassword = !string.IsNullOrEmpty(user?.PasswordHash);
    
    if (!hasPassword && linkedCount <= 1)
    {
        ModelState.AddModelError("", "Cannot unlink last authentication method. Set a password first.");
        return Page();
    }
    
    var link = await _db.ExternalIdentities.FirstOrDefaultAsync(e => e.Id == linkId && e.UserId == userId);
    if (link != null)
    {
        _db.ExternalIdentities.Remove(link);
        await _db.SaveChangesAsync();
    }
    
    return RedirectToPage();
}
```

### Stage 9: Alternative Emails Management (NEW) 🆕

**Route:** `/Account/Emails`

**Features:**
- List all alternative emails
- Add new alternative email
- Remove alternative email
- Send verification email (future)
- Mark as verified (admin only, or via email link)

**Query:**
```csharp
var emails = await _db.UserAlternativeEmails.AsNoTracking()
    .Where(e => e.UserId == userId)
    .OrderBy(e => e.Email)
    .ToListAsync();
```

**Add Email Logic:**
```csharp
public async Task<IActionResult> OnPostAddAsync()
{
    var userId = GetUserId();
    
    // Check for duplicates
    var exists = await _db.UserAlternativeEmails
        .AnyAsync(e => e.UserId == userId && e.NormalizedEmail == Input.Email.ToUpperInvariant());
    
    if (exists)
    {
        ModelState.AddModelError("Input.Email", "This email is already added.");
        return Page();
    }
    
    var altEmail = new UserAlternativeEmail
    {
        UserId = userId,
        Email = Input.Email,
        NormalizedEmail = Input.Email.ToUpperInvariant(),
        Verified = false // Require verification
    };
    
    _db.UserAlternativeEmails.Add(altEmail);
    await _db.SaveChangesAsync();
    
    // TODO: Send verification email
    
    return RedirectToPage();
}
```

## Navigation Updates

### Update Sidebar Navigation in `_Layout.cshtml`

**Remove from top nav:**
- `/Registrations/Index`
- `/Password/Index`
- `/Mfa/Index`

**Add new "My Account" section** (between Home and Admin):
```cshtml
@if (User?.Identity?.IsAuthenticated ?? false)
{
    <div class="list-group-item fw-semibold text-uppercase small">My Account</div>
    <a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account">
        <i class="bi bi-person-circle me-2"></i>Dashboard
    </a>
    <a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Profile">
        <i class="bi bi-person me-2"></i>Profile
    </a>
    <a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Password">
        <i class="bi bi-key me-2"></i>Password
    </a>
    <a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Security">
        <i class="bi bi-shield-lock me-2"></i>Security
    </a>
    <a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Sessions">
        <i class="bi bi-phone me-2"></i>Sessions
    </a>
    <a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Consents">
        <i class="bi bi-check2-square me-2"></i>Consents
    </a>
    <a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/LinkedAccounts">
        <i class="bi bi-link-45deg me-2"></i>Linked Accounts
    </a>
    <a class="list-group-item list-group-item-action" href="@(tenantPrefix)/Account/Emails">
        <i class="bi bi-envelope me-2"></i>Emails
    </a>
}
```

### Update Top Navbar (Mobile)

Keep minimal items in dropdown:
- Account Dashboard link
- Log out

## Shared Components

### Account Navigation Tabs (`_AccountTabs.cshtml`)

```cshtml
@inject MrWhoOidc.Auth.MultiTenancy.ITenantAccessor TenantAccessor
@inject Microsoft.Extensions.Options.IOptions<MrWhoOidc.Auth.MultiTenancy.MultiTenancyOptions> MultiTenancyOptions
@{
    var currentTenant = TenantAccessor.CurrentTenant;
    var tenantPrefix = currentTenant != null && MultiTenancyOptions.Value.Enabled 
        ? $"/t/{currentTenant.Slug}" 
        : "";
    var active = (string?)ViewData["ActiveAccountTab"] ?? string.Empty;
}
<ul class="nav nav-tabs mb-4">
    <li class="nav-item">
        <a class="nav-link @(active=="index"?"active":"")" href="@(tenantPrefix)/Account">Dashboard</a>
    </li>
    <li class="nav-item">
        <a class="nav-link @(active=="profile"?"active":"")" href="@(tenantPrefix)/Account/Profile">Profile</a>
    </li>
    <li class="nav-item">
        <a class="nav-link @(active=="password"?"active":"")" href="@(tenantPrefix)/Account/Password">Password</a>
    </li>
    <li class="nav-item">
        <a class="nav-link @(active=="security"?"active":"")" href="@(tenantPrefix)/Account/Security">Security</a>
    </li>
    <li class="nav-item">
        <a class="nav-link @(active=="sessions"?"active":"")" href="@(tenantPrefix)/Account/Sessions">Sessions</a>
    </li>
    <li class="nav-item">
        <a class="nav-link @(active=="consents"?"active":"")" href="@(tenantPrefix)/Account/Consents">Consents</a>
    </li>
    <li class="nav-item">
        <a class="nav-link @(active=="linked"?"active":"")" href="@(tenantPrefix)/Account/LinkedAccounts">Linked</a>
    </li>
    <li class="nav-item">
        <a class="nav-link @(active=="emails"?"active":"")" href="@(tenantPrefix)/Account/Emails">Emails</a>
    </li>
</ul>
```

## Testing Checklist

### Functional Tests
- [ ] All account pages load for authenticated users
- [ ] Profile update saves correctly
- [ ] Password change with current password validation
- [ ] MFA enable/disable flow works
- [ ] Sessions list shows active tokens
- [ ] Session revocation invalidates tokens
- [ ] Consents list shows granted apps
- [ ] Consent revocation works
- [ ] Linked accounts display correctly
- [ ] Unlink account validation (last auth method)
- [ ] Alternative emails add/remove

### Authorization Tests
- [ ] Unauthenticated users redirected to login
- [ ] Users can only see/edit their own data
- [ ] Tenant isolation (users see only their tenant's data)
- [ ] No admin role required for account pages

### UI/UX Tests
- [ ] Navigation tabs highlight active page
- [ ] Mobile responsiveness
- [ ] Success/error messages display
- [ ] Forms validate input
- [ ] Buttons/links have proper tenant prefix

### Security Tests
- [ ] Password validation enforced
- [ ] Cannot unlink last auth method without password
- [ ] Consent revocation only for own consents
- [ ] Session revocation only for own sessions
- [ ] Email uniqueness validation

## Migration Strategy

### Redirect Old Routes

Add redirects in `Program.cs` for backward compatibility:

```csharp
app.MapGet("/Password/Index", () => Results.Redirect("/Account/Password"));
app.MapGet("/Mfa/Index", () => Results.Redirect("/Account/Security"));
```

Or keep old pages with deprecation notice and link to new location.

## Success Criteria

✅ **Phase 4 Complete When:**
1. All 8 account pages functional (`/Account/*`)
2. Dashboard provides overview with quick stats
3. Profile, password, MFA fully working (migrated)
4. Sessions management allows viewing and revocation
5. Consents management shows and revokes authorizations
6. Linked accounts displays external identity links with unlink capability
7. Alternative emails allows add/remove
8. Sidebar navigation includes "My Account" section
9. All pages use tenant-aware links with `ITenantAccessor`
10. No admin role required - all authenticated users have access
11. All tests passing
12. Documentation complete

## Timeline

**Week 1 (Oct 6-12):**
- Day 1-2: Create structure, dashboard, profile ✅
- Day 3-4: Migrate password + security ✅
- Day 5: Sessions management (basic)

**Week 2 (Oct 13-19):**
- Day 1-2: Consents management
- Day 3-4: Linked accounts
- Day 5: Alternative emails

**Week 3 (Oct 20-26):**
- Day 1-2: Polish UI, responsive design
- Day 3-4: Testing, bug fixes
- Day 5: Documentation, deployment

## Dependencies

**None** - Phase 4 is independent and can be implemented alongside existing features.

## Risks & Mitigations

**Risk 1:** Session management complexity (no UserSession table yet)
- **Mitigation:** Use Token table initially, add UserSession later if needed

**Risk 2:** Email verification not implemented
- **Mitigation:** Allow adding emails without verification, add verification in Phase 5

**Risk 3:** External identity linking flow complex
- **Mitigation:** Display existing links first, add "Link New" in Phase 5

## Next Steps

After Phase 4 completion:
- **Phase 5:** UI Polish (tenant switcher, impersonation, mobile)
- **Phase 6:** Email verification system
- **Phase 7:** Advanced session management (device info, location)
- **Phase 8:** External identity linking flow

---

**Status:** 🚧 Implementation in progress  
**Started:** October 6, 2025  
**Target Completion:** October 26, 2025
