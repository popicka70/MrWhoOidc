# Hybrid Cache Candidates – MrWhoOidc.WebAuth

## Overview

This document identifies database access patterns in **MrWhoOidc.WebAuth** and related services that are candidates for hybrid caching. Each candidate is analyzed for:
- **Access frequency** (how often the query runs)
- **Data volatility** (how frequently the data changes)
- **Performance impact** (how expensive the query is)
- **Cache benefit** (expected improvement from caching)

## Priority Levels

- **🔴 High**: Critical path queries with high frequency and low volatility
- **🟡 Medium**: Moderately frequent queries with measurable impact
- **🟢 Low**: Nice-to-have optimizations for less critical paths

---

## 🔴 High Priority Candidates

### 1. Client Metadata Lookups (`IClientStore.FindByClientIdAsync`)

**Location**: `MrWhoOidc.Auth/Services/ClientStore.cs`

**Current Pattern**:
```csharp
db.Clients.AsNoTracking().Where(c => c.ClientId == clientId).FirstOrDefaultAsync()
```

**Access Frequency**: Very High
- Called on every `/authorize`, `/token`, `/userinfo`, `/revoke`, `/introspect` request
- Called in TokenHandler, AuthorizeHandler, UserInfoHandler, RevocationHandler
- One of the most frequent DB queries in the system

**Data Volatility**: Very Low
- Client configurations rarely change after initial setup
- Changes are administrative actions (manual updates via admin UI)

**Performance Impact**: Medium
- Simple indexed query but executed thousands of times per second under load
- Network round-trip to PostgreSQL on every request

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(15),          // L2 (Redis)
    LocalCacheExpiration = TimeSpan.FromMinutes(5)  // L1 (memory)
};
```

**Cache Key Pattern**: `client:metadata:{clientId}`

**Tags**: `["clients", "client:{clientId}", "tenant:{tenantId}"]`

**Invalidation Strategy**:
- Invalidate on client update/delete (admin UI)
- Invalidate on client secret rotation
- Bulk invalidate by tenant on tenant operations

**Estimated Impact**: 30-50% reduction in database load for client lookups

---

### 2. User Lookups by Username/Email (`IUserService.FindByUsernameOrEmailAsync`)

**Location**: `MrWhoOidc.Auth/Services/UserService.cs`

**Current Pattern**:
```csharp
db.Users.AsNoTracking().Where(u => u.Username == username).FirstOrDefaultAsync()
db.Users.AsNoTracking().Where(u => u.NormalizedEmail == email).FirstOrDefaultAsync()
```

**Access Frequency**: Very High
- Called on every login attempt (LoginModel, LoginTotp)
- Called during external OIDC user provisioning
- Called by admin user management pages

**Data Volatility**: Low
- User profile data (username, email, password hash) changes infrequently
- Password changes should invalidate cache
- Email verification state changes occasionally

**Performance Impact**: High
- Multiple queries per login (username lookup + alternative email lookup)
- Involves string normalization and comparison
- Can cascade to role/client assignment queries

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(10),         // L2 (Redis)
    LocalCacheExpiration = TimeSpan.FromMinutes(2) // L1 (memory) - shorter for security
};
```

**Cache Key Pattern**: 
- `user:username:{tenantId}:{username}`
- `user:email:{tenantId}:{normalizedEmail}`
- `user:id:{userId}`

**Tags**: `["users", "user:{userId}", "tenant:{tenantId}"]`

**Invalidation Strategy**:
- Invalidate on user update (profile, password, email changes)
- Invalidate on user deletion/suspension
- Invalidate on MFA status change
- Short L1 TTL to balance performance and data freshness

**Estimated Impact**: 20-40% reduction in user lookup latency during login flows

---

### 3. Tenant Metadata (`TenantResolutionMiddleware` + `ITenantSettingsService`)

**Location**: 
- `MrWhoOidc.WebAuth/Middleware/TenantResolutionMiddleware.cs`
- `MrWhoOidc.Auth/Services/TenantSettingsService.cs`

