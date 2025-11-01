# Phase 0: Research - URL Convention Standardization to kebab-case

**Feature**: System-Wide URL Convention Standardization to kebab-case  
**Date**: 2025-11-01  
**Purpose**: Resolve technical unknowns and establish implementation patterns

## Research Questions

### Q1: ASP.NET Core Razor Pages Route Customization

**Question**: How can we customize Razor Pages URL routes to use kebab-case while keeping physical file/folder structure in PascalCase (for .NET convention compatibility)?

**Research Findings**:

ASP.NET Core Razor Pages provides multiple approaches for route customization:

1. **@page Directive with Route Template** (RECOMMENDED)
   - Add `@page "/kebab-case-route"` directive at top of each `.cshtml` file
   - Physical file structure remains unchanged (e.g., `Pages/Admin/Providers/Edit.cshtml`)
   - URL route becomes `/admin/providers/edit` (or custom template)
   - Works with route parameters: `@page "/admin/providers/edit/{id:guid}"`
   - Tenant-aware routing automatically prefixes: `/t/{slug}/admin/providers/edit`

2. **PageRouteModelConvention** (GLOBAL APPROACH)
   - Create custom `IPageRouteModelConvention` to transform all routes
   - Register in `Program.cs`: `options.Conventions.Add(new KebabCasePageRouteConvention())`
   - Automatically applies kebab-case transformation to all pages
   - More maintainable for large-scale conversions
   - Reference: <https://learn.microsoft.com/en-us/aspnet/core/razor-pages/razor-pages-conventions>

3. **Folder/File Renaming**
   - Rename physical folders/files to lowercase-kebab-case
   - Routes automatically match file structure
   - **NOT RECOMMENDED**: Violates .NET naming conventions (PascalCase for types/files)
   - Would require renaming `.cshtml.cs` PageModel classes as well

**Decision**: Use **@page directive approach** for explicit control and minimal disruption to existing .NET conventions. Physical structure stays PascalCase, routes become kebab-case via directives.

**Rationale**:
- Explicit and auditable (each page declares its route)
- No changes to .NET file naming conventions
- Easy to review in code reviews (route visible at top of file)
- Compatible with existing Razor Pages infrastructure
- No custom conventions to maintain

**Implementation Pattern**:

```csharp
// File: MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml
@page "/admin/providers/edit"
@model MrWhoOidc.WebAuth.Pages.Admin.Providers.EditModel

// Rest of Razor markup...
```

**Multi-tenant Support**:

```csharp
// Tenant-aware routing automatically prefixes:
// Single-tenant mode: /admin/providers/edit
// Multi-tenant mode: /t/{slug}/admin/providers/edit
// No changes needed to @page directive
```

---

### Q2: Minimal API Route Updates

**Question**: What's the pattern for updating ASP.NET Core Minimal API endpoint routes from PascalCase to kebab-case?

**Research Findings**:

Minimal APIs define routes as string arguments to `MapGet/MapPost/MapPut/MapDelete` methods. Update strategy:

1. **Direct String Replacement** (SIMPLE)
   - Change route string from `/Auth/External/Callback` to `/auth/external/callback`
   - Example: `routes.MapGet("/Auth/External/Callback", ...)` → `routes.MapGet("/auth/external/callback", ...)`
   - No framework changes required

2. **Route Constraints Preserved**
   - Route parameters and constraints remain unchanged: `{clientId:guid}`, `{sessionToken}`, etc.
   - Example: `/clients/{clientId:guid}/jwks` → `/clients/{clientId:guid}/jwks` (already kebab-case)

3. **Discovery Document Auto-Updates**
   - Discovery handler (`DiscoveryHandler.cs`) builds endpoint URLs from routing configuration
   - When endpoint routes change, discovery document automatically reflects new URLs
   - No separate configuration update needed

**Decision**: Perform direct string replacement in `EndpointMappingExtensions.cs` and verify discovery document output.

**Rationale**:
- Simplest approach with no framework complexity
- Testable via integration tests (discovery + actual endpoint calls)
- Discovery document serves as documentation of public API surface

**Implementation Checklist**:

