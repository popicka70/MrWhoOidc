# Quickstart: URL Convention Migration to kebab-case

**Feature**: System-Wide URL Convention Standardization to kebab-case  
**Audience**: Developers implementing the URL convention changes  
**Estimated Time**: 4-6 hours (depending on test suite size)

## Overview

This guide walks through converting all URLs in MrWhoOidc from mixed PascalCase to consistent kebab-case. This is a **clean break migration** - no backward compatibility is provided.

**Timeline**:

- Day 0-29: Notify external parties (IdPs, RP clients, admin users)
- Day 30: Deploy changes (old URLs return 404)
- Day 30+: Monitor and support migration issues

## Prerequisites

- [ ] 30-day advance notice sent to external parties
- [ ] Notification acknowledgments tracked in audit log
- [ ] Backup of production database taken
- [ ] Staging environment available for testing

## Migration Steps

### Step 1: Update OIDC Protocol Endpoints (Priority: P1)

**File**: `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs`

**Changes**:

```csharp
// BEFORE
routes.MapGet("/Auth/External/Start", (IExternalOidcHandler h, HttpContext ctx) => h.StartAsync(ctx));
routes.MapGet("/Auth/External/Callback", (IExternalOidcHandler h, HttpContext ctx) => h.CallbackAsync(ctx));
routes.MapGet("/Auth/External/Confirm", (IExternalOidcHandler h, HttpContext ctx) => h.ConfirmLinkAsync(ctx));
routes.MapGet("/Auth/QrMobile", (IQrLoginHandler h, HttpContext ctx) => h.MobileLandingAsync(ctx));

// AFTER
routes.MapGet("/auth/external/start", (IExternalOidcHandler h, HttpContext ctx) => h.StartAsync(ctx));
routes.MapGet("/auth/external/callback", (IExternalOidcHandler h, HttpContext ctx) => h.CallbackAsync(ctx));
routes.MapGet("/auth/external/confirm", (IExternalOidcHandler h, HttpContext ctx) => h.ConfirmLinkAsync(ctx));
routes.MapGet("/auth/qr-mobile", (IQrLoginHandler h, HttpContext ctx) => h.MobileLandingAsync(ctx));
```

**Verification**:

```bash
# Build and test discovery endpoint
dotnet build
dotnet run --project MrWhoOidc.AppHost

# Verify discovery document contains kebab-case URLs
curl https://localhost:5001/.well-known/openid-configuration | jq .authorization_endpoint
# Expected: "https://localhost:5001/authorize"

curl https://localhost:5001/.well-known/openid-configuration | jq
# Verify all endpoint URLs use kebab-case
```

---

### Step 2: Update Admin UI Razor Pages (Priority: P2)

**Approach**: Add `@page` directives to each Razor Page to specify kebab-case routes.

**Pattern**:

```csharp
// File: Pages/Admin/Providers/Edit.cshtml
@page "/admin/providers/edit"
@model MrWhoOidc.WebAuth.Pages.Admin.Providers.EditModel

// File: Pages/Admin/Clients/Index.cshtml
@page "/admin/clients"
@model MrWhoOidc.WebAuth.Pages.Admin.Clients.IndexModel

// File: Pages/PlatformAdmin/Tenants/Index.cshtml
@page "/platform-admin/tenants"
@model MrWhoOidc.WebAuth.Pages.PlatformAdmin.Tenants.IndexModel
```

**Files to Update** (incomplete list - use grep to find all):

```text
Pages/Admin/
  ├── Branding.cshtml → @page "/admin/branding"
  ├── Settings.cshtml → @page "/admin/settings"
  ├── Index.cshtml → @page "/admin"
  ├── Providers/
  │   ├── Index.cshtml → @page "/admin/providers"
  │   ├── Edit.cshtml → @page "/admin/providers/edit"
  │   ├── Add.cshtml → @page "/admin/providers/add"
  │   ├── Delete.cshtml → @page "/admin/providers/delete"
  │   └── ClaimMappings.cshtml → @page "/admin/providers/claim-mappings"
  ├── Clients/ → Similar pattern
  ├── Users/ → Similar pattern
  ├── Realms/ → Similar pattern
  └── Scopes/ → Similar pattern

Pages/PlatformAdmin/
  ├── Index.cshtml → @page "/platform-admin"
  ├── Impersonation.cshtml → @page "/platform-admin/impersonation"
  ├── Tenants/ → Similar pattern
  └── ImpersonationHistory/ → Similar pattern
```