**Current Pattern**:
```csharp
db.Tenants.FirstOrDefaultAsync(t => t.Slug == tenantSlug)
db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId)
```

**Access Frequency**: Extremely High
- Called on EVERY incoming request by TenantResolutionMiddleware
- Tenant slug resolution happens before any authorization
- Critical path for multi-tenant routing

**Data Volatility**: Very Low
- Tenant metadata (slug, name, issuer URI, status, branding) rarely changes
- Changes are rare administrative events

**Performance Impact**: Critical
- Blocks entire request pipeline
- Currently already has partial HybridCache implementation for user-to-tenant mapping
- Full tenant metadata caching would provide additional benefit

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromHours(1),            // L2 (Redis)
    LocalCacheExpiration = TimeSpan.FromMinutes(15) // L1 (memory)
};
```

**Cache Key Pattern**: 
- `tenant:slug:{slug}`
- `tenant:id:{tenantId}`
- `tenant:issuer:{issuerUri}`

**Tags**: `["tenants", "tenant:{tenantId}"]`

**Invalidation Strategy**:
- Invalidate on tenant update (branding, settings, status)
- Invalidate on tenant deletion/suspension
- Long TTL due to extremely low volatility

**Estimated Impact**: 40-60% reduction in tenant resolution overhead

**Note**: Partial implementation exists in `TenantResolutionMiddleware` for user-to-tenant slug mapping. Extend to full tenant metadata.

---

### 4. JWKS Public Key Sets (`PublicJwksCache`)

**Location**: `MrWhoOidc.WebAuth/Security/PublicJwksCache.cs`

**Current Pattern**:
```csharp
db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId)
db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Name == providerName)
db.IdentityProviderKeys.AsNoTracking().Where(k => k.IdentityProviderId == providerId).ToListAsync()
```

**Access Frequency**: High
- Called for JAR (JWT Authorization Request) validation
- Called for client assertion validation
- Called for provider JWKS endpoints
- Currently uses IMemoryCache

**Data Volatility**: Very Low
- Cryptographic keys rotate infrequently (weeks/months)
- Key publication events are rare and administratively triggered

**Performance Impact**: High
- Cryptographic key material queries
- Multiple DB joins for provider keys
- ETag computation adds overhead

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(30),         // L2 (Redis) - longer for keys
    LocalCacheExpiration = TimeSpan.FromMinutes(10) // L1 (memory)
};
```

**Cache Key Pattern**: 
- `jwks:client:{clientId}`
- `jwks:provider:{providerName}`
- `jwks:providers:all`

**Tags**: `["jwks", "client:{clientId}", "provider:{providerId}"]`

**Invalidation Strategy**:
- Invalidate on key rotation/publication
- Invalidate on client JWKS update (admin UI)
- Invalidate on provider key activation/deactivation
- Already has metrics via `IOidcMetrics` - preserve those

**Migration Notes**:
- **Currently uses IMemoryCache** - migrate to HybridCache
- Preserve existing invalidation methods (`InvalidateClient`, `InvalidateProvider`)
- Preserve ETag computation and metrics

**Estimated Impact**: 25-35% reduction in JWKS query overhead, especially for JAR-enabled flows

---

### 5. Signing Key Retrieval (`IKeyStore`, `ISigningService`)

**Location**: `MrWhoOidc.Auth/Services/KeyStore.cs`, `MrWhoOidc.Auth/Services/SigningService.cs`

**Current Pattern**:
```csharp
db.SigningKeys.AsNoTracking().Where(k => k.RetiredAt == null).OrderByDescending(k => k.CreatedAt).FirstOrDefaultAsync()
```

**Access Frequency**: Extremely High
- Called for EVERY token signature operation
- Called for EVERY ID token generation
- Called for authorization response signing (JARM)
- Called for /jwks endpoint generation

**Data Volatility**: Very Low
- Active signing keys change only during key rotation (rare event)
- Typically one active key at a time
- Key retirement is infrequent and administratively controlled

