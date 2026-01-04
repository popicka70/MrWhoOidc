# Tenant-Aware Redirect and 404 Implementation

## Summary

Implemented tenant-aware URL handling to ensure authenticated users always work within their tenant context, even when accessing tenant-unaware URLs.

## Changes Made

### 1. New TenantAwareRedirectMiddleware (`MrWhoOidc.WebAuth/Middleware/TenantAwareRedirectMiddleware.cs`)

**Purpose**: Automatically redirect authenticated users from tenant-unaware URLs to tenant-specific versions.

**Behavior**:
- Only active when multi-tenancy is enabled
- Only applies to authenticated users
- Looks up user's tenant from database
- Redirects from `/path` to `/t/{tenant-slug}/path`
- Skips platform admin routes, auth endpoints, static assets, etc.
- Root path `/` is now redirected to tenant-specific home

**Example**:
- User accesses `/Account` → Redirected to `/t/default/Account`
- User accesses `/Mfa` → Redirected to `/t/default/Mfa`
- User accesses `/` → Redirected to `/t/default/`

### 2. Tenant-Aware NotFound Page

**Files Created**:
- `MrWhoOidc.WebAuth/Pages/NotFound.cshtml`
- `MrWhoOidc.WebAuth/Pages/NotFound.cshtml.cs`

**Features**:
- Displays tenant context when available
- Shows requested path that wasn't found
- Provides tenant-aware "Return Home" and "My Account" links
- Returns HTTP 404 status code

### 3. Enhanced TenantResolutionMiddleware

**Updated**: `MrWhoOidc.WebAuth/Middleware/TenantResolutionMiddleware.cs`

**Changes**:
- When tenant slug in URL is invalid (e.g., `/t/invalid-tenant/page`)
- Attempts to determine user's tenant from authentication
- Redirects to tenant-specific NotFound page: `/t/{user-tenant}/NotFound`
- Fallback to plain 404 if user tenant cannot be determined

### 4. Pipeline Registration

**Updated**: `MrWhoOidc.WebAuth/Infrastructure/Pipeline/PipelineExtensions.cs`

**Change**:
- Added `app.UseTenantAwareRedirect()` after authentication/authorization
- Ensures users are redirected before endpoints are executed

## Architecture

```
Request Flow:
1. UseRouting()
2. UseTenantResolution() - Resolves tenant from URL
3. UseAuthentication()
4. UseAuthorization()
5. UseTenantAwareRedirect() - Redirects if needed ← NEW
6. Endpoints
```

## Skipped Paths

The following paths are NOT redirected (they remain tenant-unaware):
- `/health*` - Health checks
- `/platform-admin/*` - Platform administration
- `/_*` - Internal routes
- `/swagger/*` - API documentation
- `/api/platform/*` - Platform APIs
- Static assets (`/css/*`, `/js/*`, `/lib/*`, `/favicon.ico`)
- Authentication flows:
  - `/discovertenant`
  - `/selecttenant`
  - `/switchtenant`
  - `/startimpersonation`
  - `/stopimpersonation`

## User Experience

### Before
- User logs in to tenant "acme"
- Navigates to `/Account` 
- Sees platform-level page (wrong context)
- Links break tenant context

### After
- User logs in to tenant "acme"
- Navigates to `/Account`
- Automatically redirected to `/t/acme/Account`
- All links maintain tenant context
- If page doesn't exist: redirected to `/t/acme/NotFound`

## Database Queries

Both middlewares query the Users and Tenants tables:
```csharp
var user = await (from u in dbContext.Users
                  join t in dbContext.Tenants on u.TenantId equals t.Id
                  where u.Id.ToString() == userId
                  select new { u.TenantId, t.Slug })
    .FirstOrDefaultAsync();
```

**Performance**: Query is only executed when:
- User is authenticated
- Multi-tenancy is enabled
- Path doesn't have `/t/{slug}` prefix
- Path is not in skip list

## Testing Recommendations

1. **Test tenant-aware redirects**:
   - Log in as user in tenant "acme"
   - Access `/` → Should redirect to `/t/acme/`
   - Access `/Account` → Should redirect to `/t/acme/Account`
   - Access `/Mfa` → Should redirect to `/t/acme/Mfa`

2. **Test 404 handling**:
   - Log in as user in tenant "acme"
   - Access `/t/acme/NonExistentPage` → Should show NotFound page with tenant context
   - Access `/t/invalid-tenant/Page` → Should redirect to `/t/acme/NotFound`

3. **Test platform admin access**:
   - Access `/platform-admin/*` routes → Should NOT redirect

4. **Test non-authenticated access**:
   - Access `/` while logged out → Should NOT redirect

## Future Enhancements

1. **Caching**: Cache user tenant lookups to reduce database queries
2. **Tenant Selection**: For users with access to multiple tenants, provide tenant picker
3. **Custom 404 Pages**: Allow tenants to customize their NotFound page
4. **Audit Logging**: Log tenant context mismatches for security analysis
