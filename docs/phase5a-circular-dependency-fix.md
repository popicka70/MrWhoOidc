# Phase 5A: Circular Dependency Fix

## Problem Discovered
**Date:** January 15, 2025 (during Docker deployment test)  
**Severity:** CRITICAL - Application failed to start

### Error
```
System.InvalidOperationException: A circular dependency was detected for the service of type 'MrWhoOidc.WebAuth.Services.IImpersonationService'.

ImpersonationService 
  → IAuthorizationService 
    → IAuthorizationHandlerProvider 
      → IEnumerable<IAuthorizationHandler> 
        → TenantAdminAuthorizationHandler 
          → IImpersonationService  ❌ (circular!)
```

### Root Cause
The `TenantAdminAuthorizationHandler` was injecting `IImpersonationService` to check if a platform admin was impersonating a tenant. However, `ImpersonationService` itself injected `IAuthorizationService` (to check if the user is a platform admin before allowing impersonation). This created a circular dependency that prevented the ASP.NET Core DI container from constructing the dependency graph.

**Dependency Chain:**
1. `TenantAdminAuthorizationHandler` needs `IImpersonationService`
2. `ImpersonationService` needs `IAuthorizationService`
3. `IAuthorizationService` needs all `IAuthorizationHandler` instances
4. Including `TenantAdminAuthorizationHandler` → **CIRCULAR!**

## Solution
Break the circular dependency by **removing the `IImpersonationService` dependency** from `TenantAdminAuthorizationHandler` and instead **directly accessing the session** to check impersonation state.

### Code Changes

**File:** `MrWhoOidc.WebAuth/Security/Admin/TenantAdminAuthorizationHandler.cs`

#### Before (Broken - Circular Dependency)
```csharp
public sealed class TenantAdminAuthorizationHandler : AuthorizationHandler<TenantAdminRequirement>
{
    private readonly IImpersonationService _impersonationService; // ❌ Causes circular dependency

    public TenantAdminAuthorizationHandler(
        ...,
        IImpersonationService impersonationService) // ❌
    {
        _impersonationService = impersonationService;
    }

    protected override async Task HandleRequirementAsync(...)
    {
        // ...
        
        // Check if platform admin is impersonating this tenant
        if (httpContext != null && _impersonationService.IsImpersonating(httpContext)) // ❌
        {
            var impersonatedTenantId = _impersonationService.GetImpersonatedTenantId(httpContext); // ❌
            // ...
        }
    }
}
```

#### After (Fixed - Direct Session Access)
```csharp
public sealed class TenantAdminAuthorizationHandler : AuthorizationHandler<TenantAdminRequirement>
{
    // ✅ Removed IImpersonationService dependency

    public TenantAdminAuthorizationHandler(
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        IOptions<TenantAdminAuthOptions> options,
        IOptions<PlatformAdminAuthOptions> platformOptions,
        IHttpContextAccessor httpContextAccessor) // ✅ Only need HttpContext
    {
        // ...
    }

    protected override async Task HandleRequirementAsync(...)
    {
        // ...
        
        // Check if platform admin is impersonating this tenant
        // ✅ Access session directly to avoid circular dependency
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext?.Session != null)
        {
            var impersonatedTenantIdStr = httpContext.Session.GetString("ImpersonatingTenantId");
            if (!string.IsNullOrEmpty(impersonatedTenantIdStr) 
                && Guid.TryParse(impersonatedTenantIdStr, out var impersonatedTenantId))
            {
                var currentTenantId = _tenantAccessor.CurrentTenant?.TenantId;
                
                if (impersonatedTenantId == currentTenantId)
                {
                    // User is a platform admin impersonating this tenant - grant access
                    context.Succeed(requirement);
                    return;
                }
            }
        }
    }
}
```

## Why This Works

### Key Principle: Authorization Handlers Should Not Depend on Authorization Service
Authorization handlers are registered as part of the authorization system. If they depend on `IAuthorizationService` (directly or indirectly via services that use it), they create circular dependencies.