**Performance Impact**: Critical
- On critical path for token issuance
- Query includes ordering and filtering
- Executes on every token/authorize endpoint hit

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(30),         // L2 (Redis)
    LocalCacheExpiration = TimeSpan.FromMinutes(10) // L1 (memory)
};
```

**Cache Key Pattern**: `signing:key:active:{tenantId}`

**Tags**: `["signing-keys", "tenant:{tenantId}"]`

**Invalidation Strategy**:
- Invalidate on key rotation (KeyRotationService)
- Invalidate on key retirement
- Invalidate on manual key management operations

**Estimated Impact**: 50-70% reduction in database load for signing key retrieval

---

### 6. Authorization Code Lookups (`ITokenService.ExchangeAuthorizationCodeAsync`)

**Location**: `MrWhoOidc.Auth/Services/TokenService.cs`

**Current Pattern**:
```csharp
db.AuthorizationCodes.FirstOrDefaultAsync(c => c.Code == code)
```

**Access Frequency**: High
- Called on every `/token` request with `grant_type=authorization_code`
- Short-lived tokens (5-10 minutes) but high throughput

**Data Volatility**: High
- Authorization codes are single-use tokens
- Consumed immediately after creation
- Must support invalidation on use

**Performance Impact**: Medium
- Indexed lookup but frequent
- Includes related user/client data retrieval after code validation

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(5),          // L2 (Redis) - short for security
    LocalCacheExpiration = TimeSpan.FromMinutes(1) // L1 (memory) - very short
};
```

**Cache Key Pattern**: `authz:code:{codeHash}`

**Tags**: `["authz-codes", "client:{clientId}", "user:{userId}"]`

**Invalidation Strategy**:
- **Critical**: Remove from cache immediately on consumption
- Expire naturally after 5 minutes (code TTL)
- Consider adding cache-aside pattern with DB as source of truth for security

**Security Considerations**:
- Authorization codes are security-sensitive
- Must ensure cache invalidation on consumption to prevent replay
- Short TTL to limit replay window
- Consider L1-only caching to avoid distributed cache latency

**Estimated Impact**: 15-25% reduction in token exchange latency (but adds complexity)

**Note**: This is a security-sensitive candidate. Test thoroughly to ensure no code replay vulnerabilities.

---

## 🟡 Medium Priority Candidates

### 7. Refresh Token Validation (`ITokenService.RefreshAccessTokenAsync`)

**Location**: `MrWhoOidc.Auth/Services/TokenService.cs`

**Current Pattern**:
```csharp
db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Type == "refresh" && t.RevokedAt == null)
```

**Access Frequency**: Medium-High
- Called on every `/token` request with `grant_type=refresh_token`
- Depends on client refresh token usage patterns

**Data Volatility**: Medium
- Refresh tokens can be revoked (user logout, admin action)
- Rotation policies may issue new refresh tokens
- RevokedAt timestamp changes require cache invalidation

**Performance Impact**: Medium
- Indexed lookup with multiple conditions
- Involves token hash computation (SHA-256)

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(5),          // L2 (Redis)
    LocalCacheExpiration = TimeSpan.FromMinutes(1) // L1 (memory)
};
```

**Cache Key Pattern**: `refresh:token:{tokenHash}`

**Tags**: `["refresh-tokens", "user:{userId}", "client:{clientId}"]`

**Invalidation Strategy**:
- Invalidate on token revocation (/revoke endpoint)
- Invalidate on user logout
- Invalidate on refresh token rotation
- Invalidate on user account changes (password reset, suspension)

**Security Considerations**:
- Must invalidate cache on revocation to prevent token reuse
- Short TTL to balance performance and security
- Monitor for cache inconsistencies

**Estimated Impact**: 15-30% reduction in refresh token validation latency

---

### 8. Consent Records (`IConsentService`)

**Location**: `MrWhoOidc.Auth/Services/ConsentService.cs`

**Current Pattern**:
```csharp
db.Consents.AsNoTracking().Where(c => c.UserId == userId && c.ClientId == clientId).FirstOrDefaultAsync()
```

**Access Frequency**: Medium
- Called during `/authorize` flow when `RequireConsent=true`
- Frequency depends on client consent requirements

**Data Volatility**: Low
- Consent grants are long-lived
- Revocation is user-initiated or administrative

**Performance Impact**: Low-Medium
- Simple indexed query
- Only impacts consent-requiring flows

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(30),
    LocalCacheExpiration = TimeSpan.FromMinutes(10)
};
```

