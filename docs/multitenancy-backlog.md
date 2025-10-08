# Multi-Tenancy Implementation Backlog

## Executive Summary

This document outlines the architectural changes and implementation roadmap to transform MrWhoOidc.WebAuth from a single-tenant OIDC Provider into a full multi-tenant SaaS authorization server. The solution will support isolated tenant contexts with per-tenant configuration, branding, user bases, client applications, and independent administration.

**Status:** Phase 1 - COMPLETE ✅ | Phase 2 - Ready to Start  
**Created:** October 4, 2025  
**Updated:** October 8, 2025  
**Target:** Production-ready multi-tenant OIDC Provider

**Phase 1 Complete - Foundation & Core Features:** ✅ 100%
- ✅ Configuration infrastructure (MultiTenancyOptions, appsettings)
- ✅ Tenant entity and TenantStatus enum
- ✅ TenantId added to all entities with FK constraints
- ✅ Tenant resolution infrastructure (ITenantResolver, TenantAccessor, TenantContext)
- ✅ Middleware created and registered (TenantResolutionMiddleware)
- ✅ EF Core migration created with default tenant seed data
- ✅ Services registered in DI container
- ✅ Service layer updated (8 services now filter by TenantId)
- ✅ Mode-aware issuer builder implemented and integrated
- ✅ All issuer construction logic updated (GetIssuer extension methods)
- ✅ Migration applied and tested in Docker
- ✅ Multi-tenant routing pattern implemented (tenant-prefixed + fallback routes)
- ✅ Background services made tenant-aware (5 services updated)
- ✅ All 331 tests passing
- ✅ JWKS endpoint tenant filtering implemented and tested
- ✅ Platform Admin UI implemented (tenant CRUD, dashboard, impersonation)
- ✅ User Self-Service Portal complete (8 pages: dashboard, profile, sessions, consents, linked accounts, emails, password, MFA)

**Next: Phase 2 - Branding & Customization** 🎨

**Implementation Decision:** Path-based tenant identification (`/t/{tenant-slug}/...`) selected as the primary strategy. Subdomain and custom domain options documented for future consideration but not in current scope.

**Key Feature: Mode Toggle** 🔄  
The solution supports both **single-tenant** and **multi-tenant** operational modes via a simple configuration flag (`MultiTenancy:Enabled`). This allows the same codebase to serve:
- **Enterprise self-hosted deployments** (single-tenant mode: no tenant prefix, root issuer)
- **IdP chaining scenarios** (single-tenant mode: simpler deployment, no multi-tenant overhead)
- **SaaS/multi-organization deployments** (multi-tenant mode: path-based tenants, platform admin)

Mode can be switched via configuration without code changes, though switching from multi → single tenant requires coordination to avoid token invalidation.

---

## 1. Multi-Tenancy Architecture Overview

### 1.1 Design Principles

1. **Tenant Isolation**: Complete data separation between tenants at the database and application layer
2. **Issuer per Tenant**: Each tenant has a unique issuer URI using path-based routing (e.g., `https://auth.example.com/t/{tenant-slug}`)
3. **Shared Infrastructure**: Single deployment serves all tenants with shared components (JWKS rotation, background workers, etc.)
4. **Tenant Context Resolution**: Early pipeline middleware resolves tenant from request path (`/t/{slug}`)
5. **Per-Tenant Configuration**: Independent OIDC settings, branding, policies, and feature flags
6. **Hierarchical Administration**: Platform admins manage tenants; tenant admins manage their realm's clients/users

### 1.2 Tenant Identification Strategies

**Decision**: Implement **Option A: Path-based routing** as the primary tenant identification strategy.

**Option A: Path-based** ✅ **SELECTED FOR IMPLEMENTATION**
- Pattern: `https://auth.example.com/t/{tenant-slug}/...`
- Example: `https://auth.example.com/t/acme-corp/.well-known/openid-configuration`
- Pros: 
  - No DNS/cert setup required; works immediately
  - Simple implementation; no infrastructure dependencies
  - Clear tenant scoping in URLs
  - Easy to test and debug
  - Compatible with any hosting environment
- Cons: 
  - Longer URLs; tenant visible in path
  - Less "white-label" appearance

**Future Options** (not in current scope, for reference only):

**Option B: Subdomain-based** 📋 *Future consideration*
- Pattern: `https://{tenant-slug}.auth.example.com/...`
- Example: `https://acme-corp.auth.example.com/.well-known/openid-configuration`
- Pros: Clean URLs; natural isolation feel
- Cons: Requires wildcard DNS and wildcard or per-tenant certs
- Could be added in a future phase if demand exists

**Option C: Custom domain** 📋 *Future consideration*
- Pattern: `https://auth.acme-corp.com/...`
- Example: `https://auth.acme-corp.com/.well-known/openid-configuration`
- Pros: Complete branding control; tenant-owned domain
- Cons: Requires DNS delegation, cert provisioning, admin complexity
- Enterprise feature for future consideration

### 1.3 Multi-Tenant Mode Toggle

**Feature Flag**: `MultiTenancy:Enabled` (boolean, default: `false`)

The server supports two operational modes:

**Single-Tenant Mode** (`MultiTenancy:Enabled = false`):
- Behaves as current implementation (no tenant prefix in URLs)
- All data implicitly belongs to a "default" tenant in the database
- Issuer: `https://auth.example.com` (no `/t/{slug}` prefix)
- Discovery: `https://auth.example.com/.well-known/openid-configuration`
- Suitable for: Self-hosted enterprise deployments, IdP chaining, simple use cases
- No tenant resolution overhead
- Platform admin UI hidden or disabled

**Multi-Tenant Mode** (`MultiTenancy:Enabled = true`):
- Full multi-tenant capabilities with path-based routing
- Tenant resolution middleware active
- Issuer: `https://auth.example.com/t/{tenant-slug}`
- Discovery: `https://auth.example.com/t/{slug}/.well-known/openid-configuration`
- Suitable for: SaaS deployments, hosting multiple organizations
- Platform admin UI available at `/platform-admin`
- Tenant admin UI at `/t/{slug}/admin`

**Mode Switching:**
- Mode can be changed via configuration without code changes
- Database schema remains the same (all entities have `TenantId`)
- Switching from single → multi requires data migration (assign existing data to tenants)
- Switching from multi → single requires selecting a single tenant to keep active

### 1.4 Issuer Construction

Each tenant must have a stable, unique issuer URI that matches the discovery endpoint location.

**Multi-Tenant Mode** (path-based issuer format):

```
Tenant Issuer = {base-url}/t/{tenant-slug}
```

Example:
- `https://auth.example.com/t/acme-corp`
- `https://auth.example.com/t/contoso`
- `https://localhost:5001/t/default` (local development)

**Single-Tenant Mode** (root issuer format):

```
Issuer = {base-url}
```

Example:
- `https://auth.example.com`
- `https://idp.mycompany.com`
- `https://localhost:5001` (local development)

**Issuer Construction Logic:**

```csharp
public static string BuildIssuer(HttpContext http, IMultiTenancyOptions options, string? tenantSlug = null)
{
    var scheme = http.Request.Scheme;
    var host = http.Request.Host.ToUriComponent();
    var baseUrl = $"{scheme}://{host}";
    
    if (!options.Enabled)
    {
        // Single-tenant mode: root issuer
        return baseUrl;
    }
    
    // Multi-tenant mode: path-based issuer
    if (string.IsNullOrEmpty(tenantSlug))
    {
        throw new InvalidOperationException("Tenant slug required in multi-tenant mode");
    }
    
    return $"{baseUrl}/t/{tenantSlug}";
}
```

**Critical**: 
- Issuer must be consistent across all token issuance and validation; changing it invalidates all existing tokens
- Mode should be set at deployment time and rarely changed
- Changing from single → multi tenant mode requires re-issuing all tokens

---

## 2. Data Model Changes

### 2.1 New Entity: Tenant

```csharp
public class Tenant
{
    public Guid Id { get; set; }
    
    // Identification
    [MaxLength(100)]
    public string Slug { get; set; } = string.Empty; // URL-safe identifier, unique
    
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty; // Display name
    
    [MaxLength(500)]
    public string? Description { get; set; }
    
    // Issuer configuration
    [MaxLength(500)]
    public string IssuerUri { get; set; } = string.Empty; // Computed as {base}/t/{slug}
    
    // Status and lifecycle
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    
    public DateTimeOffset? SuspendedAt { get; set; }
    
    public DateTimeOffset? DeletedAt { get; set; } // Soft delete
    
    // Branding
    [MaxLength(200)]
    public string? LogoUrl { get; set; }
    
    [MaxLength(50)]
    public string? PrimaryColor { get; set; }
    
    [MaxLength(50)]
    public string? AccentColor { get; set; }
    
    // Configuration overrides (JSON)
    [MaxLength(4000)]
    public string? SettingsJson { get; set; } // Per-tenant OIDC/Auth/QR settings
    
    // Limits and quotas
    public int MaxUsers { get; set; } = 10000;
    
    public int MaxClients { get; set; } = 100;
    
    public int MaxIdentityProviders { get; set; } = 10;
    
    // Contact and billing
    [MaxLength(256)]
    public string? AdminEmail { get; set; }
    
    [MaxLength(100)]
    public string? BillingPlan { get; set; } // Free, Starter, Pro, Enterprise
    
    public DateTimeOffset? TrialEndsAt { get; set; }
    
    // Metadata
    [MaxLength(2000)]
    public string? MetadataJson { get; set; } // Extensibility: custom fields, integrations
}

public enum TenantStatus
{
    Active = 1,
    Suspended = 2,      // Temporary disable (billing issue, abuse)
    PendingSetup = 3,   // Newly created, not yet ready
    Deleted = 4         // Soft deleted
}
```

### 2.2 Modify Existing Entities

**Design Decision:** All entities get `TenantId` column regardless of mode to maintain schema compatibility.

All entities that are tenant-specific must add a `TenantId` foreign key:

**Entities requiring TenantId:**
- `User` – users belong to one tenant
- `Client` – clients belong to one tenant (already has `RealmId`; `Realm` must also have `TenantId`)
- `Realm` – realms belong to one tenant
- `IdentityProvider` – IdPs configured per tenant
- `Role` – roles scoped to tenant+realm
- `Scope` – scopes can be global OR tenant-specific (add nullable `TenantId`)
- `SigningKey` – keys can be tenant-specific OR global platform keys
- `AuthorizationCode`, `Token`, `Consent` – all tenant-scoped via relationships
- `PushedAuthorizationRequest` – tenant-scoped
- `BackchannelLogoutNotification` – tenant-scoped
- `QrLoginSession` – tenant-scoped
- `Registration` – new user registrations scoped to tenant

