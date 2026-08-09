# Security Review — MrWhoOidc (June 2026)

**Scope:** Full security review of the `MrWhoOidc/` nested repo (OIDC Identity Provider).
**Date:** 2026-06-24
**Reviewer:** GitHub Copilot automated review
**Status:** All critical, medium, and low findings fixed on 2026-06-25. See "Fix Applied" notes per finding.

---

## Executive Summary

MrWhoOidc is a production-grade OIDC IdP with a generally strong security posture. The codebase demonstrates mature security practices: Argon2id password hashing, PKCE enforcement, algorithm confusion prevention, DPoP support, comprehensive rate limiting, CSRF protection, hardened cookies, and strict redirect URI validation.

However, **one critical vulnerability** and several medium-severity issues were identified that should be addressed before production deployment.

### Findings Summary

| # | Severity | Finding | Location |
|---|----------|---------|----------|
| 1 | 🔴 CRITICAL | Open redirect in `LocalLogoutHandler` — `returnUrl` from query string used directly in `Results.Redirect()` without local-URL validation | `MrWhoOidc.WebAuth/Handlers/Logout/LocalLogoutHandler.cs:19` |
| 2 | 🟡 MEDIUM | Cross-tenant IDOR in `ProviderAndBclEndpoints` — client provider mappings and client JWKS endpoints lack explicit tenant checks, relying solely on EF query filters | `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/ProviderAndBclEndpoints.cs:115-200, 510-555` |
| 3 | 🟡 MEDIUM | `ApiService` admin endpoints lack explicit tenant filters — rely solely on EF query filters with no defense-in-depth | `MrWhoOidc.ApiService/Program.cs:252-438` |
| 4 | 🟡 MEDIUM | ~~DataProtection key-ring stored unencrypted in DB by default~~ **✅ Resolved** — app now fails closed in production unless a certificate is provided or the risk is explicitly accepted | `MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/SecurityCoreExtensions.cs:54-78` |
| 5 | 🟡 MEDIUM | `RegistrationHandler` does not reject non-http(s) schemes for redirect URIs — `javascript:` and `data:` schemes pass through | `MrWhoOidc.WebAuth/Handlers/RegistrationHandler.cs:135-155` |
| 6 | 🟡 MEDIUM | SVG logo upload enables stored XSS — SVGs with embedded `<script>` are served from same origin with `image/svg+xml` content type | `MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml.cs:274` + logo endpoint |
| 7 | 🟡 MEDIUM | `NetworkSecurity.IsInternal` does not block `0.0.0.0` or `::` (unspecified addresses) — potential SSRF bypass | `MrWhoOidc.Auth/Utils/NetworkSecurity.cs:69-90` |
| 8 | 🟡 MEDIUM | `TokenValidator` skips audience validation when `validAudiences` is not provided — defense-in-depth gap | `MrWhoOidc.Auth/Services/TokenValidator.cs:57-60` |
| 9 | 🟡 MEDIUM | Potential open redirect via protocol-relative URL in federated callback — `SanitizeLocalReturn` accepts `//evil.com` as relative | `MrWhoOidc.WebAuth/Handlers/FederatedLogout.cs:243-247` → `FederatedCallbackHandler.cs:50` |
| 10 | 🟡 MEDIUM | `Testing:AllowLocalExternalOidcHttp` disables all SSRF protections for external IdP calls — dangerous if accidentally enabled in production | All external OIDC handlers |
| 11 | 🟡 MEDIUM | Hardcoded PostgreSQL password and client secret in tracked `docker-compose.dev.yml` | `docker-compose.dev.yml:15,49` |
| 12 | 🟡 LOW ✅ Fixed | Refresh token rotation lacks atomic claim — concurrent requests could double-issue | `MrWhoOidc.Auth/Services/Token/RefreshTokenExchanger.cs:50-57` |
| 13 | 🟡 LOW ✅ Fixed | `WebhookAlertPublisher` and `LicensingEntitlementsClient` use default HttpClient without SSRF protection | `Alerting.cs:32`, `LicensingEntitlementsClient.cs:53` |
| 14 | 🟡 LOW ✅ Fixed | `certs/` directory not in `.gitignore` — dev certificates could be committed | `.gitignore` (missing pattern) |
| 15 | 🟡 LOW ✅ Fixed | BCL outbox endpoints on tenant-admin group lack explicit tenant filtering | `ProviderAndBclEndpoints.cs:660-680` |