**Cache Key Pattern**: `consent:{userId}:{clientId}`

**Tags**: `["consents", "user:{userId}", "client:{clientId}"]`

**Invalidation Strategy**:
- Invalidate on consent revocation (user account page)
- Invalidate on client update if consent requirements change

**Estimated Impact**: 10-20% reduction in consent lookup overhead

---

### 9. Realm and Role Lookups (Token Claims Assembly)

**Location**: `MrWhoOidc.Auth/Services/TokenService.cs`

**Current Pattern**:
```csharp
db.Realms.AsNoTracking().Where(r => r.Id == client.RealmId).Select(r => r.Name).FirstOrDefaultAsync()
db.UserRealmRoleAssignments.Join(db.Roles, ...)
db.UserClientRoleAssignments.Join(db.Roles, ...)
```

**Access Frequency**: High
- Called on every access token generation
- Called on every ID token generation
- Multiple joins for role claims

**Data Volatility**: Low
- Realm names rarely change
- User role assignments change occasionally (admin actions)

**Performance Impact**: Medium
- Multiple queries and joins per token issuance
- Realm lookup is particularly cacheable

**Recommended Cache Configuration**:
```csharp
// Realm name caching
var realmOptions = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromHours(1),
    LocalCacheExpiration = TimeSpan.FromMinutes(30)
};

// User roles caching
var rolesOptions = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(15),
    LocalCacheExpiration = TimeSpan.FromMinutes(5)
};
```

**Cache Key Pattern**: 
- `realm:name:{realmId}`
- `user:roles:{userId}:{clientId}` (composite for realm + client roles)

**Tags**: `["realms", "roles", "user:{userId}", "client:{clientId}"]`

**Invalidation Strategy**:
- Realm: Invalidate on realm name change (rare)
- Roles: Invalidate on user role assignment/revocation
- Roles: Invalidate on role definition changes

**Estimated Impact**: 20-30% reduction in token claims assembly overhead

---

### 10. Identity Provider Metadata (External OIDC Flows)

**Location**: 
- `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcHandler.cs`
- `MrWhoOidc.WebAuth/Pages/Auth/Providers/Select.cshtml.cs`
- Admin pages for provider management

**Current Pattern**:
```csharp
db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Name == providerName && p.Enabled)
db.IdentityProviders.AsNoTracking().Where(p => p.Enabled).ToListAsync()
```

**Access Frequency**: Medium
- Called on provider selection page
- Called during external OIDC initiation
- Called during callback processing

**Data Volatility**: Very Low
- Provider configurations rarely change
- Enabling/disabling providers is administrative

**Performance Impact**: Medium
- Provider list queries can involve multiple providers
- Configuration JSON parsing adds overhead

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(30),
    LocalCacheExpiration = TimeSpan.FromMinutes(10)
};
```

**Cache Key Pattern**: 
- `idp:metadata:{providerName}`
- `idp:enabled:list:{tenantId}`

**Tags**: `["identity-providers", "provider:{providerId}", "tenant:{tenantId}"]`

**Invalidation Strategy**:
- Invalidate on provider configuration update
- Invalidate on provider enable/disable
- Invalidate list cache on any provider status change

**Estimated Impact**: 15-25% reduction in provider metadata lookup overhead

---

### 11. Client-Provider Mappings

**Location**: 
- `MrWhoOidc.WebAuth/Pages/Auth/Providers/Select.cshtml.cs`
- `MrWhoOidc.WebAuth/Pages/Admin/ProviderMappings/Index.cshtml.cs`

**Current Pattern**:
```csharp
db.ClientIdentityProviders.AsNoTracking()
    .Where(m => m.ClientId == clientId)
    .Join(db.IdentityProviders, ...)
