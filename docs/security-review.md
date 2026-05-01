# MrWhoOidc Security Review

**Date:** April 30, 2026  
**Status:** CR1 & CR2 resolved; CLI findings added  
**Review scope:** Full codebase (`MrWhoOidc.WebAuth`, `MrWhoOidc.Auth`, `MrWhoOidc.ApiService`, `MrWhoOidc.Security`, `MrWhoOidc.Cli`, examples, Docker, configuration)

---

## Executive Summary

MrWhoOidc is a .NET 10 OpenID Connect Provider and OAuth 2.0 Authorization Server. Overall, the codebase demonstrates strong security awareness. Many hardening patterns are already in place (DPoP, SSRF protection, CSP with nonces, host allow-listing, refresh token rotation, PKCE enforcement, Argon2id hashing). The findings below are organized by severity:

- **0 Critical** (both resolved) — see Resolution Notes
- **4 High** findings that should be addressed in the next development cycle
- **10 Medium** findings for steady hardening
- **5 Low** findings and informational notes

---

## Finding Summary Table

| # | Severity | Area | Title |
|---|----------|------|-------|
| CR1 | ~~Critical~~ Resolved | Password Hashing | PBKDF2 used instead of Argon2id for user password hashing |
| CR2 | ~~Critical~~ Resolved | Auto-seeding | Hardcoded admin credentials in auto-seed middleware |
| H1 | High | Impersonation | Session-based impersonation lacks re-authentication check |
| H2 | High | Admin API | ApiService dev fallback disables issuer validation |
| H3 | High | CSP Headers | unsafe-inline permitted for style-src; CDN scripts from external origins |
| H4 | High | Token Exchange | Subject token validation lacks token type enforcement for opaque tokens |
| M1 | Medium | Token Validator | Exception messages leaked in validation errors |
| M2 | Medium | Client Authentication | Basic auth with unencoded colon in secret parsing |
| M3 | Medium | Certificate Validation | DangerousAcceptAnyServerCertificateValidator used in dev paths |
| M4 | Medium | Configuration | Bootstrap token stored in plaintext in appsettings.json |
| M5 | Medium | Rate Limiting | Token exchange limiter client bucket is coarse |
| M6 | Medium | BCL Dispatcher | No SSRF validation on target URIs |
| M7 | Medium | Authorization Code | CodeHash uses Base64 (not Base64Url), code length is 43 chars |
| M8 | Medium | Device Code | User code not rate-limited for manual entry enumeration |
| M9 | Medium | CLI Config | Access/refresh tokens stored in plaintext JSON at `~/.mrwhooidc/config.json` |
| M10 | Medium | CLI Import | `--client-secret` accepts plaintext secret in shell-visible argument |
| L1 | Low | Logging | Correlation IDs use TraceIdentifier (not cryptographically random) |
| L2 | Low | Sessions | No absolute session expiry configured |
| L3 | Low | CSP | frame-ancestors 'none' conflicts with admin iframe embedding |
| L4 | Low | Docker | Non-root user not configured in Dockerfile |
| L5 | Low | CLI Config | Windows config file uses default NTFS ACLs (no restrictive permissions) |

---

## Critical Findings — RESOLVED

### CR1 ✅ — PBKDF2 used instead of Argon2id for user password hashing

**File:** `MrWhoOidc.Auth\Services\PasswordHasher.cs`  
**Severity:** Critical — **RESOLVED 2026-04-30**

**Resolution:**
- Added `Isopoh.Cryptography.Argon2` package to `MrWhoOidc.Auth.csproj`
- Replaced `Pbkdf2PasswordHasher` with `Argon2PasswordHasher` (Argon2id, 128 MB memory, 4 lanes)
- Hash format: `v2:{argons2_encoded_hash}` — the Argon2 library's self-contained format includes salt, parameters, and hash
- **Backward-compatible:** `Verify()` still supports legacy `v1:{iterations}:{salt}:{subkey}` PBKDF2 hashes for password migration
- DI registration updated in `DependencyInjection.cs` to `Argon2PasswordHasher`

---

### CR2 ✅ — Hardcoded admin credentials in auto-seed middleware and Seeder

**File:** `MrWhoOidc.WebAuth\Middleware\AutoSeedMiddleware.cs:47-49`  
**Severity:** Critical — **RESOLVED 2026-04-30**