---

## Detailed Findings

### 🔴 1. Open Redirect in `LocalLogoutHandler` (CRITICAL)

**File:** `MrWhoOidc.WebAuth/Handlers/Logout/LocalLogoutHandler.cs:14-20`

```csharp
public async Task<IResult> ExecuteAsync(HttpContext http, string? returnUrl)
{
    await http.SignOutAsync().ConfigureAwait(false);
    var destination = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
    return Results.Redirect(destination);
}
```

**Vulnerability:** The `returnUrl` parameter comes directly from the query string via `LogoutRequest.FromQuery(http.Request.Query)` (see `LogoutHandler.cs:25-26`). It is passed to `Results.Redirect()` **without any validation** that it is a local URL. An attacker can craft:

```
https://idp.example.com/logout?returnUrl=https://evil.example.com
```

After signing the user out, the IdP redirects them to `https://evil.example.com`.

**Attack paths:**
1. `GET /logout?returnUrl=https://evil.com` → `LogoutHandler.LocalLogoutAsync()` → `LocalLogoutHandler.ExecuteAsync()`
2. `GET /logout?returnUrl=https://evil.com` → `LogoutHandler.LogoutEntryAsync()` → `FederatedLogoutEntryHandler.ExecuteAsync()` → falls back to `localLogout.ExecuteAsync(http, request.ReturnUrl)` when federation is disabled or user can't federate

**Impact:**
- Phishing: User thinks they're still on the IdP domain after logout
- OAuth token theft via redirect_uri confusion
- Bypassing `redirect_uri` allow-list validation (logout endpoint has no such checks)

**Contrast:** The `Logout/Prompt/Index.cshtml.cs:56` page correctly validates: `if (string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl)) returnUrl = "/";`. The `FederatedLogout.SanitizeLocalReturn()` method also validates relative-only. But `LocalLogoutHandler` does neither.

**Fix:** Add `Url.IsLocalUrl()` validation:
```csharp
public async Task<IResult> ExecuteAsync(HttpContext http, string? returnUrl)
{
    await http.SignOutAsync().ConfigureAwait(false);
    var destination = string.IsNullOrEmpty(returnUrl) || !http.Request.Path.HasValue
        ? "/"
        : returnUrl;
    // Validate local URL only
    if (!IsLocalUrl(returnUrl, http))
        destination = "/";
    return Results.Redirect(destination);
}
```

Or simpler, inject `IUrlHelper`/use `PathString`:
```csharp
var destination = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
if (Uri.TryCreate(destination, UriKind.Absolute, out _))
    destination = "/"; // block absolute external redirects
return Results.Redirect(destination);
```

---

### 🟡 2. Cross-Tenant IDOR in `ProviderAndBclEndpoints` (MEDIUM)

**File:** `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/ProviderAndBclEndpoints.cs`

These endpoints are on the `tenant-admin` group but do not explicitly validate that the `clientId` belongs to the current tenant:

| Endpoint | Line | Issue |
|---|---|---|
| `GET /clients/{clientId}/providers` | 115 | No tenant check on client lookup |
| `POST /clients/{clientId}/providers` | 130 | Client lookup without tenant filter |
| `PUT /clients/{clientId}/providers/{idpId}` | 165 | No tenant check — can modify provider mappings for any tenant's client |
| `DELETE /clients/{clientId}/providers/{idpId}` | 185 | No tenant check — can delete provider mappings for any tenant's client |
| `GET /clients/{clientId}/keys` | 510 | No tenant check — can read JWKS of any tenant's client |
| `PUT /clients/{clientId}/keys` | 525 | **No tenant check — can modify JWKS of any tenant's client** (most severe: enables token forgery) |

**Mitigating factor:** EF Core global query filters on `Client` (`ApplyRequiredTenantFilter`) should filter out cross-tenant clients, returning null → 404. But this is the **sole protection** — there is no explicit `.Where(c => c.TenantId == currentTenantId)` check.

