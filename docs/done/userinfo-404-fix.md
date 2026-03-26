# UserInfo 404 Error Fix

> Historical note: This incident write-up captures a historical failure mode. The URLs and middleware state described here are useful for debugging context, but current setup and runtime guidance lives in [README](../../README.md) and [docs/developer-guide.md](../developer-guide.md).

**Date:** October 8, 2025  
**Issue:** OIDC authentication failing with 404 when calling `/userinfo` endpoint  
**Root Cause:** `UseStatusCodePagesWithReExecute` middleware intercepting 401 DPoP nonce challenges

## Problem

The OpenID Connect client (`MrWhoOidc.Web`) was failing during authentication with this error:

```
System.Net.Http.HttpRequestException: Response status code does not indicate success: 404 (Not Found).
   at Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler.GetUserInformationAsync(...)
```

The client was correctly trying to reach `https://localhost:7208/userinfo`, but was receiving 404 instead of the expected 401 with DPoP nonce challenge.

## Root Cause Discovery

Investigation revealed the issue had **four parts**, discovered in sequence:

### Part 1: Incorrect Pipeline Ordering (Initial Red Herring)
In `MrWhoOidc.WebAuth/Program.cs`, the endpoint mapping was occurring **before** the middleware pipeline configuration. This was fixed but the 404 persisted.

### Part 2: Middleware Added After UseRouting (Second Issue)
Inside `MapMrWhoOidcEndpoints()`, there was a call to `app.Use()` that added middleware **after** `UseRouting()` had already been called. This was moved but the 404 persisted.

### Part 3: Additional Middleware After Endpoint Mapping (Third Issue)
`app.UseAutoSeed()` was being called after endpoint mapping. This was moved but the 404 persisted.

### Part 4: Status Code Pages Intercepting API Responses (TRUE ROOT CAUSE)
The **real culprit** was discovered by examining server logs:

```
info: MrWhoOidc.WebAuth.Handlers.UserInfoHandler[0]
      /userinfo nonce challenge issued to ::1
```

The `/userinfo` endpoint **was working** and correctly returning **401 Unauthorized** with DPoP nonce headers. However, `UseStatusCodePagesWithReExecute("/NotFound")` was intercepting ALL error status codes (including 401) and re-executing the pipeline for `/NotFound`, which doesn't exist for API calls, resulting in 404.

**The sequence was:**
1. Client calls `/userinfo` with DPoP-bound token
2. Server validates token and issues DPoP nonce challenge (returns 401)
3. `UseStatusCodePagesWithReExecute` intercepts the 401
4. Pipeline re-executes for `/NotFound`
5. `/NotFound` page doesn't handle API requests
6. Client receives 404 instead of 401

## Solution

### Critical Fix: Smart Status Code Page Handling
Modified status code pages middleware to **intelligently detect API responses** and leave them untouched:

**Before:**
```csharp
// PipelineExtensions.cs - WRONG: Intercepts ALL status codes
app.UseStatusCodePagesWithReExecute("/NotFound");
```

**After:**
```csharp
// PipelineExtensions.cs - CORRECT: Smart detection
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    var request = context.HttpContext.Request;
    
    // Don't intercept if response has protocol-specific headers
    if (response.Headers.ContainsKey("WWW-Authenticate") ||
        response.Headers.ContainsKey("DPoP-Nonce") ||
        response.ContentType?.StartsWith("application/json") == true)
    {
        return; // Let the original response through
    }
    
    // Don't intercept specific API paths
    if (request.Path.StartsWithSegments("/token") ||
        request.Path.StartsWithSegments("/userinfo") ||
        /* ... other API paths ... */)
    {
        return; // Let the original response through
    }
    
    // For user-facing pages, re-execute for NotFound page
    var originalPath = request.Path.Value ?? "/";
    request.Path = "/NotFound";
    request.QueryString = new QueryString($"?path={Uri.EscapeDataString(originalPath)}");
    await context.Next(context.HttpContext);
});
```

**This approach:**
- ✅ **Preserves API protocol compliance** (DPoP, OAuth errors, etc.)
- ✅ **Maintains pretty 404 pages** for user-facing routes
- ✅ **Auto-detects API responses** via headers (`WWW-Authenticate`, `DPoP-Nonce`, `application/json`)
- ✅ **Explicit path exclusions** for known API endpoints

### Additional Fixes (Pipeline Ordering)
These were necessary but not sufficient:

1. **Moved pipeline setup before endpoint mapping** in `Program.cs`
2. **Moved migration middleware** to run before `UseRouting()`
3. **Moved `UseAutoSeed()` middleware** before endpoint mapping

## Solution

### Step 1: Fix Pipeline Order
Swapped the order in `Program.cs`:

```csharp
// CORRECT ORDER:
app.UseMrWhoOidcPipeline(redisMux, migrationCompletionSource); 
app.MapMrWhoOidcEndpoints();
```

### Step 2: Move Migration Middleware (Critical Fix)
Moved the migration-waiting middleware from `MapMrWhoOidcEndpoints()` into `UseMrWhoOidcPipeline()` **before** `UseRouting()`:

**Before:**
```csharp
// EndpointMappingExtensions.cs - WRONG
public static void MapMrWhoOidcEndpoints(this WebApplication app)
{
    app.Use(async (context, next) => { ... }); // Added after UseRouting()
    // ... endpoint mapping
}
```

