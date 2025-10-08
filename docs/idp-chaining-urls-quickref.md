# IdP Chaining URLs - Quick Reference

## What Was Added
Admin interface now displays tenant-aware IdP chaining URLs with copy buttons in the Edit Client page.

## Location
**Admin** → **Clients** → **Edit [Client]** → **Providers Tab** (at the top)

## URLs Provided

### 1. Authorization Endpoint (Login URL)
```
{issuer}/authorize
```
- **Single-tenant example**: `https://auth.example.com/authorize`
- **Multi-tenant example**: `https://auth.example.com/t/acme/authorize`
- **Use in upstream IdP**: Set as `authorization_endpoint`

### 2. End Session Endpoint (Logout URL)
```
{issuer}/connect/endsession
```
- **Single-tenant example**: `https://auth.example.com/connect/endsession`
- **Multi-tenant example**: `https://auth.example.com/t/acme/connect/endsession`
- **Use in upstream IdP**: Set as `end_session_endpoint`

## How to Use

### Copy URLs to Clipboard
1. Click the **Copy** button next to each URL
2. Button will show **✓ Copied!** on success
3. Paste into your upstream IdP configuration

### Tenant Awareness
- URLs automatically reflect the current tenant context
- No manual editing needed
- Always accurate for the current deployment

## Visual Appearance
```
┌─────────────────────────────────────────────────┐
│ 🔗 IdP Chaining Configuration URLs              │ (Blue header)
├─────────────────────────────────────────────────┤
│ ℹ Use these tenant-aware URLs when configuring │
│   this instance as a downstream IdP...          │
│                                                 │
│ Authorization Endpoint (Login URL)              │
│ [https://auth.example.com/t/acme/authorize] [Copy] │
│ Use this URL as the authorization_endpoint...   │
│                                                 │
│ End Session Endpoint (Logout URL)               │
│ [https://auth.example.com/t/acme/connect/en...] [Copy] │
│ Use this URL as the end_session_endpoint...     │
└─────────────────────────────────────────────────┘
```

## Implementation Details

### Code Changes
- **File**: `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml[.cs]`
- **Properties added**: `IdpChainingAuthorizationUrl`, `IdpChainingEndSessionUrl`
- **JavaScript**: `copyToClipboard()` function with visual feedback

### Tenant Context
Uses `HttpContext.GetIssuer(oidcOptions)` which:
- Consults `IIssuerBuilder` for tenant-aware issuer construction
- Returns configured issuer from `appsettings.json` if set
- Falls back to request-based issuer with tenant path if multi-tenant enabled

## Related Files
- Implementation: `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml[.cs]`
- Documentation: `docs/idp-chaining-urls-feature.md`
- Configuration guide: `docs/idp-chaining-client-configuration.md`

## Browser Compatibility
✅ Chrome, Firefox, Edge, Safari (all modern versions)
✅ Falls back to legacy clipboard API for older browsers

## Common Use Cases

### Case 1: Enterprise SSO Chaining
Upstream: Corporate Azure AD  
→ This MrWhoOidc instance (configured with URLs from this feature)  
→ Downstream: Internal applications

### Case 2: Partner Federation
Upstream: Partner's IdP  
→ This MrWhoOidc instance (tenant-specific URLs)  
→ Downstream: Shared services

### Case 3: Development/Testing
Test IdP configuration  
→ This MrWhoOidc instance (localhost URLs)  
→ Test applications

## Tips
- 📋 Always copy URLs from this interface (don't construct manually)
- 🔐 URLs are public endpoints (no secrets exposed)
- 🏢 Each tenant gets unique URLs in multi-tenant mode
- ✅ Verify URLs after copying by checking tenant slug presence

## Support
If URLs don't reflect expected tenant context:
1. Check tenant context in page header
2. Verify multi-tenancy is enabled in configuration
3. Check `OidcOptions.Issuer` setting in appsettings
4. Review `IIssuerBuilder` implementation