**Risk:** If `ITenantAccessor.CurrentTenant` is ever null (middleware skip path, bug in tenant resolution), the query filter is disabled and all data is exposed.

**Fix:** Use the existing `VerifyClientAccess`/`VerifyMutableClientAccess` helpers from `AdminApiEndpointMappingExtensions.cs`, or inline:
```csharp
var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value, ct);
if (client is null) return Results.NotFound();
```

---

### 🟡 3. `ApiService` Admin Endpoints Lack Explicit Tenant Filters (MEDIUM)

**File:** `MrWhoOidc.ApiService/Program.cs`

Multiple endpoints rely solely on the EF query filter with no explicit `.Where(... TenantId == currentTenantId)`:

| Endpoint | Line | Issue |
|---|---|---|
| `GET /admin/clients` | ~252 | Lists clients — relies on EF filter |
| `PUT /admin/clients/{id}` | ~310 | Loads by `c.Id == id` — no explicit tenant check before modifying |
| `GET /admin/users` | ~375 | Lists users — relies on EF filter |
| `GET /admin/users/{id}` | ~388 | Loads by `u.Id == id` — no explicit tenant check |
| `PUT /admin/users/{id}` | ~418 | Loads by `u.Id == id` — no explicit tenant check before modifying |
| `DELETE /admin/users/{id}` | ~438 | Loads by `u.Id == id` — no explicit tenant check |

**Mitigating factor:** The inline middleware (lines 96-133) sets `ITenantAccessor.CurrentTenant` for `/admin/*` paths, so the EF filter should apply. But there's no defense-in-depth.

**Fix:** Add explicit `TenantId == currentTenantId` to all read/update/delete queries.

---

### 🟡 4. DataProtection Key-Ring Stored Unencrypted in DB (MEDIUM) — ✅ Resolved

**File:** `MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/SecurityCoreExtensions.cs:54-78`

DataProtection keys are persisted to the database via `PersistKeysToDbContext<AuthDbContext>()`. Certificate-based encryption of the key-ring is supported via `DataProtection:CertificatePath` + `DataProtection:CertificatePassword`.

**Resolution:** The application now **fails closed** in production — it refuses to start unless either:
- `DataProtection:CertificatePath` (and `DataProtection:CertificatePassword`) is set to a valid X.509 certificate, **or**
- `DataProtection:AllowUnencryptedKeyRingInProduction=true` is explicitly set (opt-in, acknowledging the risk).

The `docker-compose.yml` and `.env.example` now wire these through as `DATAPROTECTION_CERTIFICATE_PATH`, `DATAPROTECTION_CERTIFICATE_PASSWORD`, and `DATAPROTECTION_ALLOW_UNENCRYPTED_KEY_RING`. See `docs/deployment-guide.md` and `docs/production-setup-guide.md` for configuration instructions.

**Previous impact (before fix):** If an attacker gains database access, they could read both the DataProtection-encrypted signing key JWK JSON **and** the DataProtection key-ring needed to decrypt it, defeating the purpose of encrypting signing keys at rest.

---

### 🟡 5. `RegistrationHandler` Does Not Reject Non-http(s) Schemes (MEDIUM)

**File:** `MrWhoOidc.WebAuth/Handlers/RegistrationHandler.cs:135-155`

Dynamic client registration validates that `http` scheme is only allowed for `localhost`, but does **not** explicitly reject non-http(s) schemes like `javascript:` or `data:`. `Uri.TryCreate()` accepts `javascript:` as an absolute URI, and the check only blocks `http` for non-localhost — it doesn't reject other schemes.

**Fix:** Add explicit scheme validation:
```csharp
if (parsedUri.Scheme != "http" && parsedUri.Scheme != "https")
    return Results.Json(new { error = "invalid_redirect_uri", ... });
```

Or use `UrlComparison.IsValidAbsolute()` which already restricts to http/https.

---

### 🟡 6. SVG Logo Upload Enables Stored XSS (MEDIUM)

**Files:**
- `MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml.cs:274` — allows `.svg` extension
- Logo endpoint at `EndpointMappingExtensions.cs:362` — serves with `Content-Type: image/svg+xml`

