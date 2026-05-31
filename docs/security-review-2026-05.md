# MrWhoOidc Security Review — May 2026

**Scope:** Source-level security review of the MrWhoOidc OIDC/OAuth 2.0 Authorization Server
(`MrWhoOidc.Auth`, `MrWhoOidc.WebAuth`, `MrWhoOidc.ApiService`, `MrWhoOidc.Security`,
`MrWhoOidc.KeyGen`, configuration & container files).

**Method:** Read-only static review of cryptography, key management, OAuth/OIDC flows,
secrets handling, persistence/injection, HTTP security (cookies, headers, CSRF, CORS),
and multi-tenant isolation/authorization. Key findings were verified directly against the code.

**Overall assessment:** The cryptographic core and protocol implementation are **strong** —
Argon2id password hashing, `RandomNumberGenerator` everywhere, constant-time comparisons,
S256-only PKCE, refresh-token rotation with reuse detection, authorization-code single-use,
exact `redirect_uri` matching, a dedicated SSRF-safe HTTP client (`NetworkSecurity.CreateSafeHttpClient`),
and key rotation with overlap. The **primary risks are operational and architectural**:
multi-tenant isolation in the management API/Razor admin pages, plaintext secrets in logs,
several external HTTP call sites that bypass the SSRF-safe client, and HTTP hardening gaps.

> **Note on `secrets/`:** `secrets/licensing-private-key.pem` and `secrets/bootstrap__token.txt`
> exist locally but are **git-ignored and NOT tracked** (verified with `git check-ignore` /
> `git ls-files`). They are not committed. However, the following key material **is tracked**
> in git and should be reviewed: `certs/aspnetapp.pfx`, `e2e/fixtures/licensing-test-private-key.pem`,
> `e2e/fixtures/licensing-test-public-key.pem`.

---

## Severity summary

| Severity | Count | Theme |
|----------|-------|-------|
| Critical | 4 | Multi-tenant IDOR in management API & admin pages; missing tenant scoping |
| High | 8 | SSRF bypass of safe client; secrets logged in plaintext; cookie/CSP/HSTS hardening; key-at-rest |
| Medium | 9 | User enumeration, distributed rate limiting, TOTP at rest, optional security controls, headers |
| Low | 8 | Cookie prefixes, clock-skew consistency, redirect-follow validation, misc hardening |

Counts are after de-duplication and correction of the inaccurate "committed private key" claim.

---

## CRITICAL findings

### C-1. Management API endpoints are not tenant-scoped (cross-tenant IDOR/BOLA)
**Files:** `MrWhoOidc.ApiService/Program.cs` — `/admin/clients` (~L210-260),
`/admin/users` (~L350-410), `/admin/scopes` (~L114-150), `/admin/users/{userId}/roles` (~L545-570),
`/admin/users/{userId}/emails` (~L418-455).

**Verified:** `Program.cs` contains **zero** `TenantId` references; `/admin/clients` GET uses
`db.Clients.AsNoTracking()` with no tenant filter; create/update/delete operate by global `Id`/`ClientId`.
The `admin` policy only checks `realm` + `roles` claims and never derives or enforces a tenant.

**Impact:** Any holder of an admin token can list/create/modify/delete clients, users, scopes,
role assignments, and alternate emails across **all tenants**. Role assignment does not verify the
role belongs to the target user's tenant → cross-tenant privilege escalation.

**Fix:**
- Decide and document the intended trust model. If this API is **platform-admin only**, enforce a
  dedicated `platform-admin` role and clearly segregate it from tenant admins; otherwise add tenant scoping.
- Extract `tenant_id` from the validated JWT (or route) and apply `.Where(x => x.TenantId == tenantId)`
  to every query and mutation.
- For role assignment, validate `role.TenantId == targetUser.TenantId` before persisting.
- Add cross-tenant IDOR integration tests (tenant A token attempting tenant B resources → 403/404).