```

**Access Frequency**: Medium
- Called on provider selection page
- Admin pages for mapping management

**Data Volatility**: Low
- Client-provider associations change infrequently
- Administrative configuration

**Performance Impact**: Low-Medium
- Join query with moderate complexity
- Only impacts federated login flows

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(20),
    LocalCacheExpiration = TimeSpan.FromMinutes(5)
};
```

**Cache Key Pattern**: `client:providers:{clientId}`

**Tags**: `["client-providers", "client:{clientId}", "tenant:{tenantId}"]`

**Invalidation Strategy**:
- Invalidate on mapping creation/deletion
- Invalidate on provider enable/disable

**Estimated Impact**: 10-20% reduction in provider selection page latency

---

### 12. Claim Mappings for External Providers

**Location**: `MrWhoOidc.WebAuth/Pages/Admin/ProviderClaimMappings/Index.cshtml.cs`

**Current Pattern**:
```csharp
db.IdentityProviderClaimMappings.AsNoTracking()
    .Where(m => m.IdentityProviderId == providerId)
    .ToListAsync()
```

**Access Frequency**: Medium
- Called during external OIDC callback processing
- Called during claim transformation
- Admin pages for mapping management

**Data Volatility**: Low
- Claim mapping rules change infrequently
- Administrative configuration

**Performance Impact**: Low-Medium
- List query per provider
- Executed during user provisioning

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(30),
    LocalCacheExpiration = TimeSpan.FromMinutes(10)
};
```

**Cache Key Pattern**: `provider:claim-mappings:{providerId}`

**Tags**: `["claim-mappings", "provider:{providerId}"]`

**Invalidation Strategy**:
- Invalidate on mapping creation/update/deletion
- Admin UI should trigger invalidation

**Estimated Impact**: 10-15% reduction in external login claim transformation overhead

---

## 🟢 Low Priority Candidates

### 13. Scope Metadata (Discovery Endpoint)

**Location**: `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs`

**Current Pattern**:
```csharp
db.Scopes.AsNoTracking().Where(s => s.IsExposed).Select(s => s.Name).ToArray()
```

**Access Frequency**: Low
- Called on `/.well-known/openid-configuration` requests
- Typically cached by clients

**Data Volatility**: Very Low
- Scope definitions rarely change

**Performance Impact**: Low
- Simple query but executed on discovery endpoint

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromHours(1),
    LocalCacheExpiration = TimeSpan.FromMinutes(30)
};
```

**Cache Key Pattern**: `discovery:scopes:{tenantId}`

**Tags**: `["discovery", "scopes", "tenant:{tenantId}"]`

**Invalidation Strategy**:
- Invalidate on scope creation/deletion/exposure changes

**Estimated Impact**: 5-10% reduction in discovery endpoint latency

---

### 14. Client Scope Assignments

**Location**: `MrWhoOidc.Auth/Services/AuthorizeService.cs`

**Current Pattern**:
```csharp
db.ClientScopes.AsNoTracking()
    .Where(cs => cs.ClientId == clientId)
    .Select(cs => cs.ScopeName)
    .ToListAsync()
```

**Access Frequency**: Medium
- Called during `/authorize` scope validation
- Only for clients with restricted scopes

**Data Volatility**: Low
- Scope assignments change during client configuration