**Vulnerability:** An admin uploads an SVG containing `<script>alert(document.cookie)</script>`. The logo is served from `/api/providers/{id}/logo` with `Content-Type: image/svg+xml`. While `<img>` tags block script execution in SVGs, direct URL navigation renders the SVG as a full page with script execution from the same origin.

**Fix:** Either:
1. Remove `.svg` from allowed upload extensions, OR
2. Serve SVGs with `Content-Disposition: attachment` and `Content-Security-Policy: default-src 'none'` header, OR
3. Sanitize SVG content to strip `<script>` tags

---

### 🟡 7. `NetworkSecurity.IsInternal` Does Not Block `0.0.0.0` / `::` (MEDIUM)

**File:** `MrWhoOidc.Auth/Utils/NetworkSecurity.cs:69-90`

The SSRF protection blocks loopback, RFC 1918 private ranges, link-local, and carrier-grade NAT. However, `0.0.0.0` (IPv4 unspecified) and `::` (IPv6 unspecified) are **not** explicitly blocked. On most OSes, connecting to `0.0.0.0` is treated as `127.0.0.1`, so this could be a bypass vector.

**Fix:**
```csharp
if (ip.Equals(IPAddress.Any)) return true;      // 0.0.0.0
if (ip.Equals(IPAddress.IPv6Any)) return true;  // ::
```

---

### 🟡 8. `TokenValidator` Skips Audience Validation When Not Provided (MEDIUM)

**File:** `MrWhoOidc.Auth/Services/TokenValidator.cs:57-60`

```csharp
ValidateAudience = expectedAudiences.Length > 0,
AudienceValidator = expectedAudiences.Length > 0
    ? null
    : static (_, _, _) => true,
```

When `validAudiences` is not provided by the caller, audience validation is completely skipped. `TokenExchangeService.cs:103` and `JwtTokenIntrospector.cs:31` call `ValidateAsync` without audiences. Compensating controls exist (DB lookup, audience policy), but this is a defense-in-depth gap — any future caller that forgets the post-check will accept tokens with arbitrary audiences.

**Fix:** Make audience validation mandatory, or at minimum log a warning when it's skipped.

---

### 🟡 9. Potential Open Redirect via Protocol-Relative URL (MEDIUM)

**File:** `MrWhoOidc.WebAuth/Handlers/FederatedLogout.cs:243-247`

```csharp
private static string SanitizeLocalReturn(string? url)
{
    if (string.IsNullOrWhiteSpace(url)) return "/";
    if (Uri.TryCreate(url, UriKind.Relative, out _)) return url; // keep relative only
    return "/"; // disallow absolute external
}
```

`Uri.TryCreate("//evil.com", UriKind.Relative, out _)` returns `true` in .NET, meaning a protocol-relative URL would pass this check. The result is then used in `FederatedCallbackHandler.cs:50`:
```csharp
return Results.Redirect(validation.ReturnUrl);
```

`Results.Redirect("//evil.com")` would redirect to `https://evil.com` (protocol-relative).

**Fix:** Reject URLs starting with `//`:
```csharp
if (url.StartsWith("//", StringComparison.Ordinal)) return "/";
```

The `RazorClient` example already does this correctly at `Logout.cshtml.cs:110`.

---

### 🟡 10. `Testing:AllowLocalExternalOidcHttp` Disables SSRF Protection (MEDIUM)

**File:** All external OIDC handlers (`ExternalOidcDiscoveryService.cs`, `ExternalOidcTokenExchangeService.cs`, `ExternalOidcRequestBuilder.cs`, `ExternalOidcTokenValidator.cs`)

When `Testing:AllowLocalExternalOidcHttp` is `true`, all external OIDC handlers use `_httpFactory.CreateClient()` (default, no SSRF protection) instead of `NetworkSecurity.CreateSafeHttpClient()`. This is safe by default (flag is OFF), but if accidentally enabled in production, all SSRF protections for external IdP calls are bypassed.

**Fix:** Guard with environment assertion:
```csharp
if (allowLocal && !env.IsDevelopment())
    throw new InvalidOperationException("AllowLocalExternalOidcHttp cannot be enabled outside Development");
```