**Resolution:**
- Removed `AdminEasyPassword = "Admin123!"` constant from `Seeder.cs`
- Admin password now generated via `RandomNumberGenerator.GetString()` (20 chars, cryptographically random)
- Added `SEED_ADMIN_PASSWORD` env var support — if set, uses the value instead of random generation; useful for Docker/CI deployments
- Logged via `ILogger.LogWarning` so the operator can retrieve it from container logs
- Changed auto-seed gating from `env.IsDevelopment() || flag` to `(env.IsDevelopment() || env.IsStaging()) && flag == "true"` — requires **both** environment AND explicit opt-in
- Updated `docker-compose.dev.yml` to include `Testing__EnableAutoSeed: "true"` and `SEED_ADMIN_PASSWORD` env var
- Updated `.env.example` with `SEED_ADMIN_PASSWORD` documentation
- Also fixed `TenantSeedingService.cs` fallback from `"Admin123!"` to random generation
- All **1,119 tests pass**

---

## High Findings

### H1 — Impersonation does not re-verify platform admin status on each request

**File:** `MrWhoOidc.WebAuth\Security\Admin\TenantAdminAuthorizationHandler.cs:76-93`  
**Severity:** High  
**CVSS:** 5.5 (AV:N/AC:L/PR:H/UI:N/S:U/C:H/I:H/A:N)

The `TenantAdminAuthorizationHandler` reads `ImpersonatingTenantId` directly from the session and grants authorization if it matches the current tenant, without re-verifying that the user is actually a platform admin.

At line 76-93:
```csharp
var impersonatedTenantIdStr = httpContext.Session.GetString("ImpersonatingTenantId");
if (!string.IsNullOrEmpty(impersonatedTenantIdStr) && Guid.TryParse(...))
{
    if (impersonatedTenantId == currentTenantId)
    {
        context.Succeed(requirement);
        return;
    }
}
```

If a platform admin's session cookie is stolen (or the admin forgets to log out on a shared machine), the attacker gains indefinite tenant admin access without needing to know platform admin credentials. There is no time limit on impersonation sessions.

**Recommendation:**
1. Set a maximum impersonation duration (e.g., 30 minutes) and store it alongside `ImpersonationStartTimeKey`.
2. Re-validate platform admin status on each request (not just when starting impersonation).
3. Consider requiring step-up re-authentication to start impersonation.

---

### H2 — ApiService dev fallback disables issuer validation entirely

**File:** `MrWhoOidc.ApiService\Program.cs:44-53`  
**Severity:** High  
**CVSS:** 6.5 (AV:N/AC:L/PR:N/UI:N/S:U/C:L/I:L/A:L)

When the `AdminAuth:Issuer` is not configured, the ApiService falls back to a dev path that disables issuer signing key validation entirely:

```csharp
else
{
    // Fallback (dev): minimal validation
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ...
    };
}
```

This means ANY valid JWT (from any issuer, even self-created) with non-expired lifetime would be accepted by the admin API. While this is behind a condition that checks for a missing issuer config, a misconfiguration in production could silently create an open auth bypass.

**Recommendation:** Remove the dev fallback entirely. If no issuer is configured, throw at startup. Use explicit dev-only configuration keys (e.g., `AdminAuth:AllowInsecureDevMode=false`) to gate relaxed validation, and always validate the signing key even in dev.

---

### H3 — Content Security Policy allows unsafe-inline for styles and loads scripts from external CDNs

**File:** `MrWhoOidc.WebAuth\Middleware\SecurityHeadersMiddleware.cs:47-52`  
**Severity:** High  
**CVSS:** 4.3 (AV:N/AC:L/PR:N/UI:R/S:U/C:N/I:L/A:N)

The CSP includes:
- `script-src 'self' 'nonce-{...}' https://unpkg.com https://cdnjs.cloudflare.com` — external scripts from CDNs
- `style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://unpkg.com https://fonts.googleapis.com` — unsafe-inline styles

The external CDN scripts (`unpkg.com`, `cdnjs.cloudflare.com`) are risky for an identity provider. A compromise of any of these CDNs, or a typo-squatted package substitution, could inject malicious JavaScript into the login/admin pages, enabling credential harvesting.

