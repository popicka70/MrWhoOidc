# Identity Provider Discovery Check - Non-Blocking Validation

**Date**: October 10, 2025  
**Component**: `MrWhoOidc.Auth.Services.IdentityProviderValidator`  
**Issue**: HTTP discovery check was blocking provider saves on network failures

---

## Problem

When editing or creating an Identity Provider (IdP) with OIDC configuration, the `IdentityProviderValidator` attempted to validate the provider by fetching the OpenID Connect discovery document (`.well-known/openid-configuration`).

**Previous Behavior**:
- If the HTTP request failed (network timeout, DNS error, HTTP 4xx/5xx, etc.)
- The validation returned an error
- **The provider could not be saved**, even if the configuration was syntactically correct

This was problematic in scenarios such as:
- Configuring providers for development/testing environments that aren't yet reachable
- Network connectivity issues between the admin server and the IdP
- IdP temporarily down for maintenance
- Firewall/proxy blocking outbound HTTP requests

---

## Solution

**Changed HTTP discovery check to non-blocking**:

### Before (Blocking):
```csharp
try
{
    var client = httpClientFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(5);
    using var resp = await client.GetAsync(metadataUrl, ct).ConfigureAwait(false);
    if (!resp.IsSuccessStatusCode)
        return (false, $"Discovery failed: HTTP {(int)resp.StatusCode}");
}
catch (Exception ex)
{
    return (false, $"Discovery error: {ex.Message}");
}
```

### After (Non-Blocking):
```csharp
try
{
    var client = httpClientFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(5);
    using var resp = await client.GetAsync(metadataUrl, ct).ConfigureAwait(false);
    if (!resp.IsSuccessStatusCode)
    {
        logger.LogWarning(
            "Provider '{ProviderName}' discovery check failed: HTTP {StatusCode} from {Url}. Provider saved anyway.",
            provider.Name, 
            (int)resp.StatusCode, 
            metadataUrl);
    }
    else
    {
        logger.LogInformation(
            "Provider '{ProviderName}' discovery check successful: {Url}",
            provider.Name,
            metadataUrl);
    }
}
catch (Exception ex)
{
    logger.LogWarning(
        ex,
        "Provider '{ProviderName}' discovery check failed: {ErrorMessage} from {Url}. Provider saved anyway.",
        provider.Name,
        ex.Message,
        metadataUrl);
}
```

---

## Behavior Changes

### Validation Still Enforced For:
✅ **Name** - Required, max 150 characters, must be unique  
✅ **Config JSON** - Must be valid JSON and parseable as `OidcProviderConfig`  
✅ **DataAnnotation Validation** - All required fields (Authority, ClientId, etc.) validated  

### No Longer Blocking:
⚠️ **HTTP Discovery Check** - Network failures or HTTP errors are logged but don't prevent saving

---

## User Experience

### Admin UI (Pages/Admin/Providers/Edit.cshtml.cs)

**Before**:
```
❌ Error: Discovery failed: HTTP 503
(Provider not saved)
```

**After**:
```
✅ Provider updated.
(Saved successfully, warning logged in server logs)
```

### Admin API (POST /admin/api/providers)

**Before**:
```json
{
  "statusCode": 400,
  "title": "Validation failed",
  "detail": "Discovery error: Connection timed out"
}
```

**After**:
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

---

## Logging

Administrators can monitor discovery check failures through structured logs:

### Success Log (Information):
```
Provider 'AzureAD' discovery check successful: https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration
```

### Failure Log (Warning):
```
Provider 'TestIdP' discovery check failed: Connection timed out from https://test-idp.local/.well-known/openid-configuration. Provider saved anyway.
```

---

## Testing "Test Discovery" Feature

The admin UI still provides a **"Test Discovery"** button (`OnPostTestAsync`) that:
- Attempts to fetch the discovery document
- Shows success/failure in the UI
- **Does NOT block saving** (separate action)

This allows admins to:
1. Configure the provider with correct syntax
2. Save it (even if unreachable)
3. Test connectivity separately
4. Update when the IdP becomes available

---

## Migration Path

### For Existing Deployments:
- No database migration required
- No breaking changes to API contracts
- Behavior change is backwards-compatible (allows more saves, not fewer)