---

### 🟡 11. Hardcoded Secrets in Tracked `docker-compose.dev.yml` (MEDIUM)

**File:** `docker-compose.dev.yml`

| Line | Secret | Value |
|---|---|---|
| 15 | PostgreSQL password | `oidcPass!` |
| 49 | Client secret | `z1bvxwNcBXeOP03EMUdawfHnBhx6KAXuYArRSY6a1ZPyme7JMJ_A50bQY75FW6TG` |
| 18 | Dev cert password | `changeit` |

These are dev-only but committed to the repository. The production `docker-compose.yml` correctly uses env var substitution.

**Fix:** Use `.env` file (git-ignored) for dev compose secrets, or document that these are dev-only throwaway values.

---

### 🟡 12. Refresh Token Rotation Lacks Atomic Claim (LOW)

**File:** `MrWhoOidc.Auth/Services/Token/RefreshTokenExchanger.cs:50-57`

The reuse detection only fires when `tokenEntity.RevokedAt != null`. But the old token is revoked at line 201 *after* the new token is created. If two concurrent requests race with the same valid refresh token, both could pass the `RevokedAt == null` check before either revokes it. Unlike authorization codes (which use `ExecuteUpdateAsync` with conditional WHERE), refresh tokens lack this race protection.

**Fix:** Add atomic conditional update before issuing the new token:
```csharp
var claimed = await db.Tokens
    .Where(t => t.Id == tokenEntity.Id && t.RevokedAt == null)
    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), ct);
if (claimed == 0)
{
    await revocations.RevokeRefreshTokenFamilyAsync(tokenEntity.Id, ct);
    return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
}
```

---

### 🟡 13. Missing SSRF Protection on Alert Webhook and Licensing Client (LOW)

**Files:**
- `MrWhoOidc.WebAuth/Observability/Alerting.cs:32` — uses `httpFactory.CreateClient()` (default, no safe handler)
- `MrWhoOidc.Auth/Entitlements/LicensingEntitlementsClient.cs:53,99` — injected HttpClient with no safe handler

Both use operator-configured URLs (not tenant-controlled), so risk is low. But for defense-in-depth:

**Fix:** Register with safe handlers:
```csharp
services.AddHttpClient("alert-webhook")
    .ConfigurePrimaryHttpMessageHandler(NetworkSecurity.CreateSafeHandler);
```

---

### 🟡 14. `certs/` Not in `.gitignore` (LOW)

**File:** `.gitignore` (missing `certs/` or `*.pfx` pattern)

`certs/aspnetapp.pfx` is not excluded by `.gitignore`. If committed, the dev HTTPS certificate (with password `changeit`) would be in the repo.

**Fix:** Add `certs/*.pfx` or `certs/` to `.gitignore`.

---

### 🟡 15. BCL Outbox Endpoints Lack Explicit Tenant Filtering (LOW)

**File:** `ProviderAndBclEndpoints.cs:660-680`

`GET /bcl/outbox` and `POST /bcl/outbox/{id}/retry` are on the `tenant-admin` group but have no explicit tenant filter. The EF query filter on `BackchannelLogoutNotification` (required filter) should apply, but there's no defense-in-depth.

**Fix:** Move to `platform-admin` group, or add explicit tenant filtering.

---

## What's Done Well ✅