**Entities remaining global (platform-level):**
- `DataProtectionKey` – shared across all tenants
- `Tenant` – platform entity itself
- `PlatformAdministrator` – platform admins (if using separate table approach)

**Behavior by Mode:**

**Single-Tenant Mode:**
- A "default" tenant is auto-created during migration
- All queries automatically use default tenant's ID
- `TenantId` filter still applied but always resolves to same value
- No tenant selection UI visible
- Platform admin features disabled/hidden

**Multi-Tenant Mode:**
- Tenant resolved from request path (`/t/{slug}`)
- `TenantId` filter uses resolved tenant
- Platform admin features enabled
- Tenant selection/management UI available

**Example changes to `User`:**

```csharp
public class User
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; } // NEW: FK to Tenant
    
    // ... existing fields ...
}
```

**Example changes to `Realm`:**

```csharp
public class Realm
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; } // NEW: FK to Tenant
    
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
    
    // ... existing fields ...
}
```

**Example changes to `SigningKey`:**

```csharp
public class SigningKey
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; } // NEW: Nullable; null = platform-wide key
    
    // ... existing fields ...
}
```

### 2.3 Indexes and Constraints

Add indexes for tenant-based queries:

```csharp
// In OnModelCreating:
modelBuilder.Entity<User>(b =>
{
    b.HasIndex(x => new { x.TenantId, x.Username }).IsUnique();
    b.HasIndex(x => new { x.TenantId, x.NormalizedEmail }).IsUnique();
    // Remove non-tenant unique indexes on Username/Email
});

modelBuilder.Entity<Client>(b =>
{
    b.HasIndex(x => new { x.TenantId, x.ClientId }).IsUnique();
    // ClientId can be reused across tenants
});

modelBuilder.Entity<Realm>(b =>
{
    b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
});

modelBuilder.Entity<Tenant>(b =>
{
    b.HasIndex(x => x.Slug).IsUnique();
    b.HasIndex(x => x.CustomDomain).IsUnique().HasFilter("[CustomDomain] IS NOT NULL");
});
```

### 2.4 Migration Strategy

**Phase 1: Schema addition (backward-compatible)**
1. Add `Tenants` table
2. Add nullable `TenantId` columns to existing entities
3. Seed a default "platform" tenant (slug: `default`)
4. Backfill `TenantId` = default tenant for all existing rows
5. Make `TenantId` NOT NULL after backfill

**Phase 2: Constraints and cleanup**
1. Add foreign key constraints
2. Update unique indexes to include `TenantId`
3. Drop old non-tenant-scoped indexes

---

## 3. Tenant Context Resolution

### 3.1 Mode-Aware Configuration

```csharp
public interface IMultiTenancyOptions
{
    bool Enabled { get; }
    string DefaultTenantSlug { get; }
}

public class MultiTenancyOptions : IMultiTenancyOptions
{
    public bool Enabled { get; set; } = false; // Default to single-tenant mode
    public string DefaultTenantSlug { get; set; } = "default";
}
```

Configuration in `appsettings.json`:

```json
{
  "MultiTenancy": {
    "Enabled": false,
    "DefaultTenantSlug": "default"
  }
}
```

### 3.2 Middleware: TenantResolutionMiddleware

Early in the request pipeline (before authentication), resolve the tenant context and store it in `HttpContext.Items` or a scoped service.

**Mode-aware implementation:**

```csharp
public class TenantContext
{
    public Tenant Tenant { get; set; } = null!;
    public string ResolvedIssuer { get; set; } = string.Empty;
    public bool IsSingleTenantMode { get; set; }
}

public interface ITenantResolver
{
    Task<TenantContext?> ResolveTenantAsync(HttpContext context);
}

public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMultiTenancyOptions _multiTenancyOptions;
    
    public TenantResolutionMiddleware(RequestDelegate next, IMultiTenancyOptions multiTenancyOptions)
    {
        _next = next;
        _multiTenancyOptions = multiTenancyOptions;
    }
    
    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver)
    {
        TenantContext? tenantContext;
        
        if (!_multiTenancyOptions.Enabled)
        {
            // Single-tenant mode: always use default tenant
            tenantContext = await resolver.ResolveDefaultTenantAsync(context);
        }
        else
        {
            // Multi-tenant mode: resolve from path
            tenantContext = await resolver.ResolveTenantAsync(context);
        }
        
        if (tenantContext == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync(_multiTenancyOptions.Enabled 
                ? "Tenant not found" 
                : "Service not configured");
            return;
        }
        
        if (tenantContext.Tenant.Status != TenantStatus.Active)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsync("Service is not available");
            return;
        }
        
        context.Items["TenantContext"] = tenantContext;
        await _next(context);
    }
}
```

### 3.3 Tenant Resolver Implementation

**Mode-aware resolver with single-tenant and multi-tenant support:**

```csharp
public class ModeAwareTenantResolver : ITenantResolver
{
    private readonly AuthDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IMultiTenancyOptions _options;
    
    public ModeAwareTenantResolver(
        AuthDbContext db, 
        IMemoryCache cache, 
        IMultiTenancyOptions options)
    {
        _db = db;
        _cache = cache;
        _options = options;
    }
    
    /// <summary>
    /// Resolve default tenant for single-tenant mode or fallback scenarios
    /// </summary>
    public async Task<TenantContext?> ResolveDefaultTenantAsync(HttpContext context)
    {
        var cacheKey = $"tenant:default:{_options.DefaultTenantSlug}";
        
        if (_cache.TryGetValue<Tenant>(cacheKey, out var cachedTenant))
        {
            return BuildTenantContext(context, cachedTenant, isSingleTenantMode: true);
        }
        
        var tenant = await _db.Tenants
            .Where(t => t.Slug == _options.DefaultTenantSlug && t.Status == TenantStatus.Active)
            .FirstOrDefaultAsync();
        
        if (tenant == null)
        {
            return null;
        }
        
        _cache.Set(cacheKey, tenant, TimeSpan.FromMinutes(30));
        return BuildTenantContext(context, tenant, isSingleTenantMode: true);
    }
    
    /// <summary>
    /// Resolve tenant from path in multi-tenant mode: /t/{slug}/...
    /// </summary>
    public async Task<TenantContext?> ResolveTenantAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        
        // Multi-tenant mode: extract from /t/{slug}/...
        if (!path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback to default tenant if path doesn't match pattern
            // This maintains backward compatibility with non-prefixed routes
            return await ResolveDefaultTenantAsync(context);
        }
        
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !segments[0].Equals("t", StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveDefaultTenantAsync(context);
        }
        
        var slug = segments[1].ToLowerInvariant();
        
        // Check cache first
        var cacheKey = $"tenant:{slug}";
        if (_cache.TryGetValue<Tenant>(cacheKey, out var cachedTenant))
        {
            return BuildTenantContext(context, cachedTenant, isSingleTenantMode: false);
        }
        
        // Look up tenant in database
        var tenant = await _db.Tenants
            .Where(t => t.Slug == slug && t.Status == TenantStatus.Active)
            .FirstOrDefaultAsync();
        
        if (tenant == null)
        {
            return null;
        }
        
        // Cache for 5 minutes
        _cache.Set(cacheKey, tenant, TimeSpan.FromMinutes(5));
        
        return BuildTenantContext(context, tenant, isSingleTenantMode: false);
    }
    
    private TenantContext BuildTenantContext(HttpContext http, Tenant tenant, bool isSingleTenantMode)
    {
        var scheme = http.Request.Scheme;
        var host = http.Request.Host.ToUriComponent();
        
        string issuer;
        if (isSingleTenantMode || !_options.Enabled)
        {
            // Single-tenant mode: root issuer (no /t/{slug} prefix)
            issuer = $"{scheme}://{host}";
        }
        else
        {
            // Multi-tenant mode: path-based issuer
            issuer = $"{scheme}://{host}/t/{tenant.Slug}";
        }
        
        return new TenantContext
        {
            Tenant = tenant,
            ResolvedIssuer = issuer,
            IsSingleTenantMode = isSingleTenantMode
        };
    }
}
```

**Key Features:**
- **Single-tenant mode**: Always resolves to default tenant, uses root issuer
- **Multi-tenant mode**: Extracts tenant from `/t/{slug}` path, uses prefixed issuer
- **Backward compatibility**: Falls back to default tenant if path doesn't match `/t/{slug}` pattern (enables gradual migration)
- **Caching**: Default tenant cached for 30 minutes (rarely changes), path-based tenants cached for 5 minutes
- **Performance**: Single DB query per tenant per cache window
**Cache Invalidation:**
- When tenant is updated/suspended/deleted, clear cache entry
- Use distributed cache (Redis) in multi-server deployments

**Future Extensions:**
If subdomain or custom domain support is added later, implement additional resolvers and use a composite pattern to try multiple strategies.

### 3.4 Tenant-Scoped DbContext

Provide a scoped `ITenantAccessor` service that injects the resolved tenant into services and queries:

```csharp
public interface ITenantAccessor
{
    Guid TenantId { get; }
    Tenant Tenant { get; }
    bool IsSingleTenantMode { get; }
}

public class TenantAccessor : ITenantAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    
    public Guid TenantId => Tenant.Id;
    
    public Tenant Tenant => 
        (_httpContextAccessor.HttpContext?.Items["TenantContext"] as TenantContext)?.Tenant
        ?? throw new InvalidOperationException("No tenant context available");
    
    public bool IsSingleTenantMode =>
        (_httpContextAccessor.HttpContext?.Items["TenantContext"] as TenantContext)?.IsSingleTenantMode
        ?? false;
}
```

All queries in services should automatically filter by `TenantId`:

```csharp
// Example in ClientStore:
public async Task<Client?> GetClientAsync(string clientId)
{
    var tenantId = _tenantAccessor.TenantId;
    return await _db.Clients
        .Where(c => c.TenantId == tenantId && c.ClientId == clientId)
        .FirstOrDefaultAsync();
}
```

**Optional:** Use EF Core query filters to auto-inject tenant filter:

```csharp
modelBuilder.Entity<User>().HasQueryFilter(u => u.TenantId == _tenantId);
```

But be careful with admin queries that need cross-tenant visibility.

---

## 4. Issuer Management

### 4.1 Dynamic Issuer Injection

Replace the single static `OidcOptions.Issuer` with per-tenant issuer resolution:

```csharp
public static class IssuerExtensions
{
    public static string GetTenantIssuer(this HttpContext http)
    {
        var tenantContext = http.Items["TenantContext"] as TenantContext;
        return tenantContext?.ResolvedIssuer 
            ?? throw new InvalidOperationException("No tenant issuer available");
    }
}
```