### For Administrators:
- Can now save providers that are temporarily unreachable
- Should monitor logs for persistent discovery failures
- Can use "Test Discovery" button to verify connectivity before enabling the provider

---

## Security Considerations

### Risk Assessment:
**Low Risk** - The change makes validation less strict but does not compromise security:

1. **Syntactic validation still enforced** - All required fields validated
2. **Runtime validation happens during actual auth flow** - When a user tries to authenticate, the discovery document is fetched fresh and validated
3. **Disabled providers don't process logins** - Admins can save but keep `Enabled = false` until tested
4. **Audit trail preserved** - All discovery failures logged with timestamps

### Best Practices:
- ✅ Use "Test Discovery" before enabling a provider
- ✅ Monitor logs for persistent discovery failures
- ✅ Keep providers disabled until verified working
- ✅ Use staging environments to test connectivity

---

## Rollback Plan

If the non-blocking behavior causes issues, revert by changing:

```csharp
// Make blocking again:
if (!resp.IsSuccessStatusCode)
    return (false, $"Discovery failed: HTTP {(int)resp.StatusCode}");
```

Then rebuild and redeploy. No database changes required.

---

## Related Files Modified

- ✅ `MrWhoOidc.Auth/Services/IdentityProviderValidator.cs`
  - Added `ILogger<IdentityProviderValidator>` parameter
  - Changed HTTP checks to log warnings instead of returning errors

---

## Testing Checklist

### Manual Testing:

- [ ] **Unreachable Authority**: Create provider with `Authority = "https://nonexistent.local"` → Should save with warning log
- [ ] **HTTP Error**: Create provider pointing to endpoint returning 503 → Should save with warning log
- [ ] **Timeout**: Create provider with slow authority (simulate with network delay) → Should save after 5s timeout with warning log
- [ ] **Valid Provider**: Create provider with real authority (e.g., Google, Azure AD) → Should save with success log
- [ ] **Test Discovery Button**: Use UI button to test connectivity → Should show result without blocking save
- [ ] **Logs Captured**: Verify structured logs contain provider name, URL, and error details

### Automated Testing:

Consider adding integration tests:

```csharp
[TestMethod]
public async Task ValidateAsync_AllowsSave_WhenDiscoveryFails()
{
    // Arrange
    var provider = new IdentityProvider
    {
        Name = "UnreachableIdP",
        Type = IdentityProviderType.Oidc,
        ConfigJson = JsonSerializer.Serialize(new OidcProviderConfig
        {
            Authority = "https://nonexistent.local",
            ClientId = "test",
            ClientSecret = "secret"
        })
    };

    // Act
    var (ok, error) = await validator.ValidateAsync(provider);

    // Assert
    Assert.IsTrue(ok, "Should allow save despite discovery failure");
    Assert.IsNull(error);
}
```

---

## Documentation Updates Required

- [x] Create this implementation guide
- [ ] Update admin user guide (`docs/admin-guide.md`) - explain discovery check is non-blocking
- [ ] Update developer guide (`docs/developer-guide.md`) - document logging patterns
- [ ] Update troubleshooting section - how to diagnose discovery failures from logs

---

## Success Metrics

| Metric | Expected Result |
|--------|----------------|
| Build Status | ✅ Success (verified) |
| Breaking Changes | None |
| User Impact | Positive (fewer blocked saves) |
| Log Quality | Warnings clearly indicate non-blocking failures |

---

## Contributors

- **Implementation**: GitHub Copilot (with human oversight)
- **Testing**: [Pending]
- **Review**: [Pending]

---

## References

- Modified File: `MrWhoOidc.Auth/Services/IdentityProviderValidator.cs`
- Admin Pages: `MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs`
- Admin API: `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/AdminApiEndpointMappingExtensions.cs`
- OIDC Discovery Spec: [OpenID Connect Discovery 1.0](https://openid.net/specs/openid-connect-discovery-1_0.html)

---

**Status**: ✅ Complete  
**Build Verified**: ✅ MrWhoOidc.Auth + MrWhoOidc.WebAuth  
**Ready For**: Testing & Deployment