### C-2. No EF Core global query filters for tenant isolation
**File:** `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (`OnModelCreating`). **Verified:** no
`HasQueryFilter` anywhere in `MrWhoOidc.Auth/Persistence`.

**Impact:** Tenant isolation depends entirely on every query manually adding a `TenantId` predicate.
A single missing filter (as in C-1) is a cross-tenant leak. This is an architectural weakness that
makes IDOR the default failure mode.

**Fix:**
- Add `modelBuilder.Entity<T>().HasQueryFilter(e => e.TenantId == _currentTenantId)` for all
  tenant-scoped entities, sourcing `_currentTenantId` from an injected tenant accessor.
- Use `IgnoreQueryFilters()` only in explicit, audited platform-admin paths.
- Add an architecture test asserting every entity implementing the tenant marker interface has a filter.

### C-3. Admin Razor pages load resources by id without tenant ownership check
**File:** `MrWhoOidc.WebAuth/Pages/Admin/ClientKeys/Index.cshtml.cs` (`OnGetAsync`/`OnPostFetchAsync`/`OnPostSaveAsync`, ~L34-52).
Page is gated by the `tenant-admin` policy but loads `db.Clients...FirstOrDefaultAsync(c => c.Id == clientId)`
with no tenant predicate.

**Impact:** A tenant admin for tenant A can fetch/modify another tenant's client JWKS (signing/validation
key material) by changing the `clientId` query parameter.

**Fix:** Scope every id-bound admin page query by the current tenant:
`FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId)` and return `NotFound()` otherwise.
Audit **all** admin Razor pages that bind an `id`/`clientId`/`userId`/`keyId` for the same pattern.

### C-4. SSRF in back-channel logout & external-OIDC calls bypasses the safe HTTP client
**Files:**
- `MrWhoOidc.WebAuth/Background/BackchannelLogoutDispatcher.cs` (~L88 create, ~L197 POST) — posts to
  client-registered `backchannel_logout_uri` using `_httpFactory.CreateClient()` with no validation.
- `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcDiscoveryService.cs` (~L62/66) — fetches discovery
  metadata from a user/tenant-supplied `authority`.

**Impact:** A malicious/compromised client or IdP registration can point these URLs at internal addresses
(`http://169.254.169.254/...`, `http://localhost`, RFC-1918) to reach cloud metadata or internal services,
exfiltrate logout/JWKS traffic, or perform DNS rebinding. The repo already ships
`MrWhoOidc.Auth.Utils.NetworkSecurity.CreateSafeHttpClient()` (with DNS-rebinding & private-range
protection) — but these call sites do not use it.

**Fix:** Route every outbound, URL-from-data HTTP call through `NetworkSecurity.CreateSafeHttpClient()`.
See H-1 for the full list of call sites.

---

## HIGH findings

### H-1. Additional external HTTP call sites bypass the SSRF-safe client
**Files:**
- `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcTokenValidator.cs` (~L198, JWKS fetch)
- `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcTokenExchangeService.cs` (~L96, ~L171, token endpoint)
- `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcRequestBuilder.cs` (~L264, PAR endpoint)

**Fix:** Switch all to `NetworkSecurity.CreateSafeHttpClient(...)`. Additionally validate that
`jwks_uri`/`token_endpoint`/`par_endpoint` share the discovery `authority`'s host, and re-validate the
target of every redirect (the safe client allows up to 10 redirects — see L-3).

### H-2. Seeded admin password written to logs in plaintext
**File:** `MrWhoOidc.Auth/Services/Seeder.cs` ~L186 (**verified**):
`logger.LogWarning("Auto-seeded admin password: {Password} (change on first login)", password)`.

**Impact:** The bootstrap admin password lands in stdout/log aggregation/Application Insights in clear text.

**Fix:** Never log the password. Log only that an admin was seeded and that `SEED_ADMIN_PASSWORD` should
be set; force a password change on first login. Scrub any environments where this already ran.

