# Tenant Selection - Configuration Summary

This document summarizes the configuration changes made to support the email-first tenant discovery feature.

## Overview
- **Feature**: Email-based tenant discovery with multi-tenant selection UI
- **Implementation Date**: 2025
- **Status**: ✅ Sprint 1 & 2 Complete - Backend + UI pages implemented
- **Configuration**: ✅ Complete - Rate limiting, session storage, routing configured

---

## Configuration Changes

### 1. Session Storage (LocalizationAndMvcExtensions.cs)

**Added Session Support:**
```csharp
services.AddDistributedMemoryCache();
services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(10);
    options.Cookie.Name = ".mrwhooidc.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.IsEssential = true;
});
```

**Purpose:**
- Store tenant list between discovery and selection pages
- Session expires after 10 minutes of inactivity
- Secure cookie settings for production use

**Location:** `MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/LocalizationAndMvcExtensions.cs`

---

### 2. Middleware Pipeline (PipelineExtensions.cs)

**Added UseSession() Call:**
```csharp
app.UseRouting();
app.UseTenantResolution();
app.UseSession(); // <- Added here
app.UseRequestLocalization(localizationOptions);
```

**Order Matters:**
- After `UseRouting()` (routing must be configured first)
- After `UseTenantResolution()` (tenant context available)
- Before `UseAuthentication()` (session available for auth flows)

**Location:** `MrWhoOidc.WebAuth/Infrastructure/Pipeline/PipelineExtensions.cs`

---

### 3. Rate Limiting Policy (RateLimitingExtensions.cs)

**Added Email Discovery Policy:**
```csharp
options.AddPolicy("email-discovery", httpContext =>
{
    var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 5,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    });
});
```

**Policy Details:**
- **Name**: `email-discovery`
- **Limit**: 5 requests per minute per IP address
- **Window**: Fixed 1-minute window
- **Partition Key**: Remote IP address
- **Queue**: No queuing (immediate rejection)

**Usage:**
- Applied to `DiscoverTenant.cshtml.cs` via `[EnableRateLimiting("email-discovery")]` attribute
- Prevents abuse of tenant discovery endpoint
- Protects database from enumeration attacks

**Location:** `MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/RateLimitingExtensions.cs`

---

### 4. Tenant-Prefixed Routes (LocalizationAndMvcExtensions.cs)

**Added Discovery Pages to Multi-Tenant Routing:**
```csharp
var authPages = new[] { 
    "/Login", 
    "/LoginTotp", 
    "/Consent", 
    "/Index", 
    "/DiscoverTenant",  // <- Added
    "/SelectTenant"     // <- Added
};
```

**Effect:**
- Discovery pages now support both:
  - Root paths: `/DiscoverTenant`, `/SelectTenant`
  - Tenant-specific paths: `/t/{slug}/DiscoverTenant`, `/t/{slug}/SelectTenant`
- Maintains backward compatibility with non-multi-tenant deployments
- Tenant context automatically resolved from path

**Location:** `MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/LocalizationAndMvcExtensions.cs`

---

### 5. Login Page Enhancements

**Added Email Pre-fill Support (Login.cshtml.cs):**
```csharp
[BindProperty(SupportsGet = true)]
public string? Email { get; set; }

public bool ShowNotYouLink => !string.IsNullOrEmpty(Email);

public void OnGet()
{
    // Pre-fill username with email if provided
    if (!string.IsNullOrEmpty(Email))
    {
        Username = Email;
    }
}
```

**Login Page UI Updates (Login.cshtml):**
- Shows personalized message when email provided: "Signing in as **email@example.com**"
- Displays "Not you?" link that redirects back to `/DiscoverTenant`
- Pre-fills username field with email from query string
- Preserves `ReturnUrl` throughout flow

**Query Parameters Supported:**
- `?email=user@example.com` - Pre-fills username field
- `?returnUrl=/app` - Preserves original redirect target

**Location:** 
- `MrWhoOidc.WebAuth/Pages/Login.cshtml`
- `MrWhoOidc.WebAuth/Pages/Login.cshtml.cs`

---

## Service Registration Summary

| Service | Lifetime | Implementation | Location |
|---------|----------|----------------|----------|
| `ITenantDiscoveryService` | Scoped | `TenantDiscoveryService` | `MrWhoOidc.Auth/DependencyInjection.cs` |
| Session Storage | Singleton | `DistributedMemoryCache` | `LocalizationAndMvcExtensions.cs` |
| Rate Limiter | Singleton | ASP.NET Core Rate Limiting | `RateLimitingExtensions.cs` |

---

## Pages Summary

| Page | Route | Purpose | Rate Limit |
|------|-------|---------|------------|
| `DiscoverTenant` | `/DiscoverTenant` | Email input for tenant discovery | 5 req/min per IP |
| `SelectTenant` | `/SelectTenant` | Multi-tenant selection UI | None (protected by session) |
| `Login` | `/Login` | Login with email pre-fill | Standard login rate limit |

---

## Configuration Files

No `appsettings.json` changes required. All configuration is code-based:

