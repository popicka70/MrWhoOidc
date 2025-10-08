# IdP Chaining URLs - Configuration Guide

## How URLs Are Determined

The IdP chaining URLs displayed in the admin interface are built using this logic:

```
User accesses page
    ↓
HttpContext.GetIssuer(oidcOptions) is called
    ↓
Is OidcOptions.Issuer configured? ─────┐
    │                                   │
    YES                                 NO
    │                                   │
    Use configured issuer               Use request context
    from appsettings                    (Request.Scheme + Request.Host)
    │                                   │
    └───────────────┬───────────────────┘
                    ↓
            IIssuerBuilder.BuildIssuer(baseUrl)
                    ↓
            Is multi-tenant enabled? ───┐
                │                        │
                YES                      NO
                │                        │
                Add /t/{tenant-slug}     Return base URL
                │                        │
                └───────────┬────────────┘
                            ↓
                    Final issuer URL
                            ↓
                    Append endpoint path
                    (/authorize or /connect/endsession)
```

## Configuration Priority

### 1. **Explicit Configuration (Highest Priority)**
Set `Oidc.Issuer` in appsettings or environment variables:

**appsettings.json**:
```json
{
  "Oidc": {
    "Issuer": "https://auth.example.com"
  }
}
```

**Environment Variable**:
```bash
Oidc__Issuer=https://auth.example.com
```

**docker-compose.yml**:
```yaml
environment:
  Oidc__Issuer: "https://auth.example.com"
```

### 2. **Request Context (Fallback)**
If `Oidc.Issuer` is not set, uses the incoming HTTP request:
- Scheme: `http` or `https`
- Host: hostname + port (e.g., `localhost:8443`, `auth.example.com`)

## Docker Compose Configuration

### Problem: Port Mismatch
When running in Docker, the container's internal port may differ from the external mapped port:
- **Internal**: Container listens on port 8443
- **External**: Host maps `8443:8443`
- **Access**: Browser uses `https://localhost:8443`

If `Oidc__Issuer` is not set, URLs will use the request host, which may be incorrect for external configuration.

### Solution: Explicit Issuer Configuration

**For localhost development**:
```yaml
services:
  webauth:
    environment:
      Oidc__Issuer: "https://localhost:8443"
```

**For production with custom domain**:
```yaml
services:
  webauth:
    environment:
      Oidc__Issuer: "https://auth.example.com"
```

**For production with reverse proxy**:
```yaml
services:
  webauth:
    environment:
      Oidc__Issuer: "https://auth.example.com"  # External URL users see
      ASPNETCORE_URLS: "http://+:80"             # Internal container port
```

## Multi-Tenancy Impact

When multi-tenancy is enabled (`MultiTenancy__Enabled: "true"`), the issuer builder automatically appends the tenant path:

### Single-Tenant Mode
```
Configured: https://localhost:8443
Result:     https://localhost:8443/authorize
```

### Multi-Tenant Mode (Tenant: "acme")
```
Configured: https://localhost:8443
Result:     https://localhost:8443/t/acme/authorize
```

### Multi-Tenant Mode (Tenant: "contoso")
```
Configured: https://localhost:8443
Result:     https://localhost:8443/t/contoso/authorize
```

## Configuration Examples

### Example 1: Local Development (Docker Compose)
```yaml
services:
  webauth:
    environment:
      ASPNETCORE_URLS: https://+:8443
      Oidc__Issuer: "https://localhost:8443"
      MultiTenancy__Enabled: "false"
    ports:
      - "8443:8443"
```

**Result**:
- Authorization URL: `https://localhost:8443/authorize`
- End Session URL: `https://localhost:8443/connect/endsession`

### Example 2: Local Development (Multi-Tenant)
```yaml
services:
  webauth:
    environment:
      ASPNETCORE_URLS: https://+:8443
      Oidc__Issuer: "https://localhost:8443"
      MultiTenancy__Enabled: "true"
    ports:
      - "8443:8443"
```

**Result for tenant "acme"**:
- Authorization URL: `https://localhost:8443/t/acme/authorize`
- End Session URL: `https://localhost:8443/t/acme/connect/endsession`

### Example 3: Production (Behind Reverse Proxy)
```yaml
services:
  webauth:
    environment:
      ASPNETCORE_URLS: http://+:80
      Oidc__Issuer: "https://auth.example.com"
      MultiTenancy__Enabled: "true"
    # No ports section - handled by reverse proxy
```

