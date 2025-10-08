# URL Configuration Fix - Summary

## Problem Identified

The IdP chaining URLs were showing `https://localhost:7208` instead of the correct Docker port `8443` because:

1. **No explicit `Oidc__Issuer` configured** in docker-compose.yml
2. System fell back to **request context** which varied depending on how the app was accessed
3. Port 7208 was coming from `appsettings.Development.json` (used when running outside Docker)

## How URL Determination Works

### Decision Flow
```
1. Check if Oidc.Issuer is configured (appsettings or env var)
   ├─ YES → Use that exact value
   └─ NO  → Fall back to request context (Request.Scheme + Request.Host)

2. Pass to IIssuerBuilder.BuildIssuer()
   ├─ Multi-tenant enabled  → Add /t/{tenant-slug}
   └─ Single-tenant mode    → Return as-is

3. Append endpoint path (/authorize or /connect/endsession)
```

### Code Path
```csharp
// Edit.cshtml.cs (line 78)
var issuer = HttpContext.GetIssuer(oidcOptions);
                ↓
// HttpContextExtensions.cs (line 21)
if (!string.IsNullOrEmpty(options.Issuer))
    return options.Issuer;  // ← Use configured value
else
    // Fall back to request context
    var issuerBuilder = httpContext.RequestServices.GetRequiredService<IIssuerBuilder>();
    var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
    return issuerBuilder.BuildIssuer(baseUrl);
```

## Solution Applied

### Change Made to `docker-compose.yml`

**Added explicit issuer configuration**:
```yaml
environment:
  # OIDC configuration
  Oidc__Issuer: "https://localhost:8443"
```

This ensures:
✅ URLs always show correct port (8443)
✅ Consistent URLs regardless of access method
✅ Predictable configuration for IdP chaining setup

### Complete Environment Section
```yaml
environment:
  ASPNETCORE_ENVIRONMENT: Development
  ASPNETCORE_URLS: https://+:8443
  ASPNETCORE_HTTPS_PORTS: 8443
  ConnectionStrings__authdb: Host=postgres;Port=5432;Database=authdb;...
  ConnectionStrings__redis: redis:6379,abortConnect=false
  ASPNETCORE_Kestrel__Certificates__Default__Path: /https/aspnetapp.pfx
  ASPNETCORE_Kestrel__Certificates__Default__Password: changeit
  # OIDC configuration
  Oidc__Issuer: "https://localhost:8443"  ← NEW
  # Multi-tenancy configuration
  MultiTenancy__Enabled: "true"
  MultiTenancy__DefaultTenantSlug: "default"
```

## Expected Results After Restart

### Before (Without Oidc__Issuer)
```
Authorization URL: https://localhost:7208/authorize
End Session URL:   https://localhost:7208/connect/endsession
```
*Incorrect port, varying based on access method*

### After (With Oidc__Issuer)
```
Authorization URL: https://localhost:8443/authorize
End Session URL:   https://localhost:8443/connect/endsession
```
*Correct port, consistent and predictable*

### Multi-Tenant Example (Tenant: "acme")
```
Authorization URL: https://localhost:8443/t/acme/authorize
End Session URL:   https://localhost:8443/t/acme/connect/endsession
```

## To Apply the Change

### Option 1: Restart Services
```bash
docker compose down
docker compose up -d --build
```

### Option 2: Recreate WebAuth Service Only
```bash
docker compose up -d --force-recreate webauth
```

### Option 3: Full Clean Restart
```bash
docker compose down -v  # Remove volumes too
docker compose up -d --build
```

## Verification Steps

1. **Wait for services to start** (~30 seconds)
2. **Access admin interface**: https://localhost:8443/Admin/Clients/Edit/{client-id}
3. **Click Providers tab**
4. **Check "IdP Chaining Configuration URLs" section**
5. **Verify URLs show**: `https://localhost:8443/...`

## Configuration for Different Environments

### Local Development (Current)
```yaml
Oidc__Issuer: "https://localhost:8443"
```

### Production with Custom Domain
```yaml
Oidc__Issuer: "https://auth.example.com"
```

### Production Behind Reverse Proxy
```yaml
Oidc__Issuer: "https://auth.example.com"
ASPNETCORE_URLS: "http://+:80"  # Internal container port
```

### Staging Environment
```yaml
Oidc__Issuer: "https://staging-auth.example.com"
```

## Key Takeaways

1. **Always set `Oidc__Issuer` explicitly** in docker-compose for predictable URLs
2. **Match the issuer to how users access the service** (external URL, not internal)
3. **Include port number** if non-standard (not 80/443)
4. **Request context fallback** is for development convenience only

## Files Modified

1. ✅ `docker-compose.yml` - Added `Oidc__Issuer: "https://localhost:8443"`

## Files Created

1. 📄 `docs/idp-chaining-urls-configuration.md` - Complete configuration guide

## Related Documentation

- **Feature Documentation**: `docs/idp-chaining-urls-feature.md`
- **Configuration Guide**: `docs/idp-chaining-urls-configuration.md`
- **Quick Reference**: `docs/idp-chaining-urls-quickref.md`
- **Visual Reference**: `docs/idp-chaining-urls-visual-reference.md`

## Priority Configuration

| Method | Priority | Recommended For |
|--------|----------|----------------|
| `Oidc__Issuer` (docker-compose) | **Highest** ✅ | Production, Docker |
| `Oidc.Issuer` (appsettings) | High | Default config |
| Request context | Lowest | Dev fallback only |

---

**Next Steps**: 
1. Restart docker-compose services
2. Verify URLs show correct port 8443
3. Test copy-to-clipboard functionality
4. Document working configuration for team
