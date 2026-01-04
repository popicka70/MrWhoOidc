# IdP Chaining Tenant-Aware URLs Fix

## Issue
When `Oidc__Issuer` is explicitly configured in Docker/production environments, the IdP Chaining Configuration URLs on the Providers tab were not including the tenant path segment, even when multi-tenancy was enabled.

### Example of the Problem
- Instance: Multi-tenant enabled, viewing client in tenant `pop-app`
- Expected URLs:
  - `https://localhost:8443/t/pop-app/authorize`
  - `https://localhost:8443/t/pop-app/connect/endsession`
- Actual URLs (before fix):
  - `https://localhost:8443/authorize` ❌
  - `https://localhost:8443/connect/endsession` ❌

## Root Cause
The `HttpContext.GetIssuer()` method prioritizes explicitly configured `Oidc__Issuer` over the tenant-aware issuer builder. This is by design for backward compatibility, but it meant that IdP Chaining URLs were not tenant-aware when an explicit issuer was configured.

### Why Oidc__Issuer is Needed
In containerized/production environments, the issuer must be explicitly configured because:
- The application needs to know its external-facing URL
- The container's internal hostname differs from the public URL
- Docker compose sets: `Oidc__Issuer: "https://localhost:8443"`

## Solution
Enhanced the IdP Chaining URL generation logic in `Edit.cshtml.cs` to explicitly append tenant path when multi-tenancy is enabled, regardless of whether `Oidc__Issuer` is configured.

### Changes Made

**File**: `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs`

1. **Added dependency injection** for `IMultiTenancyOptions`:
```csharp
public class EditModel(
    // ... existing parameters ...
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor)
```

2. **Enhanced URL building logic** in `OnGetAsync()`:
```csharp
// Build tenant-aware IdP chaining URLs
var issuer = HttpContext.GetIssuer(oidcOptions);
var baseUrl = issuer.TrimEnd('/');

// If multi-tenancy is enabled, append tenant path to the issuer
if (multiTenancyOptions.Enabled && TenantAccessor.CurrentTenant != null)
{
    var tenantSlug = TenantAccessor.CurrentTenant.Slug;
    baseUrl = $"{baseUrl}/t/{tenantSlug}";
}

IdpChainingAuthorizationUrl = $"{baseUrl}/authorize";
IdpChainingEndSessionUrl = $"{baseUrl}/connect/endsession";
```

## How It Works

### Single-Tenant Mode
When `MultiTenancy__Enabled: false`:
- Uses issuer as-is: `https://localhost:8443`
- Result: `https://localhost:8443/authorize`

### Multi-Tenant Mode
When `MultiTenancy__Enabled: true` and viewing tenant `pop-app`:
- Takes issuer: `https://localhost:8443`
- Appends tenant path: `/t/pop-app`
- Result: `https://localhost:8443/t/pop-app/authorize` ✅

## Configuration Requirements
No configuration changes needed! The fix automatically adapts based on:
- `MultiTenancy__Enabled` setting
- Current tenant context from URL/cookie

## Testing
1. Ensure Docker instance is running with multi-tenancy enabled:
   ```yaml
   MultiTenancy__Enabled: "true"
   Oidc__Issuer: "https://localhost:8443"
   ```

2. Navigate to any tenant's client edit page:
   ```
   https://localhost:8443/{tenant-slug}/Admin/Clients/{client-id}?tab=providers
   ```

3. Verify the IdP Chaining URLs include the tenant path:
   ```
   Authorization Endpoint: https://localhost:8443/t/{tenant-slug}/authorize
   End Session Endpoint: https://localhost:8443/t/{tenant-slug}/connect/endsession
   ```

## Impact
- ✅ IdP Chaining URLs are now correctly tenant-aware in all deployment scenarios
- ✅ Works with explicit `Oidc__Issuer` configuration
- ✅ Backward compatible with single-tenant mode
- ✅ No breaking changes to existing functionality

## Related Files
- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs` - URL generation logic
- `MrWhoOidc.WebAuth/Extensions/HttpContextExtensions.cs` - GetIssuer method
- `MrWhoOidc.Auth/MultiTenancy/IssuerBuilder.cs` - Tenant-aware issuer building
- `docker-compose.yml` - Docker environment configuration

## Date
October 12, 2025