**Result for tenant "customer1"**:
- Authorization URL: `https://auth.example.com/t/customer1/authorize`
- End Session URL: `https://auth.example.com/t/customer1/connect/endsession`

### Example 4: Production (Direct HTTPS)
```yaml
services:
  webauth:
    environment:
      ASPNETCORE_URLS: https://+:443
      Oidc__Issuer: "https://auth.example.com"
      MultiTenancy__Enabled: "true"
    ports:
      - "443:443"
    volumes:
      - ./certs:/https:ro
```

**Result for tenant "partner"**:
- Authorization URL: `https://auth.example.com/t/partner/authorize`
- End Session URL: `https://auth.example.com/t/partner/connect/endsession`

## Troubleshooting

### URLs Show Wrong Port
**Symptom**: URLs display `https://localhost:7208` instead of `https://localhost:8443`

**Cause**: `Oidc__Issuer` not set in docker-compose, falling back to request context from VS debugger or old appsettings

**Fix**: Add to docker-compose.yml:
```yaml
environment:
  Oidc__Issuer: "https://localhost:8443"
```

### URLs Missing Tenant Path
**Symptom**: Multi-tenant deployment but URLs don't include `/t/{slug}`

**Cause**: `MultiTenancy__Enabled` is false or not set

**Fix**: Verify multi-tenancy is enabled:
```yaml
environment:
  MultiTenancy__Enabled: "true"
```

### URLs Use HTTP Instead of HTTPS
**Symptom**: URLs display `http://` instead of `https://`

**Cause**: Request context shows HTTP (reverse proxy forwarding issue or explicit config)

**Fix Option 1** - Explicit HTTPS issuer:
```yaml
environment:
  Oidc__Issuer: "https://auth.example.com"
```

**Fix Option 2** - Configure forwarded headers (for reverse proxy):
```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto
});
```

### URLs Show Internal Container Hostname
**Symptom**: URLs display internal Docker hostname instead of external domain

**Cause**: Using request context from internal container network

**Fix**: Always set explicit issuer for production:
```yaml
environment:
  Oidc__Issuer: "https://auth.example.com"
```

## Best Practices

### Development
✅ **DO**: Set explicit issuer matching your access URL
```yaml
Oidc__Issuer: "https://localhost:8443"
```

✅ **DO**: Match ports between container and host mapping
```yaml
ports:
  - "8443:8443"  # External:Internal match
```

### Production
✅ **DO**: Always set explicit issuer for predictable URLs
```yaml
Oidc__Issuer: "https://auth.example.com"
```

✅ **DO**: Use environment-specific configuration
```yaml
# docker-compose.prod.yml
Oidc__Issuer: "https://auth.example.com"

# docker-compose.dev.yml  
Oidc__Issuer: "https://localhost:8443"
```

❌ **DON'T**: Rely on request context in production
❌ **DON'T**: Include port numbers in production issuer URLs (use standard 443)
❌ **DON'T**: Mix HTTP and HTTPS in configuration

## Verification

After configuration, verify the URLs by:

1. **Access admin interface**: `https://localhost:8443/Admin/Clients/Edit/{id}`
2. **Check Providers tab**: View "IdP Chaining Configuration URLs"
3. **Verify URLs match expected format**:
   - Scheme: http or https
   - Host: correct hostname/domain
   - Port: included if non-standard (not 80/443)
   - Path: `/t/{slug}` if multi-tenant, else none
   - Endpoint: `/authorize` or `/connect/endsession`

## Related Files

- **Configuration Source**: `MrWhoOidc.WebAuth/appsettings.json`
- **Docker Override**: `docker-compose.yml`
- **Issuer Builder**: `MrWhoOidc.Auth/MultiTenancy/IssuerBuilder.cs`
- **Extension Method**: `MrWhoOidc.WebAuth/Extensions/HttpContextExtensions.cs`
- **Page Model**: `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs`

## Summary

| Configuration Method | Priority | Use Case |
|---------------------|----------|----------|
| `Oidc__Issuer` env var | Highest | Docker, production, predictable URLs |
| `Oidc.Issuer` appsettings | High | Default configuration |
| Request context | Lowest | Development fallback |

**Recommendation**: Always set `Oidc__Issuer` explicitly in docker-compose.yml for consistent, predictable IdP chaining URLs.