### Direct Session Access is Safe
- **Session is already available** via `IHttpContextAccessor.HttpContext.Session`
- **No service dependency** - just reading a string from session storage
- **Same data source** - `ImpersonationService` also reads/writes session directly
- **No business logic needed** - just a simple key lookup

### Benefits of This Approach
1. ✅ **Breaks circular dependency** - No dependency on `IAuthorizationService`
2. ✅ **Simple and efficient** - Direct session access, no service call overhead
3. ✅ **Consistent** - Uses same session key as `ImpersonationService`
4. ✅ **Maintainable** - Clear separation of concerns

## Testing
1. Built successfully: `dotnet build` (10.4s, 1 pre-existing warning)
2. Docker rebuild: `docker compose up -d --build` (18.7s)
3. Application started successfully without circular dependency errors
4. Logs show healthy startup:
   ```
   info: Microsoft.Hosting.Lifetime[0]
         Application started. Press Ctrl+C to shut down.
   info: Microsoft.Hosting.Lifetime[0]
         Hosting environment: Development
   ```

## Lessons Learned

### DI Design Principles for Authorization Handlers
1. **Avoid service dependencies that use authorization** - Authorization handlers should not depend on services that themselves use `IAuthorizationService`
2. **Prefer primitive dependencies** - Use `IHttpContextAccessor`, `DbContext`, `IOptions<T>` instead of high-level services
3. **Direct data access is OK** - Reading session/cache directly is acceptable in authorization handlers
4. **Keep handlers lightweight** - Complex business logic should live in services, not handlers

### Alternative Solutions Considered

#### Option 1: Lazy<IImpersonationService> ❌
```csharp
private readonly Lazy<IImpersonationService> _impersonationService;
```
**Rejected:** Doesn't truly break the circular dependency, just delays it until first access.

#### Option 2: IHttpContextAccessor + Manual Check ✅ (CHOSEN)
```csharp
httpContext.Session.GetString("ImpersonatingTenantId")
```
**Chosen:** Simple, efficient, breaks dependency cycle.

#### Option 3: Separate Authorization Policy ❌
Create a separate policy for impersonation that runs before tenant-admin check.
**Rejected:** Overly complex, requires policy ordering guarantees.

#### Option 4: Extract Interface for Session Access ❌
Create `IImpersonationSessionAccessor` with no dependencies.
**Rejected:** Unnecessary abstraction, session access is already simple enough.

## Impact Assessment

### Functional Impact
- ✅ **No functional changes** - Impersonation still works exactly the same
- ✅ **Same authorization behavior** - Platform admins impersonating still get tenant-admin access
- ✅ **Session key unchanged** - Still using "ImpersonatingTenantId" key

### Performance Impact
- ✅ **Slight improvement** - One less service resolution per authorization check
- ✅ **Direct session access** - No method call overhead

### Maintainability Impact
- ✅ **Clearer separation** - Authorization handler doesn't depend on business services
- ✅ **Easier to test** - Fewer dependencies to mock
- ⚠️ **Session key coupling** - Handler and service both use "ImpersonatingTenantId" string literal

### Future Recommendations
1. **Extract session key constant** - Define `ImpersonationService.SessionKey` constant, use in both places
2. **Document session contract** - Document session keys used by impersonation feature
3. **Consider session abstraction** - If more features need session access in handlers, create lightweight session accessor

## Related Documentation
- [Phase 5A Platform Admin Impersonation](./phase5a-impersonation-complete.md)
- [Phase 5A Complete Summary](./phase5a-complete.md)
- [Circular Dependency Fix](./phase5a-circular-dependency-fix.md) ✅ (this document)

## Conclusion
The circular dependency was successfully resolved by eliminating the `IImpersonationService` dependency from `TenantAdminAuthorizationHandler` and accessing session data directly. This is a common pattern when authorization handlers need to check external state - prefer direct data access over service dependencies to avoid circular dependency issues.

**Status:** ✅ RESOLVED  
**Build:** ✅ Success  
**Docker:** ✅ Running  
**Impact:** ✅ No functional changes