**Recommendation:**
1. Bundle all third-party JS/CSS assets locally instead of loading from CDNs.
2. Remove `unsafe-inline` for styles — use nonces or hashes for inline styles.
3. If CDNs must be used, add Subresource Integrity (SRI) hashes.

---

### H4 — Token Exchange: subject token type not validated for opaque tokens

**File:** `MrWhoOidc.WebAuth\TokenEndpoint\Grants\TokenExchangeGrantHandler.cs:77-78`  
**Severity:** High  
**CVSS:** 5.9 (AV:N/AC:H/PR:N/UI:N/S:U/C:H/I:L/A:N)

The handler accepts `subject_token` and `subject_token_type` but delegates validation to `context.TokenExchange.ExchangeTokenAsync(...)`. The handler itself only checks:
- `subject_token` is not empty (line 82-84)
- Attempts DPoP validation

There is no explicit validation that the `subject_token_type` actually matches the token format. An attacker could submit a JWT access token but claim `subject_token_type=urn:ietf:params:oauth:token-type:access_token`, attempting to bypass format-specific validation logic downstream.

**Recommendation:** In the handler, parse the subject token to verify it matches the declared token type before delegating. Reject if the token format contradicts the declared type.

---

## Medium Findings

### M1 — Exception messages leaked in token validation errors

**File:** `MrWhoOidc.Auth\Services\TokenValidator.cs:79-82`  
**Severity:** Medium

```csharp
catch (Exception ex)
{
    return (false, null, ex.Message);
}
```

The raw exception message from `JwtSecurityTokenHandler.ValidateToken` is returned as the error. This can leak internal implementation details (e.g., `"IDX10214: Audience validation failed. ..."`), aiding attackers in fingerprinting the validation logic.

**Recommendation:** Return a generic error message (e.g., `"invalid_token"`) and log the actual exception details server-side only.

---

### M2 — Host allow-list wildcard `*` bypasses all checks

**File:** `MrWhoOidc.WebAuth\Middleware\HostAllowListMiddleware.cs:90-94`  
**Severity:** Medium

The `IsAllowedHost` method treats `"*"` as a wildcard that bypasses all host checks. If accidentally configured (e.g., via a shell script that resolves an empty variable to `"*"`), all host validation is silently disabled.

**Recommendation:** Remove the `"*"` wildcard or require an explicit opt-in config key (`AllowAllHosts=true`) to prevent accidental enablement.

---

### M3 — `DangerousAcceptAnyServerCertificateValidator` in dev paths

**Files:**  
- `MrWhoOidc.WebAuth\Program.cs:147` — LicensingEntitlementsClient in dev  
- `MrWhoOidc.Cli\Services\CliServerConnection.cs:130` — CLI loopback connections  

**Severity:** Medium

Both usages are gated behind dev-only conditions (environment check / loopback check). However, the WebAuth path accepts any certificate for the licensing entitlements HTTP client, and the CLI path applies to localhost. If these conditions are ever bypassed in a build pipeline, MITM becomes possible.

**Recommendation:**
1. For WebAuth licensing client: use a named HTTP client with a configurable certificate thumbprint instead.
2. For CLI: use the user's system certificate store rather than accepting all certs; if a self-signed dev cert is needed, require the user to trust it explicitly.

---

### M4 — Bootstrap token stored in plaintext in appsettings.json

**File:** `MrWhoOidc.WebAuth\appsettings.json:11`  
**Severity:** Medium

```json
"Bootstrap": {
    "Token": ""
}
```

The empty default is safe, but if a value is ever populated, it sits in the source tree as plaintext. Unlike `.env` files, `appsettings.json` is commonly checked into version control.

**Recommendation:** Add a comment explicitly stating the token should come from environment variables or a secrets manager. The `appsettings.json` key should remain empty.

---

### M5 — Token Exchange rate limiter uses coarse client bucket

**File:** `MrWhoOidc.WebAuth\TokenEndpoint\Grants\TokenExchangeGrantHandler.cs:62`  
**Severity:** Medium

The rate limiter buckets by `Bucketization.Bucket(clientId)`. The `Bucket` method (from `Bucketization.cs`) appears to hash/truncate the client ID, meaning many different clients could share the same bucket. A malicious client could exhaust the rate limit for other tenants' clients in the same hash bucket.