**Verification**:

```bash
# Manually navigate admin UI after deployment
# Verify all menu items use kebab-case URLs in browser address bar
```

---

### Step 3: Update User-Facing Pages (Priority: P2)

**Pattern**:

```csharp
// File: Pages/Account/Profile.cshtml
@page "/account/profile"

// File: Pages/Account/Sessions.cshtml
@page "/account/sessions"

// File: Pages/Account/WebAuthn.cshtml
@page "/account/webauthn"

// File: Pages/Password/Index.cshtml
@page "/password"

// File: Pages/Registrations/Index.cshtml
@page "/registrations"
```

**Pages Already Using kebab-case** (verify no regression):

```text
Pages/Login.cshtml → @page "/login" ✓
Pages/logout/FederatedCallback.cshtml → @page "/logout/federated-callback" ✓
Pages/account/ConfirmEmail.cshtml → @page "/account/confirm-email" ✓
```

---

### Step 4: Update Navigation Links (Priority: P2)

**File**: `Pages/Shared/_Layout.cshtml`

**Changes**:

```html
<!-- BEFORE -->
<a class="list-group-item" asp-page="/Admin/Providers">Providers</a>
<a class="list-group-item" asp-page="/Admin/Clients">Clients</a>
<a class="list-group-item" asp-page="/PlatformAdmin/Tenants/Index">Tenants</a>

<!-- AFTER -->
<a class="list-group-item" asp-page="/admin/providers">Providers</a>
<a class="list-group-item" asp-page="/admin/clients">Clients</a>
<a class="list-group-item" asp-page="/platform-admin/tenants">Tenants</a>
```

**Search Pattern**:

```bash
# Find all asp-page attributes
grep -r 'asp-page=' MrWhoOidc.WebAuth/Pages/Shared/

# Update each occurrence to kebab-case
```

**Files to Update**:

- `_Layout.cshtml` (20+ navigation links)
- `_AuthLayout.cshtml`
- `_TenantContextBanner.cshtml`
- `_ImpersonationBanner.cshtml`
- `_WebAuthnSetup.cshtml`

---

### Step 5: Update Programmatic URL Construction (Priority: P1)

**Pattern**: Search for `TenantAwareUrlBuilder.BuildTenantPath` and `RedirectToPage` calls.

**Search Command**:

```bash
grep -r 'TenantAwareUrlBuilder.BuildTenantPath\|RedirectToPage' MrWhoOidc.WebAuth/ --include="*.cs"
```

**Update Pattern**:

```csharp
// BEFORE
TenantAwareUrlBuilder.BuildTenantPath("/Admin/Providers", tenantAccessor, options)
return RedirectToPage("/Admin/Index");

// AFTER
TenantAwareUrlBuilder.BuildTenantPath("/admin/providers", tenantAccessor, options)
return RedirectToPage("/admin/index");
```

**Files Identified** (from grep):

- `Pages/Admin/Providers/Edit.cshtml.cs` (line 213, 222)
- `Pages/Admin/TenantAwarePageModel.cs` (lines 52, 60, 85)
- `Pages/Admin/Realms/Edit.cshtml.cs` (line 105)
- `Pages/Admin/Realms/Index.cshtml.cs` (lines 94, 105)
- `Pages/Admin/Realms/Add.cshtml.cs` (line 66)
- `Pages/Admin/Providers/ClaimMappings.cshtml.cs` (line 40)
- `Pages/Admin/Providers/Delete.cshtml.cs` (lines 67, 82)
- `Pages/Admin/Providers/Add.cshtml.cs` (line 89)

