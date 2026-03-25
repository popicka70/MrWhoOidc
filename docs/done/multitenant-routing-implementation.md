# Multi-Tenant Routing Implementation

> Historical note: This implementation summary is retained for architectural history. Routing examples and compatibility notes reflect the repository state when this work landed. For current multi-tenant behavior and deployment guidance, use [README](../../README.md), [docs/developer-guide.md](../developer-guide.md), and [docs/production-setup-guide.md](../production-setup-guide.md).

**Date:** October 4, 2025  
**Status:** ✅ Completed  
**Branch:** MultiTenant

## Summary

Implemented multi-tenant routing pattern that supports both tenant-prefixed routes (`/t/{slug}/*`) and backward-compatible fallback routes for the default tenant. This allows the same OIDC server to serve multiple isolated tenants while maintaining backward compatibility with existing single-tenant deployments.

## Implementation Details

### 1. Endpoint Registration Strategy

**File:** `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs`

Created a mode-aware routing system that registers endpoints based on multi-tenancy configuration:

```csharp
var multiTenancyOptions = app.Services.GetRequiredService<IMultiTenancyOptions>();

if (multiTenancyOptions.Enabled)
{
    // Multi-tenant mode: register tenant-prefixed routes
    var tenantGroup = app.MapGroup("/t/{slug}");
    MapOidcEndpoints(tenantGroup);
    
    // Fallback routes for backward compatibility (map to default tenant)
    MapOidcEndpoints(app);
}
else
{
    // Single-tenant mode: register root-level routes only
    MapOidcEndpoints(app);
}
```

**Key Feature:** Extracted all OIDC endpoint registrations into a reusable `MapOidcEndpoints(IEndpointRouteBuilder)` method that can be called with either:
- Root app (for root-level routes)
- `MapGroup("/t/{slug}")` (for tenant-prefixed routes)

### 2. Tenant Resolution with Fallback

**File:** `MrWhoOidc.Auth/MultiTenancy/TenantResolver.cs`

Updated `ModeAwareTenantResolver` to support backward compatibility:

**Behavior in Multi-Tenant Mode:**
- Path with `/t/{slug}` prefix → Look up specific tenant by slug
- Path without `/t/{slug}` prefix → Fall back to default tenant (backward compatibility)
- Tenant not found in database → Return null (handled differently by middleware)

```csharp
// Multi-tenant mode: parse path for /t/{slug}
var slug = ExtractTenantSlugFromPath(path);
if (string.IsNullOrEmpty(slug))
{
    // No tenant slug in path - fall back to default tenant for backward compatibility
    return await ResolveDefaultTenantAsync(cancellationToken);
}

// Path has /t/{slug} - look up the specific tenant
return await ResolveTenantBySlugAsync(slug, cancellationToken);
```

### 3. Smart Error Handling

**File:** `MrWhoOidc.WebAuth/Middleware/TenantResolutionMiddleware.cs`

Middleware now distinguishes between two failure scenarios:

**404 Not Found:**
- Path has `/t/{slug}` prefix but tenant doesn't exist
- Example: `/t/nonexistent/.well-known/openid-configuration`
- Response: "Tenant not found."

**500 Internal Server Error:**
- Path has no `/t/{slug}` prefix (falling back to default) but default tenant doesn't exist
- This indicates a configuration error (default tenant missing from database)
- Response: "Server configuration error: default tenant not found."

```csharp
if (tenantContext == null)
{
    var hasPrefix = path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase);
    
    if (hasPrefix)
    {
        // Tenant not found - 404
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("Tenant not found.");
    }
    else
    {
        // Default tenant missing - 500
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsync("Server configuration error: default tenant not found.");
    }
    return;
}
```

## Supported Route Patterns

### Multi-Tenant Mode (`MultiTenancy:Enabled = true`)

#### Tenant-Prefixed Routes (Preferred)
```
GET  /t/{slug}/.well-known/openid-configuration
GET  /t/{slug}/jwks
GET  /t/{slug}/authorize
POST /t/{slug}/token
GET  /t/{slug}/userinfo
POST /t/{slug}/introspect
POST /t/{slug}/revoke
POST /t/{slug}/par
GET  /t/{slug}/logout
GET  /t/{slug}/connect/endsession
GET  /t/{slug}/Auth/External/Start
GET  /t/{slug}/Auth/External/Callback
GET  /t/{slug}/Auth/QrMobile
GET  /t/{slug}/clients/{clientId}/jwks
```

#### Fallback Routes (Backward Compatibility)
```
GET  /.well-known/openid-configuration  → Maps to default tenant
GET  /jwks                               → Maps to default tenant
GET  /authorize                          → Maps to default tenant
POST /token                              → Maps to default tenant
(All other OIDC endpoints)               → Map to default tenant
```

### Single-Tenant Mode (`MultiTenancy:Enabled = false`)

All routes registered at root level only (no `/t/{slug}` prefix):
```
GET  /.well-known/openid-configuration
GET  /jwks
GET  /authorize
POST /token
... (all standard OIDC endpoints)
```

## Testing Results

### ✅ All Tests Passing
- **Unit Tests:** 318/318 passing
- **Build:** Successful
- **Docker:** Running without errors

### ✅ Manual Verification (Docker on localhost:8443)