**Recommendation:** Include tenant ID in the rate limit key: `$"te:{tenantId}:{clientId}"`.

---

### M6 — Backchannel Logout Dispatcher does not validate target URIs

**File:** `MrWhoOidc.WebAuth\Background\BackchannelLogoutDispatcher.cs:189`  
**Severity:** Medium

The BCL dispatcher POSTs `logout_token` to `n.TargetUri` without SSRF validation:

```csharp
resp = await http.PostAsync(n.TargetUri, content, ct);
```

If an attacker manages to insert a `BackchannelLogoutNotification` with a `TargetUri` pointing to an internal service (e.g., `http://localhost:1234/admin/delete-everything`), the dispatcher would POST the logout token to it. While the logout token itself is an opaque JWT, this is still a potential SSRF vector for probing internal services.

**Recommendation:** Use `NetworkSecurity.IsSafeUriAsync()` to validate each target URI before dispatching. The `NetworkSecurity` utility already exists in the codebase (`MrWhoOidc.Auth\Utils\NetworkSecurity.cs`).

---

### M7 — Authorization code hash uses Base64 instead of Base64Url

**File:** `MrWhoOidc.Auth\Services\AuthorizationCodeService.cs:41`  
**Severity:** Medium

```csharp
var codeHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
```

`Convert.ToBase64String` produces output with `+`, `/`, and `=` characters that are URL-unsafe. The code itself (line 35-37) uses Base64Url encoding. The hash is only stored in the database, not transmitted in URLs, so the impact is low, but this inconsistency may cause bugs if the hash is ever used in URL contexts.

The code is 43 characters (32 bytes in Base64Url), which is strong (256 bits of entropy).

**Recommendation:** Standardize on Base64Url encoding for all token/code hashes. Use the existing `CryptoHelper.ComputeSha256Base64Url()` method.

---

### M8 — Device code user code susceptible to manual enumeration

**File:** `MrWhoOidc.WebAuth\TokenEndpoint\Grants\DeviceCodeGrantHandler.cs`  
**Severity:** Medium

The device code flow does not appear to limit the number of failed verification attempts per user code. An attacker could brute-force the user_code at the verification endpoint, though the 8-character user code format provides limited entropy.

**Recommendation:** Add rate limiting on the verification endpoint (e.g., 5 failed attempts per IP per minute). Consider increasing user_code length or adding a delay between attempts.

---

### M9 — CLI stores access/refresh tokens in plaintext JSON config

**Files:**  
- `MrWhoOidc.Cli\Configuration\CliConfig.cs` — `ProfileConfig` model with `AccessToken` and `RefreshToken` string properties  
- `MrWhoOidc.Cli\Commands\LoginCommand.cs` — writes tokens to `~/.mrwhooidc/config.json` after login  
- `MrWhoOidc.Cli\Services\CliServerConnection.cs:206-225` — refreshes tokens and saves back to file  

**Severity:** Medium  

The CLI stores OAuth 2.0 access/refresh tokens as plaintext JSON strings in `~/.mrwhooidc/config.json`. On Unix, `SaveAsync()` sets `chmod 600` (owner-only read/write). On Windows, default NTFS ACLs apply — any process running as the same user can read the file with no additional restriction.

An attacker who gains read access to the user's home directory can extract long-lived refresh tokens and impersonate the user indefinitely. The refresh token is used to silently obtain new access tokens without re-authentication.

**Recommendation:**
1. Use `DPAPI` on Windows and `libsecret` / Keychain on macOS/Linux to encrypt tokens at rest via OS credential managers.
2. Alternatively, encrypt the config file using ASP.NET `DataProtection` with a machine-bound key.
3. As an immediate mitigation, store tokens in a dot-hidden file (`~/.mrwhooidc/.tokens`) with restricted ACLs on Windows via `File.SetAccessControl`.

---

### M10 — `import --client-secret` accepts plaintext secret on command line

**File:** `MrWhoOidc.Cli\Commands\ImportCommand.cs:77, 249`  
**Severity:** Medium  

The `--client-secret` option for `import preview` and `import apply` accepts a plaintext client secret as a command-line argument. This exposes the secret in:
- Shell history (`.bash_history`, `Get-History`, etc.)
- Process listings (`ps`, `tasklist`, `/proc/*/cmdline`)
- Terminal scrollback buffers