**After:**
```csharp
// PipelineExtensions.cs - CORRECT
public static WebApplication UseMrWhoOidcPipeline(
    this WebApplication app, 
    IConnectionMultiplexer? redisMux,
    TaskCompletionSource<bool> migrationCompletionSource)
{
    // Wait for migrations - BEFORE UseRouting()
    app.Use(async (context, next) => { ... });
    
    app.UseRouting(); // Now middleware is correctly ordered
    // ... rest of pipeline
}
```

### Step 3: Move AutoSeed Middleware (Final Fix)
Moved `app.UseAutoSeed()` to run **before** endpoint mapping:

**Before:**
```csharp
// Program.cs - WRONG
app.MapMrWhoOidcEndpoints();
app.UseAutoSeed();  // <-- After endpoint mapping
```

**After:**
```csharp
// Program.cs - CORRECT
app.UseMrWhoOidcPipeline(redisMux, migrationCompletionSource);
app.UseAutoSeed();  // <-- Before endpoint mapping
app.MapMrWhoOidcEndpoints();
```

## ASP.NET Core Pipeline Order (Critical Rules)

The correct middleware order is:

1. **Early middleware** (exception handling, forwarded headers, etc.)
2. **Custom middleware that doesn't depend on routing**
3. **`UseRouting()`** ← Critical boundary
4. **Middleware that uses routing** (authentication, authorization, CORS, session, etc.)
5. **`UseEndpoints()` or `Map*()` calls** ← Endpoint mapping

**FORBIDDEN ZONE:** Never call `app.Use()` or add custom middleware between step 3 (`UseRouting()`) and step 5 (endpoint mapping).

## Related Files Modified

1. **`MrWhoOidc.WebAuth/Program.cs`**
   - Swapped pipeline and endpoint mapping order
   - Added migration completion source parameter

2. **`MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs`**
   - Removed `app.Use()` middleware call
   - Added `GetMigrationCompletionSource()` method

3. **`MrWhoOidc.WebAuth/Infrastructure/Pipeline/PipelineExtensions.cs`**
   - Added migration-waiting middleware at the correct position
   - Added `migrationCompletionSource` parameter

## Why This Matters

This fix is critical because:

1. **DPoP Protocol Compliance**: DPoP nonce challenges **require** 401 status codes with specific headers. Converting to 404 breaks the protocol.

2. **OAuth/OIDC Error Handling**: All OAuth/OIDC endpoints must return proper HTTP status codes:
   - `401 Unauthorized` for authentication failures
   - `403 Forbidden` for authorization failures  
   - `400 Bad Request` for malformed requests
   - Status code page middleware breaks this contract

3. **Client Library Compatibility**: OIDC client libraries expect specific status codes and won't handle 404 correctly for authentication challenges.

## Related Files Modified

1. **`MrWhoOidc.WebAuth/Program.cs`** (Pipeline ordering fixes)
   - Moved `app.UseMrWhoOidcPipeline()` before `app.MapMrWhoOidcEndpoints()`
   - Moved `app.UseAutoSeed()` before endpoint mapping

2. **`MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs`**
   - Removed `app.Use()` middleware call from endpoint mapping

3. **`MrWhoOidc.WebAuth/Infrastructure/Pipeline/PipelineExtensions.cs`** (Critical fix)
   - Added migration-waiting middleware before `UseRouting()`
   - **Conditionally applied `UseStatusCodePagesWithReExecute` to exclude API endpoints**

## Testing

To verify the fix:

1. **Restart the WebAuth server** (critical - old build is cached):
   ```powershell
   # Stop old process if running
   Get-Process | Where-Object ProcessName -like "*WebAuth*" | Stop-Process -Force
   
   cd MrWhoOidc.WebAuth
   dotnet run
   ```

2. **Test the userinfo endpoint manually**:
   ```powershell
   # First, get a valid access token through the auth flow
   # Then test without DPoP proof (should get 401, not 404):
   Invoke-WebRequest -Uri "https://localhost:7208/userinfo" `
       -Headers @{ "Authorization" = "Bearer YOUR_ACCESS_TOKEN" } `
       -SkipHttpErrorCheck
   # Expected: 401 with DPoP-Nonce header
   ```

3. **Run the Web client** and complete an OIDC login flow - DPoP nonce challenge should be handled correctly.

## Prevention

To prevent similar issues in the future:

1. **Never apply `UseStatusCodePagesWithReExecute` globally** - always filter by path
2. **API/protocol endpoints must return raw HTTP status codes** - no transformation
3. **Test with actual clients** - server logs can be misleading (handler executed but response was intercepted)
4. **Document middleware order** and why specific endpoints are excluded
5. **Integration tests** should verify actual HTTP status codes, not just handler execution

## Key Learnings

1. **Server logs can be misleading**: The handler was executing correctly and returning 401, but middleware was transforming it to 404.

2. **Status code middleware is dangerous**: `UseStatusCodePagesWithReExecute` should almost never be used globally in applications with both pages and APIs.

3. **Pipeline order matters**: Even with correct ordering, middleware behavior can break protocol compliance.

4. **Test the full stack**: Always test from the client perspective, not just unit/integration tests of individual components.

## References

- [ASP.NET Core Status Code Pages](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling#usest atuscodepages)
- [DPoP RFC 9449](https://datatracker.ietf.org/doc/html/rfc9449)
- [OAuth 2.0 Error Responses](https://datatracker.ietf.org/doc/html/rfc6749#section-5.2)