### Session Configuration
- **Timeout**: 10 minutes (hardcoded)
- **Storage**: In-memory (distributed cache)
- **Cookie Name**: `.mrwhooidc.session`

### Rate Limiting
- **Discovery Endpoint**: 5 requests/minute per IP
- **Storage**: In-memory (partition by IP)
- **Policy Name**: `email-discovery`

### Multi-Tenancy
- Uses existing `MultiTenancy:Enabled` configuration
- No new settings required
- Routing automatically adapts based on configuration

---

## Testing Checklist

### Manual Testing
- [ ] Single tenant user flow (auto-redirect to login)
- [ ] Multi-tenant user flow (shows selection page)
- [ ] Rate limiting validation (6th request within 1 minute fails)
- [ ] Session expiration (10 minutes idle timeout)
- [ ] Email pre-fill on login page
- [ ] "Not you?" link redirects back to discovery
- [ ] ReturnUrl preserved throughout flow
- [ ] Tenant-prefixed routes work: `/t/{slug}/DiscoverTenant`

### Unit Testing (TODO)
- [ ] TenantDiscoveryService.FindTenantsByEmailAsync
- [ ] DiscoverTenant page model (0/1/2+ scenarios)
- [ ] SelectTenant page model (session retrieval, validation)
- [ ] Rate limiting enforcement

### Integration Testing (TODO)
- [ ] Full discovery flow end-to-end
- [ ] Multi-tenant user with localStorage preference
- [ ] Session persistence across pages
- [ ] Rate limit error handling

---

## Rollout Plan

### Phase 1: Internal Testing ✅
- All configuration complete
- Ready for local/Docker testing
- Verify against test users with multiple tenant memberships

### Phase 2: Beta Rollout (TODO)
- Deploy to staging environment
- Monitor rate limiting metrics
- Collect UX feedback on selection UI
- Validate session storage performance

### Phase 3: Production Rollout (TODO)
- Enable for all users
- Monitor session storage metrics
- Set up alerts for rate limiting violations
- Document user-facing instructions

---

## Troubleshooting

### Issue: Session Not Persisting
**Symptom**: SelectTenant page shows "Session expired or invalid"

**Solutions:**
1. Verify `UseSession()` is called in middleware pipeline
2. Check session cookie is being set (browser dev tools)
3. Verify `AddDistributedMemoryCache()` is called
4. Check session timeout hasn't expired (10 minutes)

### Issue: Rate Limiting Not Applied
**Symptom**: Can make unlimited discovery requests

**Solutions:**
1. Verify `[EnableRateLimiting("email-discovery")]` attribute on page model
2. Check rate limiting middleware is enabled in pipeline
3. Verify policy is registered in `AddRateLimitingPolicies()`
4. Check Redis connection if using distributed rate limiting

### Issue: Tenant Routes Not Working
**Symptom**: 404 on `/t/{slug}/DiscoverTenant`

**Solutions:**
1. Verify multi-tenancy is enabled in configuration
2. Check tenant-prefixed routes are registered in `LocalizationAndMvcExtensions.cs`
3. Verify `UseTenantResolution()` is called in middleware pipeline
4. Check tenant slug exists in database

---

## Performance Considerations

### Session Storage
- **Memory Usage**: ~1KB per active session
- **Expected Load**: 100 concurrent sessions = ~100KB
- **Cleanup**: Automatic after 10-minute idle timeout
- **Recommendation**: Monitor memory usage in production

### Rate Limiting
- **Storage**: In-memory dictionary (IP → counter)
- **Cleanup**: Automatic after 1-minute window expires
- **Expected Load**: ~1KB per unique IP
- **Recommendation**: Consider Redis for multi-instance deployments

### Tenant Discovery Query
- **Database Hits**: 1 query per discovery request
- **Indexes Used**: 
  - `Users.NormalizedEmail` (existing)
  - `UserAlternativeEmail.NormalizedEmail` (existing)
- **Cache TTL**: 5 minutes (in-memory)
- **Recommendation**: Monitor query performance in production

---

## Security Considerations

### Rate Limiting
- Protects against tenant enumeration attacks
- 5 requests/minute per IP address
- No bypass mechanism (even for authenticated users)

### Session Storage
- Secure cookie with HttpOnly, Secure flags
- SameSite=Lax prevents CSRF attacks
- 10-minute timeout limits exposure window

### Email Hashing
- Email addresses hashed in audit logs (SHA-256)
- Preserves privacy while enabling debugging
- Collision risk negligible for logging purposes

---

## Related Documentation

- [tenant-selection-START-HERE.md](./tenant-selection-START-HERE.md) - Quick start guide
- [tenant-selection-SUMMARY.md](./tenant-selection-SUMMARY.md) - Executive summary
- [tenant-selection-login-flow.md](./tenant-selection-login-flow.md) - Detailed technical spec
- [tenant-selection-quickref.md](./tenant-selection-quickref.md) - Developer reference
- [tenant-selection-diagrams.md](./tenant-selection-diagrams.md) - Visual flow diagrams

---

**Last Updated**: 2025 (Sprint 1 & 2 Configuration Complete)