| Control | Status | Notes |
|---|---|---|
| **Password hashing** | ✅ Argon2id | 128MB memory cost, 4 iterations, 16-byte salt, `FixedTimeEquals` |
| **PKCE enforcement** | ✅ S256-only | Enforced for all public clients at `/authorize` |
| **Authorization code single-use** | ✅ Atomic claim | `ExecuteUpdateAsync` with conditional WHERE + reuse detection + token revocation |
| **Refresh token rotation** | ✅ Family revocation | Lineage tracking + family-wide revocation on reuse |
| **Algorithm confusion prevention** | ✅ Asymmetric-only | `ValidAlgorithms` pinned to RSA/ECDSA/PS in all validators; `alg=none` rejected |
| **Client authentication** | ✅ All grants | Enforced globally before grant dispatch; `client_secret` required for `client_credentials` and `token_exchange` |
| **No ROPC grant** | ✅ Not implemented | Resource Owner Password Credentials grant is not supported |
| **CORS** | ✅ Fail-closed | `DisallowCredentials()`, exact origin matching, no wildcards |
| **Cookies** | ✅ Hardened | All use `__Host-` prefix, HttpOnly, Secure, SameSite=Lax |
| **CSRF** | ✅ Global | `AutoValidateAntiforgeryTokenAttribute` on all unsafe methods |
| **DPoP** | ✅ RFC 9449 | Replay cache, nonce enforcement, algorithm allowlist, `ath` validation |
| **Security headers** | ✅ Strong | CSP with nonce, HSTS preload, X-Frame-Options DENY, nosniff |
| **Rate limiting** | ✅ Comprehensive | Both ASP.NET Core + Redis distributed; token, authorize, admin, userinfo, introspect, PAR, logout all rate-limited |
| **SQL injection** | ✅ No raw SQL | All queries use LINQ with parameterized translation |
| **Command injection** | ✅ No Process.Start | No command execution in application code |
| **Deserialization** | ✅ No TypeNameHandling | All deserialization uses `System.Text.Json` with concrete types |
| **Path traversal** | ✅ No user-controlled paths | File I/O uses config-sourced paths only |
| **XXE** | ✅ No XML parsing | No XML parsing in application code |
| **ReDoS** | ✅ No user-controlled regex | All server-side regex uses static patterns |
| **Mass assignment** | ✅ Explicit DTOs | Admin API uses dedicated input types; `TenantId` is server-controlled |
| **Exception leakage** | ✅ Sanitized | 5xx errors stripped of detail in production |
| **redirect_uri validation** | ✅ Strict allow-list | `UrlComparison.IsAllowed()` with scheme + normalization + query/fragment |
| **post_logout_redirect_uri** | ✅ Opaque reference | 128-bit random ID, single-use, 5-minute expiry |
| **State/Nonce** | ✅ Correct | Passed through and echoed; external OIDC state is DataProtection-protected |
| **Token exchange** | ✅ Confined | Audience, scope intersection, delegation depth, single-hop, DPoP bridging |
| **Revocation** | ✅ Client-scoped | Client can only revoke own tokens |
| **Introspection** | ✅ Audience-scoped | Per-client audience allow-list, privacy-filtered response |
| **Key management** | ✅ Rotation | Automated 7-day rotation with overlap; JWKS strips private material |
| **Client secret generation** | ✅ Cryptographic | `RandomNumberGenerator.Fill()` with 48+ bytes |
| **Bootstrap token** | ✅ Safe-by-default | 404 if not configured, `FixedTimeEquals`, empty-DB-only |
| **No plaintext credential logging** | ✅ Verified | Seeder explicitly does not log passwords/secrets |

---

## Recommendations (Priority Order)

1. **Fix the open redirect in `LocalLogoutHandler`** — add `Url.IsLocalUrl()` or equivalent validation before `Results.Redirect()`. This is the only critical finding.
2. **Add explicit tenant checks** to `ProviderAndBclEndpoints.cs` client-provider and client-keys endpoints.
3. **Add explicit tenant checks** to `ApiService/Program.cs` read/update/delete endpoints for clients and users.
4. **Configure DataProtection certificate** in production to encrypt the key-ring at rest.
5. **Reject non-http(s) schemes** in `RegistrationHandler` redirect URI validation.
6. **Block `0.0.0.0` and `::`** in `NetworkSecurity.IsInternal`.
7. **Reject protocol-relative URLs** (`//`) in `SanitizeLocalReturn`.
8. **Guard `Testing:AllowLocalExternalOidcHttp`** with environment assertion.
9. **Remove `.svg` from logo upload** or serve with `Content-Disposition: attachment`.
10. **Make audience validation mandatory** in `TokenValidator` or log when skipped.
11. **Add atomic claim** to refresh token rotation.
12. **Add `certs/*.pfx` to `.gitignore`**.
13. **Move hardcoded dev secrets** to `.env` file.
14. **Register alert webhook and licensing client** with SSRF-safe handlers.