**Test 1: Tenant-Prefixed Route**
```bash
GET /t/default/.well-known/openid-configuration
Status: 200 OK
Issuer: https://localhost:7208
```

**Test 2: Fallback Route (Backward Compatibility)**
```bash
GET /.well-known/openid-configuration
Status: 200 OK
Issuer: https://localhost:7208
```

**Test 3: Non-Existent Tenant**
```bash
GET /t/nonexistent/.well-known/openid-configuration
Status: 404 Not Found
Response: "Tenant not found."
```

**Test 4: JWKS Endpoints**
```bash
GET /t/default/jwks
Status: 200 OK
Keys: 1

GET /jwks
Status: 200 OK
Keys: 1
```

## Background Services Status

All background services running without errors:
- ✅ `QrLoginCleanupService` - Tenant-aware with 10s startup delay
- ✅ `ParCleanupHostedService` - Tenant-aware with 10s startup delay
- ✅ `KeyRotationHostedService` - Tenant-aware with 10s startup delay
- ✅ `BackchannelLogoutDispatcher` - 10s startup delay
- ✅ `ExpiredTokenCleanupService` - Tenant-aware with startup delay

**Pattern Used:**
- Created `BackgroundServiceTenantHelper` to load default tenant and set context
- Added 10-second startup delays to avoid migration race conditions
- Services log warnings and skip iterations if default tenant not found

## Configuration

### Docker Compose (docker-compose.yml)
```yaml
webauth:
  environment:
    MultiTenancy__Enabled: "true"
    MultiTenancy__DefaultTenantSlug: "default"
```

### appsettings.json
```json
{
  "MultiTenancy": {
    "Enabled": true,
    "DefaultTenantSlug": "default"
  }
}
```

## Migration Compatibility

### For New Deployments
1. Set `MultiTenancy:Enabled` to `true` or `false` before first deployment
2. Run EF migrations (creates default tenant automatically)
3. Deploy and access via tenant-prefixed or root paths

### For Existing Deployments
1. Apply EF migration `AddMultiTenancySupport` (assigns existing data to default tenant)
2. **No changes required** - keep `MultiTenancy:Enabled = false` for current behavior
3. Optionally enable multi-tenant mode later
4. If enabled, existing routes continue to work via fallback mechanism

## Backward Compatibility Strategy

**Problem:** Existing clients may have hard-coded URLs like `https://auth.example.com/.well-known/openid-configuration`

**Solution:** 
- Fallback routes automatically map to default tenant
- No client changes required when enabling multi-tenant mode
- Clients can gradually migrate to tenant-prefixed routes

**Deprecation Path (Future):**
- Current: Both prefixed and fallback routes supported
- v2.0 (Future): Emit warnings for fallback route usage
- v3.0 (Future): Remove fallback routes, require tenant prefix

## Architecture Benefits

1. **Code Reuse:** Single `MapOidcEndpoints()` method used for both route patterns
2. **Flexibility:** Same codebase supports single-tenant and multi-tenant modes
3. **Backward Compatibility:** Existing deployments continue working without changes
4. **Clear Separation:** Tenant context resolved early in middleware pipeline
5. **Proper Error Handling:** Distinguishes between "tenant not found" vs "config error"

## Related Documentation

- `docs/multitenancy-backlog.md` - Full multi-tenancy implementation plan
- `docs/multitenancy-progress-2025-10-04.md` - Phase 1 progress summary
- `docs/copilot-instructions.md` - Architecture rules and conventions

## Next Steps

Phase 1 Multi-Tenancy is complete. Remaining work:
- ✅ ~~Multi-tenancy infrastructure~~ (Completed)
- ✅ ~~Database schema with TenantId~~ (Completed)
- ✅ ~~Mode-aware tenant resolution~~ (Completed)
- ✅ ~~Service layer filtering by TenantId~~ (Completed)
- ✅ ~~Mode-aware issuer construction~~ (Completed)
- ✅ ~~Multi-tenant routing~~ (Completed - this document)
- 🔄 Update JWKS endpoint to filter by tenant (Next priority)
- 🔄 Create admin UI for tenant management
- 🔄 Add platform admin role and routes
- 🔄 Implement tenant status management (active/suspended)
- 🔄 Add per-tenant feature flags and customization

## Files Modified

1. `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` - Routing logic
2. `MrWhoOidc.Auth/MultiTenancy/TenantResolver.cs` - Fallback logic
3. `MrWhoOidc.WebAuth/Middleware/TenantResolutionMiddleware.cs` - Error handling
4. `MrWhoOidc.UnitTests/MultiTenancy/TenantResolutionTests.cs` - Updated test expectations
5. `MrWhoOidc.WebAuth/Background/BackgroundServiceTenantHelper.cs` - New helper (2 versions)
6. `MrWhoOidc.WebAuth/Background/QrLoginCleanupService.cs` - Tenant context + delay
7. `MrWhoOidc.WebAuth/Infrastructure/ParCleanupHostedService.cs` - Tenant context + delay
8. `MrWhoOidc.Auth/Services/KeyRotationHostedService.cs` - Tenant context + delay
9. `MrWhoOidc.WebAuth/Background/BackchannelLogoutDispatcher.cs` - Startup delay
10. `MrWhoOidc.WebAuth/Infrastructure/ExpiredTokenCleanupService.cs` - Tenant context + delay

---

**Implementation completed successfully with all tests passing and Docker running without errors.**