```text
File: MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs

Update these routes:
- MapGet("/Auth/External/Start", ...) → MapGet("/auth/external/start", ...)
- MapGet("/Auth/External/Callback", ...) → MapGet("/auth/external/callback", ...)
- MapGet("/Auth/External/Confirm", ...) → MapGet("/auth/external/confirm", ...)
- MapGet("/Auth/QrMobile", ...) → MapGet("/auth/qr-mobile", ...)

Verify these routes (already kebab-case):
- MapGet("/logout/federated-callback", ...) ✓
- MapGet("/logout/final", ...) ✓
- MapGet("/authorize", ...) ✓
- MapPost("/token", ...) ✓
- MapGet("/userinfo", ...) ✓
- MapPost("/revoke", ...) ✓
- MapPost("/introspect", ...) ✓
- MapPost("/par", ...) ✓
```

---

### Q3: TenantAwareUrlBuilder Compatibility

**Question**: Does `TenantAwareUrlBuilder` utility need updates to handle kebab-case paths?

**Research Findings**:

Reviewed `MrWhoOidc.WebAuth/Extensions/TenantAwareUrlBuilder.cs`:

```csharp
public static string BuildTenantPath(
    string path,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions)
{
    if (string.IsNullOrEmpty(path))
        path = "/";
    
    // Ensure path starts with /
    if (!path.StartsWith('/'))
        path = "/" + path;

    // Add tenant prefix if multi-tenancy enabled
    var currentTenant = tenantAccessor.CurrentTenant;
    if (multiTenancyOptions.Enabled && currentTenant != null)
        return $"/t/{currentTenant.Slug}{path}";

    return path;
}
```

**Analysis**:
- Builder performs **zero path parsing or validation**
- Only operations: ensure leading slash, prepend tenant prefix
- Path content (PascalCase vs kebab-case) is **completely irrelevant** to builder logic
- Works identically with `/Admin/Providers` or `/admin/providers`

**Decision**: **No changes needed** to `TenantAwareUrlBuilder`. It's case-agnostic.

**Rationale**:
- Builder is a simple string concatenation utility
- No path segment parsing or transformation
- Kebab-case paths work identically to PascalCase paths
- All call sites just need to pass kebab-case path strings

**Call Site Update Pattern**:

```csharp
// BEFORE
TenantAwareUrlBuilder.BuildTenantPath("/Admin/Providers", tenantAccessor, options)

// AFTER
TenantAwareUrlBuilder.BuildTenantPath("/admin/providers", tenantAccessor, options)
```

---

### Q4: Test Assertion Updates

**Question**: What patterns exist in current tests for URL expectations, and how should they be updated?

**Research Findings**:

Grep search of `MrWhoOidc.UnitTests` reveals URL expectations in:

1. **Integration Test Assertions**
   - HTTP redirect assertions: `Assert.AreEqual("/Admin/Index", result.Url)`
   - Discovery document validation: JSON deserialization checks endpoint URLs
   - Protocol flow tests: Follow redirects and verify callback URLs

2. **String Constant Assertions**
   - Hardcoded URL strings in test setup: `var redirectUri = "https://localhost/Auth/External/Callback"`
   - Route template matching: Tests that verify route parameter binding

3. **Mock URL Construction**
   - Test data seeders that build URLs: `$"/Admin/Clients/Edit?id={clientId}"`
   - Email link generation tests: Verify confirmation URLs

**Update Strategy**:

1. **Search-and-replace URL segments**:
   - `/Admin/` → `/admin/`
   - `/PlatformAdmin/` → `/platform-admin/`
   - `/Account/` → `/account/`
   - `/Auth/External/` → `/auth/external/`
   - `/Auth/` → `/auth/`

2. **Run tests after each route conversion** to catch missed assertions

3. **Discovery document test** becomes primary validation - if discovery is correct, API contracts are correct

**Decision**: Systematic search-and-replace in test files after route conversions, with full test suite run as validation.

---

### Q5: External Party Notification Process

**Question**: What's the process for notifying external IdPs and RP clients of URL changes with 30-day advance notice?

**Research Findings**:

External parties requiring notification:

1. **External Identity Providers**
   - Have registered redirect URIs pointing to: `https://mrwhooidc.example.com/Auth/External/Callback`
   - Must update their configuration to: `https://mrwhooidc.example.com/auth/external/callback`
   - Notification method: Email to registered IdP admin contacts (stored in `IdentityProvider` table)