**Performance Impact**: Low-Medium
- Filtered list query
- Could be combined with client metadata cache

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(15),
    LocalCacheExpiration = TimeSpan.FromMinutes(5)
};
```

**Cache Key Pattern**: `client:scopes:{clientId}`

**Tags**: `["client-scopes", "client:{clientId}"]`

**Invalidation Strategy**:
- Invalidate on client scope assignment changes
- Could be embedded in client metadata cache (Candidate #1)

**Estimated Impact**: 5-15% reduction in authorize scope validation overhead

**Note**: Consider combining with Client Metadata cache to reduce complexity.

---

### 15. Pushed Authorization Request (PAR) Lookups

**Location**: `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs`

**Current Pattern**:
```csharp
db.PushedAuthorizationRequests.FirstOrDefaultAsync(p => p.RequestUri == requestUri && !p.Consumed)
```

**Access Frequency**: Low-Medium
- Only for clients using PAR
- PAR adoption varies by deployment

**Data Volatility**: High
- Short-lived (typically 60-90 seconds)
- Single-use tokens

**Performance Impact**: Low
- Simple indexed query
- Short TTL limits caching benefit

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromSeconds(90),         // Match PAR TTL
    LocalCacheExpiration = TimeSpan.FromSeconds(30)
};
```

**Cache Key Pattern**: `par:request:{requestUri}`

**Tags**: `["par", "client:{clientId}"]`

**Invalidation Strategy**:
- Remove on consumption
- Expire naturally after TTL

**Estimated Impact**: 5-10% reduction in PAR-enabled authorize flow latency

**Note**: Similar security considerations to authorization codes (single-use, security-sensitive).

---

### 16. Backchannel Logout Notification Queries (Admin UI)

**Location**: 
- `MrWhoOidc.WebAuth/Pages/Admin/Backchannel/Index.cshtml.cs`
- `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/AdminApiEndpointMappingExtensions.cs`

**Current Pattern**:
```csharp
db.BackchannelLogoutNotifications.AsNoTracking()
    .Where(n => n.Status == status)
    .OrderByDescending(n => n.CreatedAt)
    .ToListAsync()
```

**Access Frequency**: Low
- Admin UI and monitoring endpoints
- Not on critical user-facing paths

**Data Volatility**: High
- Notification status changes during dispatch retries
- Queue-like behavior

**Performance Impact**: Low
- Admin-only queries
- Pagination helps with large datasets

**Recommended Cache Configuration**:
```csharp
// Not recommended for caching due to high volatility and low access frequency
// If needed, use very short TTL:
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromSeconds(30),
    LocalCacheExpiration = TimeSpan.FromSeconds(10)
};
```

**Estimated Impact**: Minimal (admin UI latency reduction only)

**Note**: Low priority due to admin-only access and high data volatility.

---

### 17. Logout Redirect References

**Location**: `MrWhoOidc.WebAuth/Handlers/Logout/LogoutRedirectResolver.cs`

**Current Pattern**:
```csharp
db.LogoutRedirectReferences.FirstOrDefaultAsync(r => r.Id == refId)
```

**Access Frequency**: Low
- Only for logout flows using opaque redirect references
- Short-lived (typically 60 seconds)

**Data Volatility**: High
- Single-use references
- Marked as `Used` immediately

**Performance Impact**: Low
- Simple indexed lookup
- Short TTL limits caching benefit

**Recommended Cache Configuration**:
```csharp
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromSeconds(60),
    LocalCacheExpiration = TimeSpan.FromSeconds(20)
};
```

**Cache Key Pattern**: `logout:redirect-ref:{refId}`

**Tags**: `["logout-refs", "client:{clientId}"]`

**Invalidation Strategy**:
- Remove immediately on use
- Expire naturally after TTL

**Estimated Impact**: <5% reduction in logout redirect resolution latency

---

### 18. User-Tenant Assignment Lookups (Admin UI)

**Location**: Multiple admin pages (Users, Clients, Roles management)

**Current Pattern**:
```csharp
db.Users.AsNoTracking().Where(u => u.TenantId == tenantId).ToListAsync()
db.Tenants.AsNoTracking().Where(t => t.Status == TenantStatus.Active).ToListAsync()
```

**Access Frequency**: Low
- Admin UI list pages
- Filtered/paginated queries