```csharp
var clientSecretOption = new Option<string?>("--client-secret") { Description = "Plaintext secret to supply for an obfuscated client or provider credential" };
```

The secret is then sent directly in the HTTP POST body to the server, never written to disk by the CLI, but the command-line exposure remains.

**Recommendation:**
1. Support reading the secret from an environment variable (`IMPORT_CLIENT_SECRET` or `MRWHO_CLIENT_SECRET`).
2. Support reading from stdin (`--client-secret @-`).
3. At minimum, add a warning in the help text: "WARNING: Providing secrets on the command line exposes them in shell history."

---

## Low Findings

### L1 — Correlation IDs use `TraceIdentifier` (not cryptographically random)

**File:** Multiple locations (e.g., `TokenExchangeGrantHandler.cs:149`)  
**Severity:** Low

```csharp
var corr = http.Request.Headers["x-correlation-id"].ToString();
if (string.IsNullOrWhiteSpace(corr)) corr = http.TraceIdentifier;
```

ASP.NET Core's `TraceIdentifier` uses `ConnectionId` format, which is predictable (base64 of a counter). This enables correlation ID guessing for log injection or request smuggling in log analysis tools.

**Recommendation:** Generate correlation IDs using `Guid.NewGuid().ToString("N")` or `RandomNumberGenerator.GetHexString(16)`.

---

### L2 — No absolute session expiry for cookie authentication

**File:** `MrWhoOidc.WebAuth\Infrastructure\ServiceRegistration\AuthenticationAuthorizationExtensions.cs:46-55`  
**Severity:** Low

Cookie auth uses `SlidingExpiration = true` but does not set `ExpireTimeSpan` (defaults to 14 days). With sliding expiration and no absolute expiry, a session can persist indefinitely if the user is constantly active.

**Recommendation:** Set `ExpireTimeSpan` to a reasonable value (e.g., 12 hours) and consider adding absolute expiry via a custom cookie auth event that checks `auth_time`.

---

### L3 — CSP `frame-ancestors 'none'` may conflict with check_session_iframe

**File:** `MrWhoOidc.WebAuth\Middleware\SecurityHeadersMiddleware.cs:48`  
**Severity:** Low

The CSP includes `frame-ancestors 'none'` which blocks all framing. The code correctly exempts `/connect/checksession` from `X-Frame-Options: DENY` (line 32), but the CSP `frame-ancestors` value is applied unconditionally to non-check-session frames. If the check_session_iframe response also gets text/html content-type, it would receive both frame-deny headers and a permissive CSP, which browsers resolve to the most restrictive policy.

**Recommendation:** Apply `frame-ancestors` conditionally like `X-Frame-Options`. For the check_session_iframe path, use `frame-ancestors 'self'` or the specific allowed origins.

---

### L4 — Dockerfile does not configure non-root user

**File:** `Dockerfile` (production build)  
**Severity:** Low

The multi-stage Dockerfile targets `mcr.microsoft.com/dotnet/aspnet:10.0-noble`. The .NET 10 ASP.NET runtime images default to the `app` user, but this should be verified and made explicit with `USER app`.

**Recommendation:** Add explicit `USER app` directive in the Dockerfile and verify port bindings (the `app` user cannot bind to ports < 1024).

---

### L5 — CLI config file uses default NTFS ACLs on Windows (no restrictive permissions)

**File:** `MrWhoOidc.Cli\Configuration\CliConfig.cs:SaveAsync()`  
**Severity:** Low  

The `SaveAsync()` method sets `UnixFileMode.UserRead | UnixFileMode.UserWrite` (600) on non-Windows systems, but on Windows, no ACL restrictions are applied. The config file inherits default NTFS permissions from the parent directory, typically granting read access to other users in the same system.

**Recommendation:** On Windows, apply `File.SetAccessControl` to restrict the config file to the current user only. Alternatively, use `File.Encrypt()` (EFS) for transparent encryption.

---

## Resolution Notes

### CR1 & CR2 — Resolved 2026-04-30

Both critical findings have been resolved. See the updated Critical Findings section above for full details. Changes were verified with **1,119 passing unit tests**.