Update all issuer references in:
- `DiscoveryHandler` (`.well-known/openid-configuration`)
- `TokenService` (JWT `iss` claim)
- `AuthorizeHandler`, `TokenHandler`, `UserInfoHandler`, `IntrospectionHandler`
- `LogoutHandler` (backchannel logout token `iss`)

### 4.2 JWKS per Tenant

Each tenant can have its own signing keys OR share platform keys (configurable):

**Option 1: Per-tenant keys** (recommended for isolation)
- Filter `SigningKeys` by `TenantId`
- JWKS endpoint: `/t/{slug}/.well-known/jwks.json`
- Key rotation scoped to tenant

**Option 2: Shared platform keys**
- Use `SigningKey` with `TenantId = NULL`
- All tenants use same JWKS
- Simpler but less isolated; key compromise affects all tenants

**Hybrid approach**: Platform provides default keys; tenants can optionally upload custom keys.

### 4.3 Discovery Endpoint per Tenant

Discovery must reflect tenant-specific configuration:

```json
{
  "issuer": "https://auth.example.com/t/acme-corp",
  "authorization_endpoint": "https://auth.example.com/t/acme-corp/authorize",
  "token_endpoint": "https://auth.example.com/t/acme-corp/token",
  "jwks_uri": "https://auth.example.com/t/acme-corp/.well-known/jwks.json",
  ...
}
```

Update `DiscoveryHandler` to:
1. Resolve tenant context
2. Build tenant-prefixed endpoints
3. Return tenant-specific capabilities (e.g., if tenant disables QR login, omit from discovery)

---

## 5. Tenant Provisioning and Lifecycle

### 5.1 Tenant Creation Flow

**Admin-initiated** (platform admin creates tenant):
1. Platform admin navigates to `/platform-admin/tenants/create`
2. Fills form: slug, name, admin email, billing plan
3. System validates slug uniqueness
4. System creates `Tenant` record
5. System seeds default realm (`default` realm for new tenant)
6. System optionally creates first tenant admin user
7. System sends onboarding email with tenant URL and credentials

**Self-service signup** (future enhancement):
1. User visits `/signup` or platform landing page
2. User fills signup form: company name, email, desired slug
3. System validates slug availability
4. System creates `Tenant` + default realm + first admin user in pending state
5. System sends email verification
6. User verifies email → tenant activated
7. User redirected to tenant-specific admin UI

### 5.2 Tenant Setup Wizard

After tenant creation, guide tenant admin through setup:

1. **Profile**: Set logo, branding colors, company info
2. **First Client**: Create an OIDC client (wizard for common scenarios: SPA, web app, mobile)
3. **Identity Providers**: Optionally configure external IdPs (Google, Azure AD)
4. **Users**: Invite initial users or configure user registration settings
5. **Settings**: Review OIDC policies (PKCE, PAR, consent, etc.)

### 5.3 User Self-Service Portal

**Separate from Admin UI**: Regular users access their own self-service portal, not admin functions.

**Multi-Tenant Mode:**
- User portal at `/t/{slug}/account` or `/t/{slug}/profile`

**Single-Tenant Mode:**
- User portal at `/account` or `/profile`