**Data Volatility**: Low-Medium
- User creation/deletion
- Tenant status changes

**Performance Impact**: Low
- Paginated queries help limit impact
- Admin-only paths

**Recommended Cache Configuration**:
```csharp
// Typically not worth caching due to pagination and filtering variations
// If caching, use page-specific keys:
var options = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(2),
    LocalCacheExpiration = TimeSpan.FromMinutes(1)
};
```

**Estimated Impact**: Minimal (admin UI only)

**Note**: Query variations (search, filters, pagination) make caching complex. Consider query result caching only for frequently accessed admin views.

---

## Implementation Recommendations

### Phase 1: Critical Path (Weeks 1-2)
1. **Client Metadata** (Candidate #1) - Highest impact
2. **Tenant Metadata** (Candidate #3) - Already partially implemented
3. **Signing Key Retrieval** (Candidate #5) - Critical for token operations

### Phase 2: High-Traffic Flows (Weeks 3-4)
4. **User Lookups** (Candidate #2) - Login flow optimization
5. **JWKS Caching Migration** (Candidate #4) - Replace IMemoryCache
6. **Realm/Role Lookups** (Candidate #9) - Token claims optimization

### Phase 3: Secondary Optimizations (Weeks 5-6)
7. **Refresh Token Validation** (Candidate #7)
8. **Consent Records** (Candidate #8)
9. **Identity Provider Metadata** (Candidate #10)

### Phase 4: Nice-to-Have (As Needed)
10. Remaining medium and low priority candidates based on monitoring data

---

## Common Invalidation Patterns

### By Entity Type
- **Client changes**: Invalidate `client:*`, `client-scopes:*`, `client-providers:*`, `jwks:client:*`
- **User changes**: Invalidate `user:*`, `consent:*`, `refresh:token:*` for user
- **Tenant changes**: Invalidate `tenant:*`, all child entities
- **Provider changes**: Invalidate `idp:*`, `jwks:provider:*`, `client-providers:*`
- **Key rotation**: Invalidate `signing:key:*`, `jwks:*`

### Admin UI Integration
Add cache invalidation calls in:
- `Pages/Admin/Clients/Edit.cshtml.cs` → Client metadata
- `Pages/Admin/Users/Edit.cshtml.cs` → User metadata
- `Pages/Admin/Providers/Edit.cshtml.cs` → Provider metadata
- `Pages/Admin/Settings.cshtml.cs` → Tenant settings
- Background workers (KeyRotationService, etc.)

---

## Monitoring and Metrics

### Key Metrics to Track
1. **Cache Hit Ratio**: Target >80% for high-priority candidates
2. **Database Query Count**: Should decrease by 30-50% overall
3. **P95 Latency**: Token endpoint, authorize endpoint, userinfo endpoint
4. **Cache Invalidation Rate**: Track to identify thrashing
5. **Memory Usage**: L1 cache growth in each instance

### Alerting Thresholds
- Cache hit ratio drops below 60%
- Database query rate increases unexpectedly
- L1 cache memory usage exceeds configured limits

### Tools
- Use existing `IOidcMetrics` infrastructure
- Add HybridCache-specific metrics:
  - `cache.hit.count` (by entity type)
  - `cache.miss.count` (by entity type)
  - `cache.invalidation.count` (by reason)

---

## Testing Strategy

### Unit Tests
- Test cache key generation
- Test cache invalidation logic
- Test factory methods with null/empty results

### Integration Tests
- Verify cache population on first access
- Verify cache hit on subsequent access
- Verify cache invalidation triggers work correctly
- Test multi-instance consistency (L2 cache with Redis)

### Load Tests
- Measure database query reduction under load
- Measure latency improvements
- Identify cache stampede scenarios (should be prevented by HybridCache)

### Security Tests
- Verify sensitive data (authorization codes, refresh tokens) invalidates correctly
- Test cache isolation between tenants
- Verify no cross-tenant cache leakage

---

## Security Considerations

### Sensitive Data
- **Authorization Codes**: Very short TTL, immediate invalidation on use
- **Refresh Tokens**: Short L1 TTL, immediate invalidation on revocation
- **User Credentials**: Never cache password hashes directly
- **Client Secrets**: Never cache plaintext secrets

### Tenant Isolation
- Always include `tenantId` in cache keys where applicable
- Use tags to support bulk tenant invalidation
- Test cross-tenant cache isolation

### PII and GDPR
- Audit what user data is cached
- Implement data retention policies matching DB
- Support right-to-be-forgotten via cache invalidation

---

## Redis Configuration Notes

### Existing Redis Setup
- Already configured in `MrWhoOidc.WebAuth/Program.cs`
- Used for DPoP replay cache, JAR replay cache, rate limiting
- Connection string: `ConnectionStrings:redis` in appsettings

### HybridCache L2 Backend
- HybridCache automatically uses Redis when `IDistributedCache` is registered
- No additional configuration needed
- L1 (memory) cache provides sub-millisecond access
- L2 (Redis) cache provides multi-instance consistency

### Fallback Behavior
- HybridCache operates in L1-only mode if Redis is unavailable
- Graceful degradation for single-instance deployments
- Monitor Redis connection health

---

## Example Implementation: Client Metadata Cache

```csharp
// Before (MrWhoOidc.Auth/Services/ClientStore.cs)
public Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
{
    var query = db.Clients.AsNoTracking().Where(c => c.ClientId == clientId);
    if (tenantAccessor.CurrentTenant != null)
    {
        query = query.Where(c => c.TenantId == tenantAccessor.CurrentTenant.TenantId);
    }
    return query.FirstOrDefaultAsync(ct);
}

// After (with HybridCache)
public async Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
{
    var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;
    var cacheKey = $"client:metadata:{tenantId}:{clientId}";
    
    var options = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromMinutes(15),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };
    
    var tags = new List<string> 
    { 
        "clients", 
        $"client:{clientId}", 
        $"tenant:{tenantId}" 
    };
    
    return await _cache.GetOrCreateAsync(
        cacheKey,
        async cancel =>
        {
            var query = db.Clients.AsNoTracking().Where(c => c.ClientId == clientId);
            if (tenantAccessor.CurrentTenant != null)
            {
                query = query.Where(c => c.TenantId == tenantAccessor.CurrentTenant.TenantId);
            }
            return await query.FirstOrDefaultAsync(cancel);
        },
        options,
        tags,
        ct
    );
}

// Invalidation (in Pages/Admin/Clients/Edit.cshtml.cs)
public async Task<IActionResult> OnPostAsync(int id)
{
    // ... update logic ...
    
    await db.SaveChangesAsync();
    
    // Invalidate cache
    var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;
    await _cache.RemoveAsync($"client:metadata:{tenantId}:{client.ClientId}");
    
    // Or bulk invalidate by tag
    // await _cache.RemoveByTagAsync($"client:{client.ClientId}");
    
    return RedirectToPage("./Index");
}
```

---

## References

- [Hybrid Cache Guide](./hybrid-cache-guide.md) - Usage patterns and best practices
- [Hybrid Cache Setup Complete](./hybrid-cache-setup-complete.md) - Initial setup documentation
- [Microsoft Learn: HybridCache](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid)
- Existing implementations:
  - `TenantResolutionMiddleware` - User-to-tenant slug mapping
  - `PublicJwksCache` - JWKS caching with IMemoryCache (migration candidate)

---

## Change Log

| Date       | Author | Changes                          |
|------------|--------|----------------------------------|
| 2025-01-10 | AI     | Initial backlog creation         |

---

## Next Steps

1. Review and prioritize candidates with team
2. Implement Phase 1 candidates (critical path)
3. Add metrics and monitoring for cache hit rates
4. Document invalidation patterns in code comments
5. Update admin UI to trigger cache invalidations
6. Create integration tests for caching behavior
7. Monitor database query reduction in staging
8. Gradually roll out Phase 2 and beyond
