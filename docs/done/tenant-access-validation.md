# Tenant Access Validation Implementation

## Summary

Enhanced the `TenantResolutionMiddleware` to detect and prevent users from accessing tenants they don't belong to, automatically redirecting them to their correct tenant.

## Feature: Tenant Access Validation

### **How It Works**

When a user accesses a tenant-specific URL, the middleware now:

1. **Resolves the tenant** from the URL path (e.g., `/t/acme/Account`)
2. **Checks if user is authenticated**
3. **Looks up user's actual tenant** from the database
4. **Compares** requested tenant vs user's tenant
5. **If mismatch detected**:
   - Logs a warning with security context
   - Determines user's correct tenant
   - Redirects to the same page but in user's correct tenant
   - Preserves the query string

### **Example Scenarios**

#### **Scenario 1: User tries to access wrong tenant**
```
User "alice" belongs to tenant "acme"
User navigates to: /t/contoso/Account

Middleware detects:
- Requested tenant: "contoso"
- User's tenant: "acme"
- Mismatch! ⚠️

Action:
- Redirect to: /t/acme/Account
- Log warning with user ID and tenant IDs
```

#### **Scenario 2: User switches to wrong tenant via URL manipulation**
```
User "bob" belongs to tenant "default"
User manually types: /t/competitor-tenant/Admin/Clients

Middleware detects:
- Bob doesn't belong to "competitor-tenant"

Action:
- Redirect to: /t/default/Admin/Clients
- Preserves the intended page, just corrects the tenant
```

#### **Scenario 3: User accesses their correct tenant**
```
User "alice" belongs to tenant "acme"
User navigates to: /t/acme/Account

Middleware detects:
- No mismatch ✅

Action:
- Allow access
- Continue normal processing
```

### **Code Changes**

**File Modified**: `MrWhoOidc.WebAuth/Middleware/TenantResolutionMiddleware.cs`

**Added Logic**:
```csharp
// After tenant resolution, before continuing pipeline:
1. Check if user is authenticated
2. Query database for user's actual tenant ID
3. Compare with resolved tenant ID
4. If different:
   - Log warning with security context
   - Query user's correct tenant slug
   - Strip incorrect tenant prefix from path
   - Redirect to correct tenant with same page/query
```

### **Security Benefits**

1. **Prevents tenant data leakage**: Users can't access other tenants' data even if they know the tenant slug
2. **Automatic correction**: Users are seamlessly redirected to their correct tenant
3. **Audit trail**: All attempted cross-tenant access is logged with:
   - User ID
   - Requested tenant ID
   - User's actual tenant ID
   - Original path
   - Redirect path

4. **User experience**: Users don't see errors, just automatic correction to their proper context

### **Performance Considerations**

**Database Queries**:
- Only executes when user is authenticated
- Single query to check user's tenant: `SELECT TenantId FROM Users WHERE Id = @userId`
- If mismatch, additional query: `SELECT Slug FROM Tenants WHERE Id = @userTenantId`
- Queries are fast (indexed primary key lookups)

**Caching Opportunity** (future enhancement):
- User's tenant ID could be cached in claims during authentication
- Would eliminate the database query on every request
- Trade-off: Need to invalidate cache if user's tenant changes

### **Logging Examples**

**Warning when mismatch detected**:
```
User 12345678-1234-1234-1234-123456789012 attempted to access tenant 
aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa but belongs to different tenant 
bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb
```

**Info when redirecting**:
```
Redirecting user 12345678-1234-1234-1234-123456789012 from 
/t/wrong-tenant/Account to correct tenant path /t/correct-tenant/Account
```

**Debug for normal resolution**:
```
Resolved tenant: acme (ID: aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa, Mode: multi-tenant)
```

### **Edge Cases Handled**

1. **User not authenticated**: Skip validation, allow access
2. **User ID not found**: Skip validation (shouldn't happen with proper auth)
3. **User tenant not found in DB**: Skip validation (data integrity issue)
4. **Empty tenant ID**: Skip validation
5. **Query string preservation**: Maintains all query parameters during redirect
6. **Path normalization**: Correctly handles root path after stripping tenant prefix

### **Testing Recommendations**

1. **Test normal access**: User accesses their own tenant → should work
2. **Test wrong tenant**: User tries to access different tenant → should redirect
3. **Test query strings**: Redirect should preserve ?returnUrl= etc.
4. **Test nested paths**: `/t/wrong/Admin/Clients/Edit/123` → `/t/correct/Admin/Clients/Edit/123`
5. **Test platform admin**: Ensure platform admin routes still work (they skip this middleware)
6. **Test non-authenticated**: Non-authenticated users can still access public pages

### **Configuration**

No configuration needed. The feature is automatically active when:
- Multi-tenancy is enabled
- User is authenticated
- Request includes a tenant prefix in the path

### **Future Enhancements**

1. **Claims-based validation**: Store tenant ID in user claims during authentication
   - Eliminates database query on every request
   - Faster performance

2. **Platform admin bypass**: Allow platform admins to access any tenant
   - Add authorization policy check
   - Enable cross-tenant administration

3. **Tenant switching UI**: Provide UI for users with multi-tenant access
   - Some users may legitimately have access to multiple tenants
   - Allow explicit tenant selection
   - Store preference in session/cookie

4. **Audit events**: Emit structured audit events for:
   - Cross-tenant access attempts
   - Successful validations
   - Security monitoring and alerting

5. **Rate limiting**: Prevent brute-force tenant discovery
   - Limit redirects per user per time window
   - Block repeated attempts to access wrong tenants

## Related Components

- `TenantAwareRedirectMiddleware`: Redirects tenant-unaware URLs to tenant-specific versions
- `TenantResolutionMiddleware`: Resolves tenant from URL and validates user access
- `TenantAccessor`: Provides current tenant context to the application
- `NotFound` page: Displays tenant-aware 404 errors

## Documentation

See also:
- `docs/tenant-aware-redirect-implementation.md` - Tenant-aware URL handling
- `docs/multitenancy-quick-reference.md` - Multi-tenancy overview
- `docs/multitenancy-roadmap-october-2025.md` - Multi-tenancy roadmap