**Verification**:

```bash
# After updates, grep for remaining PascalCase path segments
grep -r '"/Admin/\|/PlatformAdmin/\|/Account/[A-Z]' MrWhoOidc.WebAuth/Pages/ --include="*.cs"
# Should return zero results
```

---

### Step 6: Update Test Assertions (Priority: P1)

**Pattern**: Search-and-replace URL segments in test files.

**Search-and-Replace Table**:

| Old Pattern | New Pattern | Notes |
|-------------|-------------|-------|
| `/Admin/` | `/admin/` | All admin routes |
| `/PlatformAdmin/` | `/platform-admin/` | Platform admin routes |
| `/Account/Profile` | `/account/profile` | User account pages |
| `/Account/Sessions` | `/account/sessions` | Session management |
| `/Account/WebAuthn` | `/account/webauthn` | WebAuthn management |
| `/Auth/External/Start` | `/auth/external/start` | External OIDC start |
| `/Auth/External/Callback` | `/auth/external/callback` | External OIDC callback |
| `/Auth/External/Confirm` | `/auth/external/confirm` | External OIDC confirm |
| `/Auth/QrMobile` | `/auth/qr-mobile` | QR login mobile landing |

**Test Files to Update**:

```bash
# Find all test files with URL assertions
grep -r '"/Admin/\|/PlatformAdmin/\|/Account/\|/Auth/' MrWhoOidc.UnitTests/ --include="*.cs"
```

**Verification**:

```bash
# Run full test suite
dotnet test

# Expected: All tests pass
# If tests fail, review assertion failures for missed URL updates
```

---

### Step 7: Update Documentation (Priority: P3)

**Files to Update**:

```text
docs/
  ├── developer-guide.md → Update example URLs in code snippets
  ├── admin-guide.md → Update screenshots and URL references
  ├── idp-chaining-client-configuration.md → Update redirect URI examples
  ├── backlog.md → Update URL references
  └── [Other docs with URL references]
```

**Search Pattern**:

```bash
grep -r 'https://.*/(Admin|PlatformAdmin|Account|Auth)/[A-Z]' docs/ --include="*.md"
```

---

### Step 8: Deploy 404 Error Page with Helpful Suggestions (Optional)

**File**: `MrWhoOidc.WebAuth/Program.cs`

**Add Custom 404 Handler**:

```csharp
app.UseStatusCodePagesWithReExecute("/error/{0}");

app.MapGet("/error/{statusCode:int}", (int statusCode, HttpContext ctx) => 
{
    if (statusCode == 404)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var suggestion = SuggestKebabCase(path);
        
        var html = $@"
<!DOCTYPE html>
<html>
<head><title>Page Not Found</title></head>
<body>
    <h1>Page Not Found (404)</h1>
    <p>The URL <code>{path}</code> does not exist.</p>
    <div class='alert alert-info'>
        <h4>Did you mean this URL?</h4>
        <p>We recently changed all URLs to use lowercase kebab-case.</p>
        <p><strong>Try:</strong> <a href='{suggestion}'>{suggestion}</a></p>
    </div>
    <p><a href='/'>Return to home page</a></p>
</body>
</html>";
        
        return Results.Content(html, "text/html");
    }
    return Results.NotFound();
});

static string SuggestKebabCase(string path)
{
    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var kebabSegments = segments.Select(ToKebabCase);
    return "/" + string.Join("/", kebabSegments);
}

static string ToKebabCase(string text)
{
    if (string.IsNullOrEmpty(text)) return text;
    var result = new System.Text.StringBuilder();
    result.Append(char.ToLower(text[0]));
    for (int i = 1; i < text.Length; i++)
    {
        if (char.IsUpper(text[i]))
        {
            result.Append('-');
            result.Append(char.ToLower(text[i]));
        }
        else
        {
            result.Append(text[i]);
        }
    }
    return result.ToString();
}
```

---

## Pre-Deployment Checklist

