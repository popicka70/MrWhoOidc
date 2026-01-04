# IdP Chaining Configuration URLs Feature

## Overview
Added tenant-aware IdP chaining URLs with copy-to-clipboard functionality in the admin Edit Client page to help administrators configure upstream identity providers.

## Implementation Date
October 8, 2025

## Changes Made

### 1. Code-Behind Updates (`Edit.cshtml.cs`)

#### New Dependencies
- Added `OidcOptions` to constructor parameters
- Added `using MrWhoOidc.Auth.MultiTenancy`
- Added `using MrWhoOidc.WebAuth.Handlers`
- Added `using MrWhoOidc.WebAuth.Extensions`

#### New Properties
```csharp
public string IdpChainingAuthorizationUrl { get; private set; } = string.Empty;
public string IdpChainingEndSessionUrl { get; private set; } = string.Empty;
```

#### URL Building Logic
In the `OnGetAsync` method, URLs are built using tenant-aware issuer:
```csharp
var issuer = HttpContext.GetIssuer(oidcOptions);
var baseUrl = issuer.TrimEnd('/');
IdpChainingAuthorizationUrl = $"{baseUrl}/authorize";
IdpChainingEndSessionUrl = $"{baseUrl}/connect/endsession";
```

This ensures:
- **Single-tenant mode**: Returns root issuer (e.g., `https://auth.example.com/authorize`)
- **Multi-tenant mode**: Returns path-based issuer (e.g., `https://auth.example.com/t/acme/authorize`)

### 2. UI Updates (`Edit.cshtml`)

#### New Section in Providers Tab
Added a prominent card at the top of the Providers tab with:
- **Authorization Endpoint (Login URL)**: Read-only input field with the `/authorize` endpoint
- **End Session Endpoint (Logout URL)**: Read-only input field with the `/connect/endsession` endpoint
- **Copy Buttons**: Each URL has a dedicated copy button with visual feedback

#### Features
- URLs are displayed in monospace font for clarity
- Fields are read-only to prevent accidental editing
- Each field has helpful descriptive text explaining its purpose
- Visual feedback when copying (checkmark on success, X on failure)
- Bootstrap icons for better UX

#### JavaScript Enhancement
Added `copyToClipboard` function that:
- Uses modern `navigator.clipboard.writeText` API with fallback to `document.execCommand`
- Provides visual feedback by changing button icon and color
- Shows "Copied!" message for 1.5 seconds on success
- Shows "Failed" message for 1.5 seconds on error
- Automatically resets to original state

### 3. Location in UI
The new section appears:
- In the **Providers tab** (third tab in the Edit Client page)
- At the **top of the tab content** (before the "Add or update mapping" section)
- With a distinctive **blue header** (`bg-info`) to stand out

## Usage for Administrators

### Scenario: Configuring Upstream IdP
When setting up an upstream identity provider that needs to chain to this MrWhoOidc instance:

1. Navigate to **Admin** → **Clients** → **Edit** (for the relevant client)
2. Click the **Providers** tab
3. Find the "IdP Chaining Configuration URLs" section at the top
4. Copy the **Authorization Endpoint** URL and paste it into the upstream IdP's `authorization_endpoint` configuration
5. Copy the **End Session Endpoint** URL and paste it into the upstream IdP's `end_session_endpoint` configuration

### Benefits
- **No manual URL construction**: URLs are automatically built with correct tenant paths
- **Copy-paste ready**: One click to copy, no typos
- **Tenant-aware**: Works correctly in both single-tenant and multi-tenant deployments
- **Always accurate**: URLs reflect the current request context and tenant

## Tenant Sensitivity

The implementation correctly handles multi-tenancy:
- Uses `HttpContext.GetIssuer(oidcOptions)` which consults `IIssuerBuilder`
- In multi-tenant mode, URLs include the tenant slug: `/t/{tenant-slug}/authorize`
- In single-tenant mode, URLs are at the root: `/authorize`
- Works with custom issuer configurations in `appsettings.json`

## Related Documentation
- [IdP Chaining Configuration Guide](idp-chaining-client-configuration.md)
- [IdP Chaining Backlog](idp-chaining-backlog.md)

## Testing Recommendations
1. **Single-tenant**: Verify URLs are at root level
2. **Multi-tenant**: Verify URLs include correct tenant slug
3. **Copy functionality**: Test copy buttons in different browsers
4. **Fallback**: Test on browsers without modern clipboard API
5. **Visual feedback**: Verify success/failure messages appear correctly

## Technical Notes

### Why These Endpoints?
- `/authorize`: Standard OAuth 2.0 authorization endpoint (RFC 6749)
- `/connect/endsession`: OIDC end session endpoint (OIDC Session Management)

### Security Considerations
- URLs are read-only in the UI (no modification risk)
- No sensitive data is exposed (public endpoints)
- Tenant isolation is maintained through URL path

### Browser Compatibility
- Modern browsers: Uses `navigator.clipboard.writeText` (secure context required)
- Legacy browsers: Falls back to `document.execCommand('copy')`
- Works in all major browsers: Chrome, Firefox, Edge, Safari

## Future Enhancements
Potential additions (not currently implemented):
- Discovery document URL with copy button
- JWKS URI with copy button
- Token endpoint URL (if needed for back-channel flows)
- Full OpenID Connect metadata JSON with download button
- QR code generation for mobile configuration