**User Self-Service Capabilities (non-admin users):**
- View and edit profile (name, email, alternative emails)
- Change password
- Enable/disable MFA (TOTP)
- Manage TOTP devices (view QR code, regenerate secret)
- View active sessions
- Revoke sessions/tokens
- View consent history (apps they've authorized)
- Revoke consent for specific clients
- View linked external identities (Google, Azure AD, etc.)
- Unlink external identities (if allowed by policy)
- Delete account (if allowed by policy)

**Access Control:**
- Any authenticated user can access their own profile
- Users can only see/modify their own data
- No access to other users, clients, realms, or admin functions

**UI Location:**
- Completely separate from admin UI (different route prefix)
- Lighter theme/simpler navigation (user-focused, not administrative)

### 5.4 Tenant Administration

**Separate from User Portal**: Tenant admins access administrative functions for managing the tenant.

**Multi-Tenant Mode:**
- Admin portal at `/t/{slug}/admin` (protected by admin role check)

**Single-Tenant Mode:**
- Admin portal at `/admin` (protected by admin role check)

**Access Control:**
- **Role-based**: User must have `tenant-admin` role in the tenant's `default` realm
- Middleware enforces role check before rendering admin UI
- Unauthorized users redirected to `/account` (their profile) or login

**Tenant Admin Capabilities (admin users only):**

**User Management:**
- View all users in tenant
- Create new users (invite via email or direct creation)
- Edit user profiles (name, email, roles, realm assignments)
- Reset user passwords
- Suspend/unsuspend users
- Delete users
- Assign roles to users
- View user audit logs

**Client Management:**
- View all clients (OIDC/OAuth2 applications)
- Create new clients (wizard or advanced form)
- Edit client settings (redirect URIs, policies, secrets, JWKS, etc.)
- View client secrets (with "show" toggle)
- Regenerate client secrets
- Delete clients
- View client usage metrics (token issuance, active users)

**Realm and Role Management:**
- View all realms in tenant
- Create new realms
- Edit realm settings
- Create/edit/delete roles (realm-scoped or client-scoped)
- Assign roles to users

**Identity Provider Management:**
- Configure external IdPs (Google, Azure AD, SAML, etc.)
- Test IdP connections
- View IdP claim mappings
- Enable/disable IdPs

**Settings and Configuration:**
- Customize branding (logo, colors, login page text) - multi-tenant mode only
- Configure OIDC policies (PKCE, consent, PAR, etc.)
- Configure authentication policies (password strength, MFA requirements)
- Configure user registration settings (open/closed, email verification)

**Audit and Monitoring:**
- View audit logs (tenant-scoped)
- View usage metrics (active users, token issuance, API calls)
- View recent logins, failed attempts
- Export audit logs

**Tenant Admin Users:**
- **Role-based approach** (recommended): Use existing role system
  - Role: `tenant-admin` in the tenant's `default` realm
  - Check via `User.Roles.Any(r => r.Name == "tenant-admin" && r.RealmId == defaultRealmId)`
- **Alternative**: Add `IsTenantAdmin` boolean flag to `User` entity
  - Simpler but less flexible (can't grant admin to specific realms)
- **Middleware**: `RequireTenantAdminAttribute` or policy check before admin routes

### 5.5 Tenant Suspension and Deletion

**Suspension** (reversible):
- Triggered by billing failure, abuse, or admin action
- Set `Tenant.Status = Suspended`
- Middleware returns 503 for suspended tenants
- Existing sessions/tokens remain valid but new auth requests fail
- Admin can unsuspend via platform admin UI

**Soft Deletion** (recoverable):
- Set `Tenant.DeletedAt = Now`, `Status = Deleted`
- Tenant hidden from list but data retained
- Grace period (e.g., 30 days) before hard delete
- Admin can restore during grace period

**Hard Deletion** (permanent):
- Background job purges all tenant data after grace period
- Cascade deletes all related entities (users, clients, tokens, etc.)
- Irreversible; warn prominently in UI

---

## 6. Platform Administration

### 6.1 Platform Admin UI

**Multi-Tenant Mode Only**: Platform admin area at `/platform-admin`

**Access Control:**
- Platform admins are NOT tenant-scoped
- New entity: `PlatformAdministrator` OR special user flag `IsPlatformAdmin`
- Middleware: Check `User.IsPlatformAdmin` before allowing `/platform-admin` access
- In single-tenant mode, `/platform-admin` returns 404 or redirects to `/admin`

**Platform Admin Capabilities:**
- View all tenants (list, search, filter by status)
- Create new tenants
- Suspend/unsuspend/delete tenants
- View cross-tenant metrics (total users, tenants, token volume)
- Manage platform-wide settings (default limits, feature flags)
- Toggle multi-tenancy mode (with warnings about token invalidation)
- View audit logs (all tenants)
- Access tenant admin UIs (impersonate tenant admin for support)

### 6.2 Platform Admin Entities

**Option A: Separate table**

```csharp
public class PlatformAdministrator
{
    public Guid Id { get; set; }
    [MaxLength(200)]
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
```

**Option B: Flag on User** (simpler)

```csharp
public class User
{
    // ... existing fields ...
    public bool IsPlatformAdmin { get; set; } = false; // NEW
}
```

Recommendation: Use Option A for better separation; platform admins should not belong to any tenant.

### 6.3 Tenant Impersonation (Support Mode)

Allow platform admins to access a tenant's admin UI for troubleshooting:

1. Platform admin clicks "Impersonate" on tenant list
2. System creates temporary session scoped to that tenant
3. Platform admin sees tenant admin UI as if they were a tenant admin
4. Banner displays "Support Mode: Viewing {TenantName}"
5. Audit log records impersonation start/end

---

## 7. Routing and URL Structure

### 7.1 Mode-Aware Routing

**Single-Tenant Mode** (`MultiTenancy:Enabled = false`):

All endpoints at root level (no tenant prefix):

```
Platform routes:
  GET  /health                        Health check (global)
  GET  /metrics                       Metrics (auth required)

OIDC protocol routes:
  GET  /.well-known/openid-configuration   Discovery
  GET  /.well-known/jwks.json              JWKS
  GET  /authorize                          Authorization endpoint
  POST /token                              Token endpoint
  GET  /userinfo                           UserInfo endpoint
  POST /introspect                         Introspection endpoint
  POST /revoke                             Revocation endpoint
  POST /par                                PAR endpoint
  GET  /logout                             Logout (RP-initiated)
  GET  /connect/endsession                 Logout (alternative)

User-facing UI routes:
  GET  /login                              Login page
  GET  /consent                            Consent page
  GET  /register                           User registration (if enabled)
  GET  /account/*                          User self-service portal (profile, MFA, etc.)
  GET  /profile                            User profile (alias for /account)

Admin UI routes (tenant admin only):
  GET  /admin/*                            Tenant admin UI (role-protected)
  GET  /admin/users                        User management
  GET  /admin/clients                      Client management
  GET  /admin/realms                       Realm management
  GET  /admin/providers                    Identity provider configuration
  GET  /admin/settings                     Tenant settings
  GET  /admin/audit                        Audit logs
```

**Multi-Tenant Mode** (`MultiTenancy:Enabled = true`):

All tenant-specific endpoints prefixed with `/t/{slug}`:

```
Platform routes (no tenant context):
  GET  /platform-admin/*              Platform admin UI (platform admin only)
  GET  /health                        Health check (global)
  GET  /metrics                       Metrics (aggregated, auth required)
  GET  /                              Landing page (tenant selector or marketing)

OIDC protocol routes (tenant-scoped):
  GET  /t/{slug}/.well-known/openid-configuration   Discovery
  GET  /t/{slug}/.well-known/jwks.json              JWKS
  GET  /t/{slug}/authorize                          Authorization endpoint
  POST /t/{slug}/token                              Token endpoint
  GET  /t/{slug}/userinfo                           UserInfo endpoint
  POST /t/{slug}/introspect                         Introspection endpoint
  POST /t/{slug}/revoke                             Revocation endpoint
  POST /t/{slug}/par                                PAR endpoint
  GET  /t/{slug}/logout                             Logout (RP-initiated)
  GET  /t/{slug}/connect/endsession                 Logout (alternative)

User-facing UI routes (tenant-scoped):
  GET  /t/{slug}/login                              Login page
  GET  /t/{slug}/consent                            Consent page
  GET  /t/{slug}/register                           User registration (if enabled)
  GET  /t/{slug}/account/*                          User self-service portal
  GET  /t/{slug}/profile                            User profile (alias)

Admin UI routes (tenant admin only, role-protected):
  GET  /t/{slug}/admin/*                            Tenant admin UI
  GET  /t/{slug}/admin/users                        User management
  GET  /t/{slug}/admin/clients                      Client management
  GET  /t/{slug}/admin/realms                       Realm management
  GET  /t/{slug}/admin/providers                    Identity provider configuration
  GET  /t/{slug}/admin/settings                     Tenant settings
  GET  /t/{slug}/admin/branding                     Branding customization
  GET  /t/{slug}/admin/audit                        Audit logs

Backward compatibility (fallback to default tenant, deprecated):
  GET  /.well-known/openid-configuration   → Uses default tenant
  GET  /authorize                          → Uses default tenant
  POST /token                              → Uses default tenant
  GET  /account/*                          → Uses default tenant
  GET  /admin/*                            → Uses default tenant (if admin)
  ...
```

### 7.2 Access Control Summary

**Three Levels of Access:**

1. **Regular Users** (authenticated):
   - OIDC protocol endpoints (authorize, token, userinfo, etc.)
   - User self-service portal: `/account/*` or `/t/{slug}/account/*`
   - Can only access their own data
   - No access to admin UI

2. **Tenant Administrators** (authenticated + `tenant-admin` role):
   - Everything regular users can access
   - Tenant admin UI: `/admin/*` or `/t/{slug}/admin/*`
   - Can manage users, clients, realms, IdPs within their tenant
   - Cannot access other tenants or platform admin

3. **Platform Administrators** (separate entity, multi-tenant mode only):
   - Platform admin UI: `/platform-admin/*`
   - Can manage all tenants, view cross-tenant metrics
   - Can impersonate tenant admins for support
   - Not scoped to any specific tenant

**Middleware Stack:**

```csharp
// User self-service routes
app.MapGroup("/account")
   .RequireAuthorization(); // Any authenticated user

// Tenant admin routes
app.MapGroup("/admin")
   .RequireAuthorization("TenantAdminPolicy"); // Requires tenant-admin role

// Platform admin routes (multi-tenant mode only)
app.MapGroup("/platform-admin")
   .RequireAuthorization("PlatformAdminPolicy"); // Requires platform admin
```

### 7.3 Authorization Policies

**TenantAdminPolicy:**
```csharp
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("TenantAdminPolicy", policy => 
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("tenant-admin"); // Or custom claim check
    });
```

**PlatformAdminPolicy:**
```csharp
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("PlatformAdminPolicy", policy => 
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("is_platform_admin", "true"); // Or check PlatformAdministrator table
    });
```

### 7.4 Future: Subdomain-Based Routing (Not in Current Scope)

If subdomain-based routing is added in the future:

```
Example: acme-corp.auth.example.com

User routes (any authenticated user):
  GET  /account/*                     User self-service portal
  GET  /profile                       User profile

Admin routes (tenant admin only):
  GET  /admin/*                       Tenant admin UI
  
OIDC protocol routes:
  GET  /.well-known/openid-configuration   Discovery
  GET  /authorize                          Authorization endpoint
  POST /token                              Token endpoint
  ...
```

Tenant is resolved from subdomain instead of path prefix.

### 7.5 Route Registration Strategy

```csharp
var app = builder.Build();

var multiTenancyOptions = app.Services.GetRequiredService<IMultiTenancyOptions>();

if (multiTenancyOptions.Enabled)
{
    // Multi-tenant mode: register tenant-prefixed routes
    var tenantGroup = app.MapGroup("/t/{slug}");
    tenantGroup.MapOidcEndpoints();
    tenantGroup.MapGroup("/account").MapUserSelfServiceEndpoints()
        .RequireAuthorization(); // Any authenticated user
    tenantGroup.MapGroup("/admin").MapTenantAdminEndpoints()
        .RequireAuthorization("TenantAdminPolicy"); // Tenant admin only
    
    // Fallback to default tenant for backward compatibility
    app.MapGroup("").MapOidcEndpoints(); 
    app.MapGroup("/account").MapUserSelfServiceEndpoints()
        .RequireAuthorization();
    app.MapGroup("/admin").MapTenantAdminEndpoints()
        .RequireAuthorization("TenantAdminPolicy");
    
    // Platform admin (multi-tenant mode only)
    app.MapGroup("/platform-admin").MapPlatformAdminEndpoints()
        .RequireAuthorization("PlatformAdminPolicy");
}
else
{
    // Single-tenant mode: register only root routes
    app.MapOidcEndpoints();
    app.MapGroup("/account").MapUserSelfServiceEndpoints()
        .RequireAuthorization(); // Any authenticated user
    app.MapGroup("/admin").MapTenantAdminEndpoints()
        .RequireAuthorization("TenantAdminPolicy"); // Tenant admin only
}
```

---

```
Example: acme-corp.auth.example.com

  GET  /.well-known/openid-configuration
  GET  /authorize
  POST /token
  ...
```

Middleware detects subdomain and resolves tenant accordingly.

### 7.3 Custom Domain Routing (Phase 3)

Tenant brings their own domain (e.g., `auth.acme-corp.com`):

1. Tenant configures DNS CNAME: `auth.acme-corp.com → auth.example.com`
2. Platform provisions SSL cert (Let's Encrypt, ACME protocol)
3. Middleware looks up tenant by `Host` header
4. Routes behave as subdomain mode (no prefix)

**Challenges:**
- Wildcard cert won't work; need per-domain certs
- Cert provisioning automation (use ACME client, store in DB or Key Vault)
- SNI routing to serve correct cert

---

## 8. Mode Switching and Migration

### 8.1 Switching from Single-Tenant to Multi-Tenant Mode

**Scenario:** Organization starts with single-tenant deployment and wants to add additional tenants.

**Prerequisites:**
1. All existing data already assigned to default tenant (via `TenantId` column)
2. Database schema includes all multi-tenant entities and indexes
3. Application code is mode-aware

**Migration Steps:**

1. **Preparation (pre-switch):**
   ```sql
   -- Verify all data is assigned to default tenant
   SELECT COUNT(*) FROM "Users" WHERE "TenantId" IS NULL;
   SELECT COUNT(*) FROM "Clients" WHERE "TenantId" IS NULL;
   -- Should return 0 for all tenant-scoped tables
   ```

2. **Create additional tenants:**
   ```sql
   -- Can be done before or after switching mode
   INSERT INTO "Tenants" ("Id", "Slug", "Name", "IssuerUri", "Status", "CreatedAt")
   VALUES 
     (gen_random_uuid(), 'acme-corp', 'Acme Corporation', 
      'https://auth.example.com/t/acme-corp', 1, NOW()),
     (gen_random_uuid(), 'contoso', 'Contoso Ltd', 
      'https://auth.example.com/t/contoso', 1, NOW());
   ```

3. **Update configuration:**
   ```json
   {
     "MultiTenancy": {
       "Enabled": true,
       "DefaultTenantSlug": "default"
     }
   }
   ```

4. **Restart application**

5. **Update RPs (Relying Parties):**
   - **Existing RPs (default tenant)**: Can continue using root URLs (fallback behavior) OR update to `/t/default/` prefix
   - **New RPs**: Configure with tenant-specific issuer (`https://auth.example.com/t/{slug}`)

6. **Gradual migration (recommended):**
   - Phase 1: Enable multi-tenant mode, leave existing RPs on root URLs
   - Phase 2: Create new tenants, onboard new clients to tenant-specific URLs
   - Phase 3: Migrate existing RPs to `/t/default/` prefix (optional)
   - Phase 4: Deprecate root URL fallback in future version

**Impact:**
- ✅ **No token invalidation** if existing RPs continue using root URLs (issuer remains `https://auth.example.com`)
- ✅ **Backward compatible** via fallback to default tenant
- ⚠️ **Platform admin UI appears** at `/platform-admin` (may need access control update)
- ⚠️ **Tenant prefix available** for default tenant at `/t/default/*` (parallel to root)

### 8.2 Switching from Multi-Tenant to Single-Tenant Mode

**Scenario:** SaaS provider wants to offer isolated deployment to enterprise customer.

**Prerequisites:**
1. Only one active tenant exists, or decision made about which tenant to keep
2. All other tenants deleted or data migrated

**Migration Steps:**

1. **Consolidate to single tenant:**
   ```sql
   -- Option A: Delete other tenants (cascade deletes their data)
   UPDATE "Tenants" SET "Status" = 4, "DeletedAt" = NOW() 
   WHERE "Slug" != 'default';
   
   -- Option B: Migrate data from other tenants to default tenant
   UPDATE "Users" SET "TenantId" = (SELECT "Id" FROM "Tenants" WHERE "Slug" = 'default')
   WHERE "TenantId" != (SELECT "Id" FROM "Tenants" WHERE "Slug" = 'default');
   -- Repeat for all tenant-scoped tables
   
   -- Then delete other tenants
   DELETE FROM "Tenants" WHERE "Slug" != 'default';
   ```

2. **Update configuration:**
   ```json
   {
     "MultiTenancy": {
       "Enabled": false,
       "DefaultTenantSlug": "default"
     }
   }
   ```

3. **Restart application**

4. **Update RPs (Relying Parties):**
   - **Critical**: All RPs must update issuer from `https://auth.example.com/t/{slug}` to `https://auth.example.com`
   - This **invalidates all existing tokens** (issuer mismatch)
   - Plan for coordinated cutover or token grace period

5. **Update discovery endpoints:**
   - Old: `https://auth.example.com/t/default/.well-known/openid-configuration`
   - New: `https://auth.example.com/.well-known/openid-configuration`
   - Both should return same content during transition

**Impact:**
- ⚠️ **Token invalidation** if issuer changes (from `/t/{slug}` to root)
- ⚠️ **All RPs must reconfigure** (issuer, endpoints)
- ⚠️ **Downtime required** for coordinated cutover
- ✅ **Simplified operations** (no tenant overhead)
- ✅ **Platform admin UI hidden** (cleaner for single-tenant use case)

**Recommendation:** Only switch from multi → single for new deployments or isolated enterprise instances. For existing multi-tenant deployments, consider keeping multi-tenant mode even if only one active tenant (avoids token invalidation).

### 8.3 Mode Toggle Safety Checklist

**Before enabling multi-tenancy:**
- [ ] Verify default tenant exists and is active
- [ ] Verify all data has `TenantId` assigned
- [ ] Test tenant resolution with `/t/default/` prefix
- [ ] Document RP migration plan (root → tenant prefix)
- [ ] Communicate changes to RP maintainers
- [ ] Test platform admin UI access controls

**Before disabling multi-tenancy:**
- [ ] **WARNING: Token invalidation expected**
- [ ] Consolidate or delete all tenants except one
- [ ] Notify all RPs of issuer change
- [ ] Plan coordinated cutover window
- [ ] Test issuer resolution (should use root URL)
- [ ] Verify no references to `/t/{slug}` in configs
- [ ] Update monitoring/alerting (no tenant-level metrics)

---

## 9. Configuration and Settings

### 9.1 Multi-Level Configuration Hierarchy

Settings cascade from platform → tenant → client:

1. **Platform defaults**: `appsettings.json` (global fallback)
2. **Tenant overrides**: `Tenant.SettingsJson` (per-tenant OIDC/Auth options)
3. **Client overrides**: `Client.*` fields (per-client policies)

Example: Access token lifetime
- Platform default: 900s (appsettings)
- Tenant override: 1800s (Tenant.SettingsJson)
- Client override: 3600s (Client.M2MAccessTokenLifetimeSeconds)

### 9.2 Tenant Settings Schema

Store tenant-specific settings in `Tenant.SettingsJson`:

```json
{
  "oidc": {
    "requirePkce": true,
    "requireConsent": true,
    "accessTokenLifetimeSeconds": 1800,
    "refreshTokenLifetimeSeconds": 2592000
  },
  "auth": {
    "allowUserRegistration": true,
    "requireEmailVerification": true,
    "passwordPolicy": {
      "minLength": 12,
      "requireUppercase": true,
      "requireDigit": true,
      "requireSpecialChar": true
    }
  },
  "qrLogin": {
    "enabled": false
  },
  "backchannelLogout": {
    "enabled": true
  },
  "branding": {
    "loginPageTitle": "Sign in to Acme Corp",
    "loginPageSubtitle": "Enter your credentials"
  }
}
```

Deserialize into strongly-typed classes at runtime:

```csharp
public class TenantSettings
{
    public OidcSettings? Oidc { get; set; }
    public AuthSettings? Auth { get; set; }
    public QrLoginSettings? QrLogin { get; set; }
    // ...
}
```

Access via `ITenantAccessor.Tenant.GetSettings<TenantSettings>()`.

---

## 10. Security and Isolation

### 10.1 Data Isolation

**Database level:**
- All queries automatically filter by `TenantId` (via `ITenantAccessor` or query filters)
- Foreign key constraints enforce tenant boundaries
- No cross-tenant joins possible

**Application level:**
- Services resolve `TenantId` from scoped `ITenantAccessor`
- Admin queries explicitly check tenant ownership before returning data
- Background jobs process tenant data in isolated batches

**Audit:**
- Log all cross-tenant access attempts (should never happen)
- Alert on anomalies

### 9.2 Token Isolation

**Issuer validation:**
- Each tenant has unique issuer URI
- Tokens issued by tenant A cannot be used in tenant B (issuer mismatch)
- RPs validate `iss` claim matches expected tenant issuer

**Audience validation:**
- Audiences scoped to tenant (e.g., `https://api.acme-corp.com`)
- Prevent audience confusion across tenants

**Signing keys:**
- Per-tenant keys (recommended) OR shared platform keys
- Key rotation isolated per tenant (if per-tenant keys used)

### 9.3 Rate Limiting

Apply rate limits per tenant (not just per IP):

```csharp
[RateLimit("tenant-token", Tenant = true, Requests = 1000, Window = "1m")]
public async Task<IResult> TokenEndpoint(...)
{
    // ...
}
```

Middleware extracts `TenantId` and buckets limits accordingly.

### 9.4 Denial of Service Protection

**Per-tenant quotas:**
- Max users, clients, IdPs, realms (stored in `Tenant` entity)
- Enforce during creation operations
- Return 429 or 403 when quota exceeded

**Resource limits:**
- Max token size, max JWKS size, max redirect URIs per client
- Prevent one tenant from exhausting shared resources (DB connections, memory, etc.)

---

## 11. Observability and Monitoring

### 11.1 Tenant-Scoped Metrics

Extend `OidcMetrics` to tag metrics with `tenant_id` (hashed for privacy):

```csharp
metrics.TokenIssuedCounter.Add(1, new KeyValuePair<string, object?>("tenant_id", tenantIdHash));
```

Aggregated metrics:
- Token issuance rate per tenant
- Error rate per tenant
- P95 latency per tenant
- Active sessions per tenant

### 10.2 Tenant-Scoped Logs

Structured logging with `TenantId` and `TenantSlug` in log context:

```csharp
logger.LogInformation("User {UserId} logged in", userId);
// Automatically includes: TenantId, TenantSlug via scope or enricher
```

Tenant admins can access filtered logs via admin UI.

### 10.3 Tenant Health Dashboard

Platform admin dashboard shows:
- Tenant status (active, suspended, pending, deleted)
- Usage metrics per tenant (users, clients, token volume)
- Health score per tenant (error rate, latency, uptime)
- Billing status (trial, active, overdue)

Tenant admin dashboard shows:
- Their tenant's metrics only
- Active users, sessions, token issuance
- Recent audit logs
- Client health (per-client metrics)

---

## 12. Billing and Subscription Management

### 12.1 Billing Plans

Define tiered plans with quotas:

**Free Tier:**
- Max 100 users
- Max 5 clients
- Max 1 IdP
- Community support

**Starter Tier:**
- Max 1,000 users
- Max 20 clients
- Max 5 IdPs
- Email support

**Pro Tier:**
- Max 10,000 users
- Max 100 clients
- Max 10 IdPs
- Priority support
- Custom branding

**Enterprise Tier:**
- Unlimited users/clients/IdPs
- Custom domain support
- SLA guarantee
- Dedicated support

Store plan in `Tenant.BillingPlan` and enforce quotas at creation time.

### 11.2 Integration with Payment Provider

**Stripe integration (recommended):**
1. Create Stripe Customer when tenant created
2. Store `Stripe.CustomerId` in `Tenant.MetadataJson`
3. On plan upgrade/downgrade, create Stripe Subscription
4. Handle webhooks for payment success/failure
5. Suspend tenant on payment failure (after grace period)

**Billing events:**
- Tenant created → create Stripe customer
- Plan changed → create/update Stripe subscription
- Payment succeeded → unsuspend tenant if suspended
- Payment failed → send warning email; suspend after 7 days

### 11.3 Usage-Based Billing (future)

Track billable events:
- Monthly Active Users (MAU)
- Token issuance count
- API calls (introspection, userinfo, etc.)

Store aggregated counts in `TenantUsage` table:

```csharp
public class TenantUsage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DateOnly Month { get; set; }
    public int MonthlyActiveUsers { get; set; }
    public long TokensIssued { get; set; }
    public long ApiCallsIntrospection { get; set; }
    // ...
}
```

Background job aggregates daily/monthly; sends to billing system.

---

## 13. Migration Path for Existing Deployments

### 13.1 Backward Compatibility

For existing single-tenant deployments:

**Option 1: Automatic migration to default tenant**
1. Run migration that creates "default" tenant
2. Backfill all existing data with `TenantId = default tenant`
3. Keep existing routes functional (no `/t/{slug}` prefix required)
4. Middleware: if no tenant context, assume default tenant
5. Discovery at root `/` still works for default tenant

**Option 2: Opt-in multi-tenancy**
1. Feature flag: `MultiTenancy:Enabled = false` (default)
2. When disabled, tenant resolution skipped; uses default tenant implicitly
3. Admin can enable multi-tenancy when ready
4. Existing single-tenant deployments unaffected

### 12.2 Data Migration Script

Provide script to migrate existing single-tenant data:

```sql
-- Create default tenant
INSERT INTO "Tenants" ("Id", "Slug", "Name", "IssuerUri", "Status", "CreatedAt")
VALUES (gen_random_uuid(), 'default', 'Default Tenant', 'https://auth.example.com', 1, NOW());

-- Backfill TenantId in existing tables
UPDATE "Users" SET "TenantId" = (SELECT "Id" FROM "Tenants" WHERE "Slug" = 'default');
UPDATE "Clients" SET "TenantId" = (SELECT "Id" FROM "Tenants" WHERE "Slug" = 'default');
UPDATE "Realms" SET "TenantId" = (SELECT "Id" FROM "Tenants" WHERE "Slug" = 'default');
-- ... repeat for all tenant-scoped entities
```

---

## 14. Implementation Phases

**Important Note on Single-Tenant Mode:**
All phases include mode-aware implementation. The `MultiTenancy:Enabled` feature flag allows deployments to run in single-tenant mode (for self-hosted/enterprise/IdP chaining scenarios) or multi-tenant mode (for SaaS scenarios) without code changes. The database schema remains the same; only routing and issuer construction differ based on the mode.

### Phase 1: Foundation (MVP) – 4-6 weeks

**Goal:** Support multiple tenants with path-based routing and basic admin UI, with mode toggle capability.

**Tasks:**
1. Configuration:
   - [x] Add `MultiTenancy:Enabled` feature flag to appsettings
   - [x] Add `MultiTenancy:DefaultTenantSlug` configuration
   - [x] Create `IMultiTenancyOptions` interface and implementation
2. Data model changes:
   - [x] Add `Tenant` entity
   - [x] Add `TenantId` to existing entities (nullable first)
   - [x] Add `TenantStatus` enum
   - [x] Add indexes and foreign keys in OnModelCreating
   - [x] Create migration with backfill to default tenant
   - [x] Seed default tenant in migration (Id: ...0001, Slug: "default")
   - [x] Apply migration to database (running in Docker)
3. Tenant resolution:
   - [x] Implement `ITenantResolver` with mode awareness
   - [x] Implement `ModeAwareTenantResolver` with caching
   - [x] Add `TenantResolutionMiddleware` (single/multi mode support)
   - [x] Implement `ITenantAccessor` scoped service
   - [x] Create `TenantContext` class
   - [x] Register services in DI container (Program.cs)
   - [x] Add middleware to pipeline (after UseRouting, before UseAuthentication)
4. Update services to filter by `TenantId`:
   - [x] `ClientStore`, `UserService`, `ConsentService`, `AuthorizationCodeService`
   - [x] `KeyStore`, `RefreshTokenService`, `RevocationService`, `QrLoginService`
   - [x] All 8 tenant-aware services updated and tested (331/331 tests passing)
5. Update issuer logic:
   - [x] Create `IIssuerBuilder` interface and implementation
   - [x] Register IssuerBuilder in DI container
   - [x] Update `GetIssuer` extension methods (HttpContextExtensions, LogoutExtensions, AuthorizeHandler)
   - [x] Mode-aware issuer: root in single-tenant, path-based in multi-tenant
   - [x] All handler code updated to use mode-aware issuer
6. Per-tenant JWKS:
   - [x] Update JWKS endpoint to use tenant-filtered keys from KeyStore
   - [x] Verify JWKS response includes only current tenant's keys
   - [x] Create comprehensive tests for tenant isolation
7. Routing:
   - [x] Implement mode-aware route registration (root or `/t/{slug}/*`)
   - [x] Update all OIDC protocol endpoints to support both modes
   - [x] Add fallback to default tenant for non-prefixed routes (backward compatibility)
8. Platform admin UI:
   - [x] Create `/platform-admin/tenants` list page (multi-tenant mode only)
   - [x] Create `/platform-admin/tenants/create` form
   - [x] Implement tenant CRUD operations (Index, Create, Edit)
   - [x] Platform admin dashboard with cross-tenant stats
   - [x] Tenant impersonation functionality
   - [x] Impersonation history tracking
   - [x] Hide platform admin UI in single-tenant mode
9. Tenant admin UI:
   - [x] Existing admin UI works at `/admin` (single) or `/t/{slug}/admin` (multi)
   - [x] Add tenant context awareness to admin pages
   - [x] Platform admin can filter by tenant across all admin pages
   - [x] Tenant admins see only their own tenant data
10. User self-service portal:
   - [x] Create `/account/*` routes (separate from admin UI)
   - [x] Dashboard page with overview stats (`/Account`)
   - [x] Profile management page (view/edit name, email) (`/Account/Profile`)
   - [x] Active sessions page (view and revoke) (`/Account/Sessions`)
   - [x] Consent history page (view and revoke app authorizations) (`/Account/Consents`)
   - [x] Linked identities page (view external IdP linkages, unlink) (`/Account/LinkedAccounts`)
   - [x] Alternative emails management (`/Account/Emails`)
   - [x] Password change page (existing `/Password/Index`, routing fixed)
   - [x] MFA management page (existing `/Mfa/Index`, routing fixed)
   - [x] Apply authorization policy: any authenticated user (no admin role required)
   - [x] Shared tab navigation component (`_AccountTabs.cshtml`)
11. Testing:
   - [x] Unit tests updated for issuer builder and multi-tenancy services
   - [x] All 331 tests passing with mode-aware issuer logic
   - [ ] Single-tenant mode E2E tests (root issuer, no tenant prefix)
   - [ ] Multi-tenant integration tests (2+ tenants, data isolation)
   - [ ] Verify data isolation (queries don't leak across tenants)
   - [ ] E2E test: create tenant, create client, issue token, validate issuer
   - [ ] Mode switching tests (single → multi, multi → single)
   - [ ] User self-service tests (non-admin user access, admin UI protection)
   - [ ] Platform admin impersonation security tests

**Deliverables:**
- ✅ Mode-aware OIDC server (single-tenant or multi-tenant via config)
- ✅ Platform admin can create/manage tenants (multi-tenant mode)
- ✅ Tenant admins can manage their own clients/users (both modes)
- ✅ User self-service portal complete (8 pages, accessible to all authenticated users)
- ✅ Role-based access control (platform admin, tenant admin, regular user separation)
- ✅ Platform admin impersonation with audit trail
- 🔄 Documentation: multi-tenancy architecture guide and mode-switching guide (in progress)
- 🔄 Comprehensive integration and E2E tests (pending)

### Phase 2: Branding and Tenant Settings – 2-3 weeks

**Goal:** Add per-tenant branding and advanced settings management.

**Tasks:**
1. Branding:
   - [ ] Add logo/color fields to `Tenant` entity
   - [ ] Update login/consent pages to use tenant branding
   - [ ] Tenant admin UI for branding customization
3. Tenant setup wizard:
   - [ ] Post-creation wizard (branding, first client, IdP, users)
   - [ ] Guided onboarding flow
4. Settings overrides:
   - [ ] Implement `Tenant.SettingsJson` parsing
   - [ ] Cascade settings: platform → tenant → client
   - [ ] Tenant admin UI for settings management
5. Testing:
   - [ ] Branding display tests (visual regression or screenshot)
   - [ ] Settings override tests (platform → tenant → client cascade)

**Deliverables:**
- Per-tenant branding (logo, colors)
- Tenant setup wizard
- Settings override system
- Enhanced tenant admin UI

### Phase 3: Lifecycle Management and Advanced Admin – 3-4 weeks

**Goal:** Advanced tenant lifecycle management and administrative features.

**Tasks:**
1. Tenant lifecycle:
   - [ ] Implement suspension flow (billing, abuse)
   - [ ] Implement soft delete with grace period
   - [ ] Implement hard delete background job
   - [ ] Tenant restore from soft delete
3. Advanced admin features:
   - [ ] Tenant impersonation (platform admin → tenant admin)
   - [ ] Cross-tenant audit logs (platform admin view)
   - [ ] Tenant usage dashboard (MAU, token volume)
4. Quota enforcement:
   - [ ] Enforce max users/clients/IdPs at creation
   - [ ] Return 429/403 when quota exceeded
   - [ ] Tenant admin UI shows current usage vs quota
5. Testing:
   - [ ] Suspension/deletion/restore E2E tests
   - [ ] Quota enforcement tests
   - [ ] Tenant impersonation security tests

**Deliverables:**
- Tenant suspension and deletion flows
- Tenant impersonation for support
- Quota enforcement
- Advanced admin features

### Phase 4: Billing Integration and Self-Service – 3-4 weeks

**Goal:** Enable self-service tenant signup and integrate billing.

**Tasks:**
1. Self-service signup:
   - [ ] Public `/signup` page (form: email, company, slug)
   - [ ] Email verification flow
   - [ ] Auto-create tenant + admin user on verification
   - [ ] Redirect to tenant-specific onboarding
2. Billing integration:
   - [ ] Integrate Stripe (create customer, subscriptions)
   - [ ] Handle Stripe webhooks (payment success/failure)
   - [ ] Suspend tenant on payment failure after grace period
   - [ ] Tenant admin UI: view plan, upgrade/downgrade, billing history
3. Usage tracking:
   - [ ] Track MAU, token issuance, API calls
   - [ ] Store in `TenantUsage` table
   - [ ] Aggregate daily/monthly via background job
   - [ ] Display in tenant admin dashboard
4. Testing:
   - [ ] Self-service signup E2E tests
   - [ ] Stripe webhook tests (mock webhooks)
   - [ ] Usage tracking accuracy tests

**Deliverables:**
- Self-service tenant signup
- Stripe billing integration
- Usage tracking and reporting
- Tenant billing dashboard

### Phase 5: Scale and Hardening – 2-3 weeks

**Goal:** Production-ready scale, security, and observability.

**Tasks:**
1. Performance optimization:
   - [ ] Add Redis caching for tenant resolution
   - [ ] Cache tenant settings, JWKS per tenant
   - [ ] Query optimization (analyze slow queries, add indexes)
   - [ ] Load testing (1000+ tenants, 10k+ users per tenant)
2. Security hardening:
   - [ ] Audit cross-tenant access (ensure no leaks)
   - [ ] Add honeypot queries to detect cross-tenant attempts
   - [ ] Enable EF query filters globally (optional)
   - [ ] Penetration testing for tenant isolation
3. Observability:
   - [ ] Add tenant-scoped metrics (Prometheus/App Insights)
   - [ ] Add tenant-scoped logs (ELK/Seq)
   - [ ] Platform admin dashboard: tenant health scores
   - [ ] Alerting: tenant error rates, high latency, quota exceeded
4. Documentation:
   - [ ] Multi-tenancy admin guide
   - [ ] Self-service signup guide for end users
   - [ ] API reference for tenant management
   - [ ] Migration guide for existing deployments

**Deliverables:**
- Production-ready multi-tenant system
- Performance and load testing results
- Security audit report
- Comprehensive documentation

---

## 15. Testing Strategy

### 15.1 Unit Tests

**Tenant resolution:**
- Path-based resolver extracts correct slug
- Subdomain resolver extracts correct slug
- Custom domain resolver matches correct tenant
- Fallback behavior when tenant not found

**Data isolation:**
- Queries filter by `TenantId` correctly
- Cross-tenant queries return empty results
- Admin queries explicitly allow cross-tenant (platform admin only)

**Issuer construction:**
- Path-based issuer format correct
- Subdomain issuer format correct
- Custom domain issuer uses stored value

### 14.2 Integration Tests

**Multi-tenant flows:**
- Create 2 tenants, create clients in each, issue tokens, verify issuers differ
- User in tenant A cannot access resources in tenant B
- Tokens issued by tenant A rejected by tenant B

**Admin operations:**
- Platform admin creates tenant, tenant appears in list
- Tenant admin manages clients, changes visible only in their tenant
- Platform admin suspends tenant, tenant requests fail with 503

### 14.3 End-to-End Tests

**Self-service signup:**
1. User signs up with email/company/slug
2. User receives verification email
3. User clicks link, tenant created
4. User redirected to onboarding wizard
5. User creates first client
6. User completes setup, issues first token

**Custom domain:**
1. Tenant configures custom domain in admin UI
2. System validates DNS (TXT record)
3. System provisions SSL cert
4. Tenant accesses OP via custom domain
5. Token issued with custom domain as issuer

### 14.4 Performance Tests

**Load test scenarios:**
- 1000 tenants, 100 concurrent requests per tenant (100k total)
- Discovery endpoint latency under load
- Token issuance rate (tokens/sec)
- Database query performance (tenant filter overhead)

**Chaos engineering:**
- Kill primary DB during token issuance (failover)
- Suspend tenant mid-session (503 behavior)
- Inject high latency in tenant resolution (timeout handling)

---

## 16. Documentation Deliverables

### 16.1 For Platform Administrators

- **Multi-Tenancy Architecture Guide** (this document)
- **Tenant Management Guide**: How to create, suspend, delete tenants
- **Billing Configuration Guide**: Integrate Stripe, configure plans
- **Custom Domain Setup Guide**: DNS, SSL cert provisioning
- **Monitoring and Alerting Guide**: Tenant health dashboards, alert rules

### 15.2 For Tenant Administrators

- **Tenant Admin Quick Start**: First login, setup wizard
- **Client Configuration Guide**: Create OIDC clients, configure policies
- **User Management Guide**: Invite users, assign roles, reset passwords
- **Branding Customization Guide**: Upload logo, set colors, customize login page
- **Identity Provider Integration Guide**: Configure Google, Azure AD, etc.
- **Audit Logs and Monitoring Guide**: View logs, understand metrics

### 15.3 For Developers (RP integrators)

- **Multi-Tenant Integration Guide**: Discover tenant issuer, configure RP
- **Per-Tenant Discovery**: How to find tenant-specific `.well-known/openid-configuration`
- **Tenant-Specific Audiences**: Configure audiences for each tenant
- **Troubleshooting Guide**: Common issues (issuer mismatch, audience mismatch, etc.)

### 15.4 For Internal Developers

- **Multi-Tenancy Development Guide**: Work with tenant context, test locally
- **Adding Tenant-Scoped Entities**: How to add new entities with `TenantId`
- **Testing Multi-Tenancy**: Unit/integration test patterns
- **Debugging Cross-Tenant Issues**: Tools and techniques

---

## 17. Open Questions and Decisions

### 17.1 Tenant Limits and Quotas

**Question:** Should quotas be hard limits or soft warnings?

**Options:**
- **Hard limits**: Reject creation when quota exceeded (clear, enforceable, but can frustrate users)
- **Soft warnings**: Allow temporary overage, send notification (flexible, but risk of abuse)
- **Hybrid**: Soft warning for 10% overage, hard limit at 20% (balanced)

**Recommendation:** Hard limits with grace period (e.g., allow 10% overage for 7 days).

### 16.2 Shared vs Per-Tenant Signing Keys

**Question:** Should each tenant have its own signing keys or share platform keys?

**Options:**
- **Per-tenant keys**: Better isolation, key rotation per tenant, but more complex (key storage, JWKS per tenant)
- **Shared platform keys**: Simpler, single JWKS, but key compromise affects all tenants
- **Hybrid**: Platform provides default keys; tenants can upload custom keys (flexibility, but added complexity)

**Recommendation:** Start with per-tenant keys (better security/isolation); add shared keys as fallback if needed.

### 16.3 Default Tenant for Legacy Routes

**Question:** Should existing non-tenant routes (e.g., `/authorize`) continue to work?

**Decision:** **Yes, map to default tenant** for backward compatibility.

**Implementation:**
- Create a "default" tenant during initial migration
- If request path does NOT start with `/t/{slug}`, use default tenant
- Default tenant slug: `default` (configurable via `MultiTenancy:DefaultTenantSlug`)
- Emit warning logs when default tenant is used
- Add deprecation notice in discovery metadata (custom field)
- Plan to require `/t/{slug}` prefix in v2.0 (breaking change)

### 16.4 Tenant Data Export

**Question:** Should tenants be able to export all their data (GDPR, portability)?

**Options:**
- **Yes, via admin UI**: Tenant admin clicks "Export" → ZIP with JSON files (users, clients, tokens, logs)
- **Yes, via API**: Provide `/t/{slug}/admin/export` endpoint (programmatic access)
- **Not initially**: Defer to Phase 4 or later

**Recommendation:** Add in Phase 3 or 4; important for GDPR compliance and enterprise customers.

### 16.5 Cross-Tenant SSO

**Question:** Should users be able to have a single account across multiple tenants (B2B2C scenario)?

**Example:** User works for Company A and Company B; should they have one account or two?

**Options:**
- **Separate accounts per tenant**: Simpler, full isolation, but user has multiple credentials
- **Federated identity across tenants**: User has one account, links to multiple tenants (complex, but better UX)
- **Platform-level identity with tenant memberships**: User entity is platform-level, linked to multiple tenants via `UserTenantMembership` (most flexible, but significant architecture change)

**Recommendation:** Start with separate accounts per tenant (MVP); add federated identity in Phase 5+ if demand exists.

---

## 18. Success Metrics

### 18.1 Technical Metrics

- **Tenant isolation**: Zero cross-tenant data leaks (audited via tests and logs)
- **Performance**: P95 latency <200ms for token issuance (single-tenant baseline: <100ms)
- **Uptime**: 99.9% uptime per tenant (SLA for Pro/Enterprise plans)
- **Scale**: Support 10,000 tenants with 1,000 active users each (10M total users)

### 17.2 Business Metrics

- **Tenant adoption**: 100 tenants in first 3 months post-launch
- **Self-service conversion**: 50% of signups complete onboarding wizard
- **Paid conversion**: 20% of free tier tenants upgrade to paid plans within 6 months
- **Support load**: <5% of tenants require support interaction per month

### 17.3 User Experience Metrics

- **Time to first token**: <10 minutes from signup to issuing first token
- **Admin UI satisfaction**: >80% satisfaction score from tenant admins
- **RP integration time**: <1 hour for developers to integrate with multi-tenant OP

---

## 19. Risks and Mitigations

### 19.1 Risk: Data Leakage Across Tenants

**Impact:** Critical (security, compliance, trust)

**Likelihood:** Medium (bugs in query filters, admin UI)

**Mitigation:**
- Comprehensive unit/integration tests for data isolation
- EF query filters as safety net
- Audit logs for cross-tenant access attempts
- Penetration testing before launch
- Bug bounty program post-launch

### 18.2 Risk: Performance Degradation at Scale

**Impact:** High (user experience, SLA violations)

**Likelihood:** Medium (many tenants, large DB, slow queries)

**Mitigation:**
- Load testing with realistic tenant/user distribution
- Database sharding (partition by `TenantId` if single DB becomes bottleneck)
- Redis caching for tenant resolution and settings
- Horizontal scaling (stateless app servers, load balancer)
- Monitor query performance (slow query log, APM)

### 18.3 Risk: Complex Migrations for Existing Deployments

**Impact:** Medium (delayed adoption, support burden)

**Likelihood:** High (many existing single-tenant deployments)

**Mitigation:**
- Provide automated migration script (backfill default tenant)
- Feature flag for multi-tenancy (opt-in)
- Comprehensive migration guide and video walkthrough
- Dedicated support during migration window

### 18.4 Risk: Custom Domain SSL Provisioning Failures

**Impact:** Medium (tenant unable to use custom domain)

**Likelihood:** Medium (DNS misconfig, ACME rate limits, cert validation failures)

**Mitigation:**
- Clear DNS setup instructions with validation checks
- Use Let's Encrypt staging environment for testing
- Retry logic for ACME challenges
- Fallback to manual cert upload if automation fails
- Monitor cert expiry, auto-renew 30 days before expiration

### 18.5 Risk: Billing Integration Issues

**Impact:** High (revenue loss, tenant suspension)

**Likelihood:** Medium (webhook failures, payment gateway downtime)

**Mitigation:**
- Idempotent webhook handlers (use event ID to dedupe)
- Retry failed webhooks (Stripe retries for 3 days)
- Manual reconciliation tool (compare Stripe state vs DB state)
- Grace period before suspension (7 days after payment failure)
- Alert on webhook failures, investigate promptly

---

## 20. Conclusion

This backlog provides a comprehensive roadmap for transforming MrWhoOidc.WebAuth into a production-ready multi-tenant OIDC Provider. The phased approach balances feature delivery with risk management, starting with an MVP (path-based routing, basic admin) and progressively adding advanced features (branding, lifecycle management, billing).

**Key success factors:**
- **Data isolation**: Rigorous testing and auditing to prevent cross-tenant leaks
- **User access separation**: Clear distinction between regular users (self-service portal) and administrators (admin UI) with role-based access control
- **Mode flexibility**: Single-tenant mode for enterprise/IdP chaining scenarios; multi-tenant mode for SaaS deployments
- **Scalability**: Performance testing and architectural decisions (caching, sharding) to support 10k+ tenants
- **Developer experience**: Clear documentation and intuitive UIs for end users, tenant admins, and platform admins
- **Flexibility**: Configurable settings hierarchy (platform → tenant → client) to accommodate diverse use cases

**Access Control Summary:**

| User Type | Access to | Route Prefix | Role Required |
|-----------|-----------|--------------|---------------|
| **Regular User** | Self-service portal (profile, MFA, sessions, consent) | `/account/*` or `/t/{slug}/account/*` | None (just authenticated) |
| **Tenant Admin** | Admin UI (manage users, clients, realms, IdPs, settings) | `/admin/*` or `/t/{slug}/admin/*` | `tenant-admin` role |
| **Platform Admin** | Platform admin (manage all tenants, cross-tenant metrics) | `/platform-admin/*` | Platform admin entity/flag |

**Next steps:**
1. ✅ ~~Review and approve this backlog with stakeholders~~
2. ✅ ~~Prioritize Phase 1 tasks and assign to team~~
3. ✅ ~~Create detailed task breakdown for Phase 1~~
4. ✅ ~~Design wireframes for user self-service portal and updated admin UI~~
5. ✅ ~~Set up CI/CD pipeline for multi-tenant testing~~
6. ✅ ~~Complete Phase 1 implementation!~~

**Current Status: Phase 1 Complete (95%), Moving to Testing & Phase 2**

---

## 21. Phase 1 Completion Status & Next Steps (October 7, 2025)

### ✅ Phase 1 Achievements (October 4-7, 2025)

**Infrastructure (100% Complete):**
- ✅ Multi-tenancy configuration system
- ✅ Tenant entity model and database migrations
- ✅ Tenant resolution middleware with mode awareness
- ✅ Service layer tenant filtering (8 core services)
- ✅ Mode-aware issuer builder
- ✅ Multi-tenant routing with fallback support
- ✅ Background services tenant context management
- ✅ JWKS endpoint tenant filtering

**Platform Admin UI (100% Complete):**
- ✅ Dashboard with cross-tenant statistics
- ✅ Tenant list, create, edit pages
- ✅ Tenant impersonation functionality
- ✅ Impersonation audit history
- ✅ Platform admin authorization policy

**User Self-Service Portal (100% Complete):**
- ✅ Dashboard with account overview
- ✅ Profile management
- ✅ Active sessions with revocation
- ✅ App consent/permissions management
- ✅ Linked external accounts
- ✅ Alternative emails management
- ✅ Password change (routing fixed)
- ✅ MFA/security management (routing fixed)

**Test Coverage:**
- ✅ All 331 unit tests passing
- ✅ Tenant resolution tests
- ✅ Service layer isolation tests
- ✅ JWKS filtering tests

### 🔄 Remaining Phase 1 Work (5%)

**Integration & E2E Testing (Priority: HIGH):**

1. **Multi-Tenant E2E Flow Tests** (Estimated: 8-12 hours)
   - [ ] Create 2+ tenants via Platform Admin UI
   - [ ] Create clients in each tenant
   - [ ] Issue tokens for each tenant
   - [ ] Verify issuer URIs differ by tenant
   - [ ] Verify tokens from Tenant A rejected by Tenant B
   - [ ] Verify JWKS contains only tenant-specific keys
   - [ ] Test discovery endpoint per tenant

2. **Data Isolation Verification** (Estimated: 4-6 hours)
   - [ ] Audit all database queries for `TenantId` filtering
   - [ ] Create automated "cross-tenant leak" detection tests
   - [ ] Test that User A (Tenant 1) cannot access User B (Tenant 2) data
   - [ ] Test that Client A (Tenant 1) cannot access Client B (Tenant 2) data
   - [ ] Verify admin UI queries respect tenant boundaries

3. **Mode Switching Tests** (Estimated: 4-6 hours)
   - [ ] Test single-tenant mode behavior (root issuer, no `/t/{slug}`)
   - [ ] Test multi-tenant mode behavior (path-based issuers)
   - [ ] Document mode switching procedure
   - [ ] Test fallback routes to default tenant
   - [ ] Verify issuer consistency after mode change

4. **Platform Admin Security Tests** (Estimated: 4-6 hours)
   - [ ] Verify non-platform-admin users cannot access `/PlatformAdmin/*`
   - [ ] Test impersonation authorization (only platform admins)
   - [ ] Test impersonation audit logging
   - [ ] Verify impersonation session isolation
   - [ ] Test "stop impersonation" cleanup

5. **User Self-Service Authorization Tests** (Estimated: 2-3 hours)
   - [ ] Verify any authenticated user can access `/Account/*`
   - [ ] Verify users can only see their own data
   - [ ] Test session revocation (cannot revoke current session)
   - [ ] Test consent revocation
   - [ ] Verify tenant admins cannot access other users' `/Account` pages

**Estimated Total Remaining: 22-33 hours (3-4 days)**

### 📋 Proposed Next Steps (Priority Order)

#### Immediate (This Week - October 7-11, 2025)

**Option A: Complete Phase 1 Testing First (Recommended)**
- Focus: Integration and E2E testing
- Goal: Ensure Phase 1 foundation is rock-solid before adding new features
- Deliverables:
  - Comprehensive E2E test suite
  - Data isolation verification report
  - Mode switching guide
  - Security audit report
- Benefit: Reduce technical debt, catch issues early

**Option B: Begin Phase 2 Branding (Parallel Track)**
- Focus: Per-tenant branding and settings
- Risk: May introduce bugs before Phase 1 is fully validated
- Benefit: Faster feature delivery for end users

**Recommendation:** **Option A** - Complete Phase 1 testing before moving to Phase 2.

#### Week 2-3 (October 14-25, 2025): Phase 2 - Branding & Settings

**Phase 2 Core Tasks:**

1. **Tenant Branding System** (6-8 hours)
   - [ ] Implement logo upload to blob storage or CDN
   - [ ] Add color scheme configuration (primary, accent, background)
   - [ ] Create branding preview component
   - [ ] Update login/consent pages to use tenant branding
   - [ ] Add branding to Platform Admin UI (Create/Edit tenant)

2. **Per-Tenant Settings System** (8-10 hours)
   - [ ] Define settings schema (JSON or dedicated columns)
   - [ ] Implement settings cascade: Platform → Tenant → Client
   - [ ] Add settings editor UI in Platform Admin
   - [ ] Add tenant-specific OIDC overrides (token lifetimes, PKCE requirements, etc.)
   - [ ] Add tenant-specific features flags (allow registration, require email verification, etc.)

3. **Tenant Setup Wizard** (8-12 hours)
   - [ ] Post-creation wizard flow (branding, first client, first user)
   - [ ] Guided onboarding experience
   - [ ] Skip/complete tracking
   - [ ] Integration with tenant seeding service

4. **Tenant Admin Settings Page** (4-6 hours)
   - [ ] Create `/t/{slug}/Admin/Settings` page
   - [ ] Allow tenant admins to customize tenant-level settings
   - [ ] Prevent editing platform-enforced settings
   - [ ] Settings validation and preview

**Estimated Phase 2 Total: 26-36 hours (3-5 days)**

#### Week 4-6 (October 28 - November 15, 2025): Phase 3 - Lifecycle Management

**Phase 3 Core Tasks:**

1. **Tenant Suspension Flow** (6-8 hours)
   - [ ] Implement suspension logic (billing, abuse, manual)
   - [ ] Return 503 for suspended tenant requests
   - [ ] Platform Admin UI for suspend/unsuspend
   - [ ] Notification system for suspended tenants
   - [ ] Grace period configuration

2. **Soft Delete with Grace Period** (8-10 hours)
   - [ ] Implement soft delete (mark `DeletedAt` timestamp)
   - [ ] Hide soft-deleted tenants from normal queries
   - [ ] Create background job for hard delete after grace period (30 days)
   - [ ] Platform Admin UI for restore from soft delete
   - [ ] Audit logging for deletion/restoration

3. **Quota Enforcement** (6-8 hours)
   - [ ] Check quotas at creation time (users, clients, IdPs)
   - [ ] Return 429/403 when quota exceeded
   - [ ] Display current usage vs. quota in Tenant Admin dashboard
   - [ ] Quota warning notifications (80%, 90%, 100%)
   - [ ] Platform Admin can override quotas

4. **Usage Dashboard** (8-12 hours)
   - [ ] Track MAU (Monthly Active Users)
   - [ ] Track token issuance volume
   - [ ] Track API calls per tenant
   - [ ] Create `TenantUsage` table
   - [ ] Background job for usage aggregation
   - [ ] Platform Admin cross-tenant usage view
   - [ ] Tenant Admin own usage view

**Estimated Phase 3 Total: 28-38 hours (4-5 days)**

### 🎯 Recommended 4-Week Plan (October 7 - November 1, 2025)

**Week 1 (Oct 7-11): Phase 1 Testing**
- Days 1-2: Multi-tenant E2E tests
- Days 3-4: Data isolation verification
- Day 5: Mode switching tests, security audit

**Week 2 (Oct 14-18): Phase 2 - Branding (Part 1)**
- Days 1-2: Tenant branding system (logo, colors)
- Days 3-4: Apply branding to login/consent pages
- Day 5: Per-tenant settings schema

**Week 3 (Oct 21-25): Phase 2 - Settings & Wizard (Part 2)**
- Days 1-3: Settings cascade implementation
- Days 4-5: Tenant setup wizard

**Week 4 (Oct 28 - Nov 1): Phase 3 - Lifecycle (Part 1)**
- Days 1-2: Tenant suspension flow
- Days 3-5: Soft delete with grace period

**Deliverables by November 1:**
- ✅ Phase 1 fully tested and documented
- ✅ Phase 2 branding and settings complete
- ✅ Phase 3 lifecycle management (suspension, deletion) complete
- 🔄 Phase 3 quota enforcement (deferred to November)
- 🔄 Phase 4 billing integration (deferred to later)

### 📊 Success Metrics for Next Phase

**Quality Metrics:**
- Zero cross-tenant data leaks (verified by tests)
- All integration tests passing (target: 50+ new tests)
- E2E test coverage for critical flows (tenant creation, token issuance, revocation)
- Security audit with no critical findings

**Feature Metrics:**
- Tenant branding applied to all public-facing pages
- Settings cascade working (platform → tenant → client)
- Tenant suspension/deletion flows tested and documented

**Performance Metrics:**
- Tenant resolution latency < 10ms (with caching)
- Token issuance latency < 200ms (multi-tenant mode)
- Database query performance acceptable (no N+1 queries)

### 🚨 Risks & Mitigation

**Risk 1: E2E Tests Reveal Major Issues**
- **Likelihood:** Medium
- **Impact:** High (delays Phase 2 start)
- **Mitigation:** Budget extra time (1-2 days buffer) for test fixes

**Risk 2: Data Isolation Gaps**
- **Likelihood:** Low-Medium
- **Impact:** Critical (security)
- **Mitigation:** Systematic audit of all queries; add EF query filters as safety net

**Risk 3: Performance Degradation**
- **Likelihood:** Medium
- **Impact:** High (user experience)
- **Mitigation:** Performance testing under load; add caching where needed

**Risk 4: Scope Creep (Too Many "Nice to Have" Features)**
- **Likelihood:** High
- **Impact:** Medium (delays, budget overrun)
- **Mitigation:** Strict adherence to phase scope; defer non-critical features

### 📝 Documentation Priorities

**This Week:**
1. Integration testing guide (how to run, what to verify)
2. Mode switching procedure (single ↔ multi-tenant)
3. Data isolation verification report
4. Platform admin user guide (tenant management)

**Next 2 Weeks:**
1. Tenant branding customization guide
2. Settings cascade architecture document
3. Tenant setup wizard user guide

---

## Appendix A: Entity Relationship Diagram (Conceptual)

```
Platform Level:
  - Tenant (1) → (M) Realm
  - Tenant (1) → (M) User
  - Tenant (1) → (M) Client
  - Tenant (1) → (M) IdentityProvider
  - Tenant (1) → (M) SigningKey (nullable TenantId for shared keys)

Tenant Level:
  - Realm (1) → (M) Role
  - Client (1) → (M) ClientScope
  - Client (1) → (M) Token
  - User (1) → (M) Consent
  - User (1) → (M) UserRoleAssignment

Cross-Tenant (Platform Admin only):
  - PlatformAdministrator (separate table, no TenantId)
```

## Appendix B: Sample Tenant Configuration

```json
{
  "tenantId": "550e8400-e29b-41d4-a716-446655440000",
  "slug": "acme-corp",
  "name": "Acme Corporation",
  "issuerUri": "https://auth.example.com/t/acme-corp",
  "status": "Active",
  "branding": {
    "logoUrl": "https://cdn.example.com/tenants/acme-corp/logo.png",
    "primaryColor": "#007bff",
    "accentColor": "#0056b3"
  },
  "quotas": {
    "maxUsers": 10000,
    "maxClients": 100,
    "maxIdentityProviders": 10
  },
  "billingPlan": "Pro",
  "trialEndsAt": null,
  "settings": {
    "oidc": {
      "requirePkce": true,
      "requireConsent": true,
      "accessTokenLifetimeSeconds": 1800
    },
    "auth": {
      "allowUserRegistration": true,
      "requireEmailVerification": true
    }
  }
}
```

## Appendix C: References

- OpenID Connect Core 1.0: https://openid.net/specs/openid-connect-core-1_0.html
- Multi-Tenancy Patterns: https://docs.microsoft.com/en-us/azure/architecture/patterns/multitenancy
- Stripe API Documentation: https://stripe.com/docs/api
- Let's Encrypt ACME Protocol: https://letsencrypt.org/docs/client-options/
- ASP.NET Core Multi-Tenancy: https://www.finbuckle.com/MultiTenant/Docs/
- OWASP Multi-Tenancy Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Multitenancy_Security_Cheat_Sheet.html