### H-3. Generated client secrets written to logs in plaintext
**File:** `MrWhoOidc.Auth/Services/Seeder.cs` ~L200-238 / ~L550-563 — auto-generated client secrets
(blazor-web, m2m, test-api) logged as warnings.

**Fix:** Log only the client id and the env var to set; return the secret once at creation time (already
done) and never log the value. Rotate any secrets that were previously emitted to logs.

### H-4. Signing private keys stored unencrypted at rest in the database
**Files:** `MrWhoOidc.Auth/Services/KeyStore.cs` (~L53-67), `KeyRotationService.cs` (~L83-96) — `SigningKeys.JwkJson`
holds full private parameters (`d,p,q,dp,dq,qi`) as plaintext JSON.

**Fix:** Encrypt `JwkJson` at rest (envelope encryption with a KEK from Key Vault/KMS, or ASP.NET Core
Data Protection / `pgcrypto`). Prefer a managed key store / HSM where feasible. Restrict and audit DB/backups.

### H-5. OP browser-state cookie is script-readable cross-site
**File:** `MrWhoOidc.WebAuth/Services/AuthorizeResponseGenerator.cs` (~L188-200) — `mrwho.opbs` set with
`HttpOnly = false`, `SameSite = None`, `Secure = http.Request.IsHttps`.

**Impact:** Session-management state is readable by any cross-origin script context; the `Secure` flag is
conditional, so it can be sent over plain HTTP during downgrade/MITM windows.

**Fix:** Keep the value strictly the OPBS salt (no sensitive data — confirm), set `Secure = true`
unconditionally, and rely on the `check_session_iframe` `postMessage` origin validation. Consider
`SameSite=None` only with strong origin checks; document the threat model.

### H-6. CSP allows `unsafe-inline` styles and CDN assets without SRI
**File:** `MrWhoOidc.WebAuth/Middleware/SecurityHeadersMiddleware.cs` (~L48-51) — `style-src 'self'
'unsafe-inline' https://cdn.jsdelivr.net https://unpkg.com https://fonts.googleapis.com ...`.

**Fix:** Replace `'unsafe-inline'` with per-request nonces (or hashes) and add Subresource Integrity
(or self-host) for CDN CSS/fonts. Confirm `script-src` does not also permit `unsafe-inline`/`unsafe-eval`.

### H-7. `check_session_iframe` exempt from frame protection without `frame-ancestors`
**File:** `MrWhoOidc.WebAuth/Middleware/SecurityHeadersMiddleware.cs` (~L30-40) — the checksession
endpoint is excluded from `X-Frame-Options: DENY` but no restrictive `frame-ancestors` is applied.

**Fix:** For that endpoint emit `Content-Security-Policy: frame-ancestors https:` (or an RP allowlist)
rather than leaving framing unrestricted; keep `DENY` everywhere else.

### H-8. HSTS uses short max-age, no `includeSubDomains`/`preload`
**File:** `MrWhoOidc.WebAuth/Infrastructure/Pipeline/PipelineExtensions.cs` (~L52) — `app.UseHsts()` default
(30 days, no preload).

**Fix:** Configure `MaxAge = 365 days`, `IncludeSubDomains`, and `Preload` for a production IdP, after
verifying all subdomains are HTTPS-only.

---

## MEDIUM findings

### M-1. Login error messages enable user enumeration
**Files:** `MrWhoOidc.WebAuth/Pages/Login.cshtml.cs` (POST handler);
`MrWhoOidc.Auth/Services/GlobalAuthenticationService.cs` distinguishes `UserNotFound` vs `InvalidPassword`.

**Fix:** Return a single generic "Invalid credentials" message to the client for both cases; keep the
detailed reason only in server-side audit logs. Equalize response timing (the Argon2 verify cost already
helps; consider a dummy verify on the not-found path).