- [ ] All OIDC protocol endpoints updated to kebab-case
- [ ] All admin Razor Pages have `@page` directives with kebab-case routes
- [ ] All user-facing pages have `@page` directives with kebab-case routes
- [ ] All navigation links in layouts updated to kebab-case
- [ ] All `TenantAwareUrlBuilder.BuildTenantPath` calls use kebab-case paths
- [ ] All `RedirectToPage` calls use kebab-case paths
- [ ] All test assertions updated to expect kebab-case URLs
- [ ] Full test suite passes (`dotnet test` returns exit code 0)
- [ ] Discovery document verified to contain only kebab-case URLs
- [ ] Manual testing: Admin UI navigation uses kebab-case URLs
- [ ] Manual testing: User flows (login/profile/logout) use kebab-case URLs
- [ ] Manual testing: External IdP callback works with kebab-case URL
- [ ] Documentation updated with new URL patterns
- [ ] External parties notified 30 days in advance
- [ ] Custom 404 error page deployed (optional but recommended)

## Post-Deployment Monitoring

**Day 1-7 After Deployment**:

- Monitor 404 error rate for spike (indicates external parties hitting old URLs)
- Check support tickets for URL-related issues
- Monitor external IdP callback failures
- Review audit logs for authentication flow errors

**If Issues Detected**:

1. Identify which external party is using old URL
2. Contact party directly via phone/email
3. Provide updated redirect URI configuration
4. Offer assistance with debugging

**Metrics to Track**:

- 404 error rate (should decrease over 7-14 days)
- Authentication success rate (should remain stable)
- Admin UI usage (should remain stable)
- Support ticket volume (may spike initially, then normalize)

## Rollback Plan

**If critical issues detected within 24 hours**:

1. Revert code changes via git: `git revert <commit-hash>`
2. Redeploy previous version
3. Notify external parties of rollback
4. Analyze failure and plan corrective actions
5. Reschedule migration with fixes

**Note**: Rollback becomes increasingly difficult after 24 hours as external parties begin adopting new URLs.

## Success Criteria Validation

After deployment, verify:

- [ ] SC-001: Zero PascalCase URLs in routing configuration (grep returns zero results)
- [ ] SC-002: Zero PascalCase URLs in navigation links (grep returns zero results)
- [ ] SC-003: Zero PascalCase URLs in programmatic construction (grep returns zero results)
- [ ] SC-004: Discovery document contains only kebab-case URLs (manual verification)
- [ ] SC-005: Complete OIDC flow works (integration test passes)
- [ ] SC-006: Admin UI navigation works (manual testing)
- [ ] SC-007: User account management works (manual testing)
- [ ] SC-008: Tenant-aware routing works (integration test passes)
- [ ] SC-009: All tests pass (dotnet test exit code 0)

## Troubleshooting

### Issue: External IdP Callback Fails

**Symptom**: Users report "Authentication failed" after external IdP redirect.

**Diagnosis**:

```bash
# Check callback URL in IdP configuration
# Should be: https://mrwhooidc.example.com/auth/external/callback
# Not: https://mrwhooidc.example.com/Auth/External/Callback
```

**Resolution**: Contact external IdP admin, request redirect URI update.

---

### Issue: Admin User Bookmarks Broken

**Symptom**: Admin users report "Page not found" when clicking bookmarks.

**Diagnosis**: Bookmarked URLs use old PascalCase convention.

**Resolution**: Instruct users to re-bookmark admin pages with new kebab-case URLs. Custom 404 page will suggest correct URL.

---

### Issue: Email Confirmation Links Broken

**Symptom**: Users report "Invalid link" when clicking email confirmation links.

**Diagnosis**: Old emails contain PascalCase confirmation URLs.

**Resolution**: Instruct users to request new confirmation email from login page. New emails will contain kebab-case URLs.

---

## Contact

For questions or issues during migration:

- **Slack**: #mrwhooidc-dev
- **Email**: dev-team@mrwhooidc.example.com
- **On-call**: See PagerDuty schedule