2. **Relying Party Clients**
   - Have configured `redirect_uri` values in their applications pointing to old URLs
   - Typically internal applications under our control, but some may be third-party
   - Notification method: Email to registered client contacts + developer portal announcement

3. **End Users**
   - Admin users have bookmarks to admin pages (low impact - can be re-bookmarked)
   - Email confirmation links in inboxes will break (intentional - tokens invalidated)
   - Notification method: System banner in admin UI 30 days before deployment + email to all admin users

**Notification Timeline**:

```text
Day 0:  Announce URL change via email and admin UI banner
Day 7:  Reminder email to external parties who haven't acknowledged
Day 14: Second reminder email
Day 21: Third reminder email with deployment date
Day 28: Final warning - 2 days to deployment
Day 30: Deploy URL changes
```

**Communication Template**:

```markdown
Subject: [ACTION REQUIRED] MrWhoOidc URL Convention Change - Update by [DATE]

Dear [External Party Name],

We are standardizing all URLs in MrWhoOidc to use kebab-case convention. This requires you to update your registered redirect URIs.

**What's Changing**:
- Old: https://mrwhooidc.example.com/Auth/External/Callback
- New: https://mrwhooidc.example.com/auth/external/callback

**Action Required**:
Update your identity provider configuration to use the new redirect URI by [DATE].

**Timeline**:
- Changes deploy: [DATE] at [TIME] UTC
- Old URLs will return 404 after deployment

**Need Help?**
Contact support@mrwhooidc.example.com

Best regards,
MrWhoOidc Platform Team
```

**Decision**: Create notification email templates and deployment checklist. Store notification acknowledgments in audit log.

---

### Q6: 404 Error Page with Helpful Messaging

**Question**: How should we implement the helpful 404 error page suggesting kebab-case alternatives?

**Research Findings**:

ASP.NET Core provides `UseStatusCodePagesWithReExecute` middleware for custom error pages:

```csharp
// In Program.cs
app.UseStatusCodePagesWithReExecute("/error/{0}");

// Error page handler
app.MapGet("/error/{statusCode:int}", (int statusCode, HttpContext ctx) => {
    if (statusCode == 404) {
        var path = ctx.Request.Path;
        // Analyze path and suggest kebab-case alternative
        return Results.Content(GenerateHelpful404Html(path), "text/html");
    }
    return Results.NotFound();
});
```

**Path Analysis Logic**:

```csharp
private static string SuggestKebabCase(string path)
{
    // If path contains PascalCase segments, suggest kebab-case equivalent
    // Example: /Admin/Providers → /admin/providers
    // Example: /Auth/External/Callback → /auth/external/callback
    
    // Simple heuristic: Lowercase and add hyphens before capitals
    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var kebabSegments = segments.Select(s => ToKebabCase(s));
    return "/" + string.Join("/", kebabSegments);
}

private static string ToKebabCase(string text)
{
    if (string.IsNullOrEmpty(text)) return text;
    
    // Insert hyphen before capitals (except first character)
    var result = new StringBuilder();
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

**Error Page Content**:

```html
<h1>Page Not Found (404)</h1>
<p>The URL you requested does not exist.</p>

<!-- If path looks like PascalCase -->
<div class="alert alert-info">
    <h4>Did you mean this URL?</h4>
    <p>We recently changed all URLs to use lowercase kebab-case convention.</p>
    <p><strong>Try this instead:</strong> <a href="/admin/providers">/admin/providers</a></p>
</div>

<p><a href="/">Return to home page</a></p>
```

**Decision**: Implement custom 404 handler with path analysis and kebab-case suggestion. Include in quickstart.md.

---

## Research Summary

All technical unknowns resolved:

1. ✅ **Razor Pages**: Use `@page "/kebab-case"` directives (physical structure stays PascalCase)
2. ✅ **Minimal APIs**: Direct string replacement in `MapGet/MapPost` routes
3. ✅ **TenantAwareUrlBuilder**: No changes needed (case-agnostic)
4. ✅ **Tests**: Systematic search-and-replace URL segments after route updates
5. ✅ **External Notifications**: 30-day email campaign with deployment checklist
6. ✅ **404 Error Page**: Custom middleware with PascalCase → kebab-case suggestion

**No blockers identified.** Ready to proceed to Phase 1 (Design).