Key changes:
- Password hashing migrated from PBKDF2 to Argon2id (128 MB memory, 4 lanes)
- Backward-compatible verification of legacy PBKDF2 hashes
- Auto-seed now requires both development/staging environment AND explicit `Testing:EnableAutoSeed=true`
- Admin passwords are randomly generated; configurable via `SEED_ADMIN_PASSWORD` env var
- `Admin123!` hardcoded password removed from all code paths

### Operational Impact of SEED_ADMIN_PASSWORD

For Docker deployments where auto-seed is enabled (dev/staging):

```bash
# Set a known admin password
export SEED_ADMIN_PASSWORD=MySecurePass123!

# Or in docker-compose.override.yml:
services:
  webauth:
    environment:
      SEED_ADMIN_PASSWORD: MySecurePass123!
```

If `SEED_ADMIN_PASSWORD` is not set, a random 20-character password is generated and logged at Warning level. Retrieve it via:
```bash
docker compose logs webauth | grep "Auto-seeded admin password"
```

## Positive Security Observations

The following security measures are well-implemented:

1. **DPoP (RFC 9449):** Full implementation with JWK thumbprint binding, ath claim validation, nonce management, replay detection — a strong proof-of-possession mechanism.

2. **SSRF Protection (`NetworkSecurity`):** Custom `SocketsHttpHandler` with `ConnectCallback` that filters private/internal IPs at the socket level, plus URI validation utilities. Well-implemented defense-in-depth.

3. **PKCE (S256) Enforcement:** Authorization code flow requires `code_challenge`/`code_verifier` validation via SHA-256.

4. **Refresh Token Rotation:** One-time use refresh tokens with reuse detection — if a stolen refresh token is reused, all tokens for that grant are revoked.

5. **Tenant Isolation in TenantResolutionMiddleware:** Cross-tenant access is checked per-request; users are denied access to tenants they don't belong to (line 147-202).

6. **Host Allow-List Middleware:** Enforces allowed hosts, supports wildcard subdomains, and fail-closed configuration (blocks all traffic if no hosts configured).

7. **Security Headers:** `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, `X-Frame-Options: DENY`, CSP with nonce-based script-src.

8. **Cookie Security:** `HttpOnly = true`, `SecurePolicy = Always`, `SameSite = Lax` — all three protections enabled.

9. **No raw SQL:** All database access is through Entity Framework Core with LINQ — no raw SQL injection surface.

10. **Argon2id for client secrets:** The ApiService uses Argon2id for hashing client secrets (`Isopoh.Cryptography.Argon2`).

11. **Device Code Atomic Redemption:** Uses `ExecuteDeleteAsync` with status check to prevent concurrent token issuance (line 140-147).

12. **Authorization Code Hash Storage:** Stores SHA-256 hash of the code, not the code itself, in the database.

13. **Nonce exclusion from redirect URIs:** Per OIDC spec, the nonce is correctly excluded from the authorization response query string (line 83).

14. **Rate Limiting:** Configurable rate limiting policies with Redis-backed distributed enforcement.

15. **Data Protection at Rest:** Data Protection keys are persisted to PostgreSQL for consistency across restarts (critical for antiforgery tokens).

---

## Risk Matrix

| Risk | Likelihood | Impact | Risk Level |
|------|-----------|--------|------------|
| Production exposure of autoseed | Low (gated by env) | Critical | High |
| Password hash cracking (PBKDF2 vs Argon2) | Low (requires DB breach) | High | Medium |
| Impersonation session hijack | Medium | High | High |
| Issuer-less dev fallback in production | Low | Critical | Medium |
| CDN script compromise | Low | High | Medium |
| SSRF via BCL target URI | Low | Medium | Low |
| Token exchange bypass | Low | High | Medium |

---

## Remediation Priority

1. **Immediately (Critical):**
   - CR2: Remove hardcoded credentials / strengthen autoseed gating
   - CR1: Migrate password hashing to Argon2id

2. **Next sprint (High):**
   - H2: Remove ApiService insecure dev fallback
   - H4: Add subject token type validation in Token Exchange
   - H1: Add impersonation session limits and re-validation

3. **This quarter (Medium):**
   - H3: Bundle CDN assets locally and tighten CSP
   - M1–M8: Address medium findings systematically

4. **Ongoing (Low):**
   - L1–L4: Address as part of regular maintenance