### M-2. Login rate limiting is per-process (not distributed)
**File:** `MrWhoOidc.WebAuth/Pages/Login.cshtml.cs` (~L31-40) — in-memory `ConcurrentDictionary`
IP+username sliding window.

**Fix:** Back rate limiting with `IDistributedCache`/Redis (keys like `rl:login:{ip}:{user}`) or the
ASP.NET Core rate limiter with a distributed store so multi-instance deployments are protected. The
global account lockout (5/15 min) remains a good complementary control.

### M-3. TOTP secrets stored unencrypted at rest
**File:** `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (UserAccount TOTP secret column).

**Fix:** Encrypt TOTP secrets at rest (Data Protection / column encryption / AES-256-GCM with a KEK).
Require re-enrollment on password change; audit verify attempts.

### M-4. Optional security controls can be disabled by configuration
**Files:** `MrWhoOidc.Auth/Services/RequestObjectValidator.cs` (~L105-130) — request-object lifetime check
only runs when `RequestObjectMaxLifetimeSeconds > 0`; `MrWhoOidc.Security/DPoP.cs` (~L38-47) — DPoP iat
leeway configurable up to 300s.

**Fix:** Enforce a safe non-zero default lifetime that cannot be silently disabled; cap DPoP iat leeway at
≤120s and warn if a larger value is configured.

### M-5. Missing `Permissions-Policy` header
**File:** `MrWhoOidc.WebAuth/Middleware/SecurityHeadersMiddleware.cs`.

**Fix:** Add e.g. `Permissions-Policy: geolocation=(), microphone=(), camera=(), payment=(), usb=()`.

### M-6. Session state not explicitly cleared/regenerated on login
**Files:** `MrWhoOidc.WebAuth/Pages/Login.cshtml.cs` (~L430), `LoginTotp.cshtml.cs` (~L98).

**Fix:** Call `HttpContext.Session.Clear()` before `SignInAsync` to drop pre-auth state and reduce session
fixation surface (the cookie auth ticket is reissued by the framework, but app session state may persist).

### M-7. Argon2 verify path relies on library timing guarantees
**File:** `MrWhoOidc.Auth/Services/PasswordHasher.cs` (~L66 Argon2 verify; PBKDF2 fallback uses
`FixedTimeEquals` at ~L95).

**Fix:** Confirm `Isopoh.Cryptography.Argon2.Verify` is constant-time; if unverified, wrap comparison with
`CryptographicOperations.FixedTimeEquals` on the derived hash.

### M-8. Client-credentials / token-exchange & multi-secret verification timing
**File:** `MrWhoOidc.Auth/Services/ClientStore.cs` (~L190-215) — loop over active secrets returns on first
match (acceptable, but leaks marginal timing). Token-exchange/CC audience+scope restriction verified correct.

**Fix:** Optional — evaluate all candidate secrets to mask which one matched, or accept the residual risk
(low). No privilege-escalation issues found in token exchange/client-credentials.

### M-9. Algorithm fallback to RS256 when key alg unspecified
**File:** `MrWhoOidc.Auth/Services/JwtService.cs` (~L136-145) — silent default to RS256.

**Fix:** Require an explicit `alg` on every signing key at creation; log a warning (or throw) on fallback
to avoid masking EC/RSA misconfiguration.

---

## LOW findings

| ID | File:Area | Issue | Fix |
|----|-----------|-------|-----|
| L-1 | Cookies: `AuthenticationAuthorizationExtensions.cs` (~L48), `LocalizationAndMvcExtensions.cs` (~L35-38, ~L123) | Auth/session/antiforgery cookies lack `__Host-`/`__Secure-` prefixes | Add `__Host-` (path `/`, no domain, Secure) where possible |
| L-2 | `MrWhoOidc.Security/DPoP.cs`; `ClientAssertionValidator.cs` (~L100); `CibaAuthenticationHandler.cs` (~L433); `TokenValidator.cs` (~L49) | Clock-skew values inconsistent (60/120s, some hardcoded) | Centralize clock skew in one options object |
| L-3 | `Pages/Admin/Clients/Edit.cshtml.cs` (~L708-728) | Safe client used, but follows up to 10 redirects to JWKS URI | Disable auto-redirect or re-validate each redirect target IP |
| L-4 | `certs/aspnetapp.pfx`, `e2e/fixtures/*.pem` (tracked) | Dev/test key material committed | Confirm dev-only & non-reused; document; keep prod keys out of repo |
| L-5 | `.env` / `certs/README.md` | Default TLS cert password `changeit` | Dev-only; add pre-deploy validation rejecting weak/`changeit`/short values |
| L-6 | `.env.example` (~L17) | `POSTGRES_PASSWORD=changeme_...` placeholder | Pre-deploy guard fails on `changeme`/short passwords |
| L-7 | `SecurityHeadersMiddleware.cs` | No legacy `X-XSS-Protection` (CSP preferred) | Optional `1; mode=block` for old browsers |
| L-8 | `Pages/Auth/Qr.cshtml.cs` (~L126) | PKCE verifier in `sessionStorage` (XSS exposure) | Mitigated by S256 + short-lived single-use codes + CSP; tighten CSP per H-6 |

---

## Verified-good practices (keep)

- **Password hashing:** Argon2id (t=4, m=128 MiB, p=4, 16-byte salt) with PBKDF2 legacy verify via `FixedTimeEquals`.
- **Randomness:** `RandomNumberGenerator.GetBytes` throughout (no `System.Random` for security material).
- **PKCE:** S256 only; `plain` rejected; constant-time verifier comparison.
- **Authorization codes:** SHA-256 hashed at rest, single-use, bound to client_id + redirect_uri, ~5-min expiry.
- **Refresh tokens:** rotation + reuse detection with family revocation (`ReplacedById` traversal).
- **redirect_uri:** exact match incl. query/fragment; only http/https.
- **Token validation:** alg allowlist (no `alg=none`), issuer/audience/lifetime enforced, revocation checks.
- **SSRF-safe client exists:** `NetworkSecurity.CreateSafeHttpClient` (DNS rebinding + private-range guard);
  `SectorIdentifierResolver` already uses it (the gap is the call sites in C-4/H-1).
- **Secrets model:** client secrets hashed (Argon2id), returned only at creation, rotation with expiry, expiry monitor.
- **Response types:** only `code`; implicit/hybrid rejected.
- **Injection:** EF Core parameterized queries; no `FromSqlRaw`/string-concat SQL, no `BinaryFormatter`/`TypeNameHandling`, no unsafe XML.
- **Antiforgery + LocalRedirect** used on interactive flows; logout HTML output is attribute-encoded.

---

## Prioritized remediation plan

**Now (Critical):**
1. C-1/C-3 — add tenant scoping to every management API endpoint and id-bound admin page; add IDOR tests.
2. C-2 — introduce EF Core global query filters + architecture test.
3. C-4/H-1 — route all data-driven outbound HTTP through `CreateSafeHttpClient`; validate redirect targets.

**This week (High):**
4. H-2/H-3 — stop logging admin password and client secrets; rotate anything already emitted.
5. H-4 — encrypt signing keys (and M-3 TOTP secrets) at rest.
6. H-5/H-6/H-7/H-8 — cookie/CSP/frame-ancestors/HSTS hardening.

**Next (Medium/Low):**
7. M-1/M-2 — generic login errors + distributed rate limiting.
8. M-4..M-9 and L-1..L-8 — enforce non-disableable controls, headers, cookie prefixes, clock-skew unification, weak-default guards.

---

*Findings reference approximate line numbers; verify exact locations before editing. Items marked
"verified" were confirmed directly against the source during this review.*
