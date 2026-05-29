# MrWhoOidc Code Review — Quality & Security

- **Date:** 2026-05-28
- **Scope:** Full `MrWhoOidc` solution (`MrWhoOidc.Auth`, `MrWhoOidc.ApiService`, `MrWhoOidc.WebAuth`, `MrWhoOidc.Security`, `MrWhoOidc.KeyGen`, `MrWhoOidc.Cli`, supporting projects)
- **Focus:** Code quality and security
- **Method:** Static review of source. Each finding below was verified against the actual source; theoretical-only items from the initial sweep were dropped or downgraded.

## Summary

The codebase is mature and well-structured: consistent `ConfigureAwait(false)`, broad `CancellationToken` propagation, `AsNoTracking` on read paths, `HybridCache` usage, clean DI registration, and good test coverage. No blocking `.Result`/`.Wait()` calls and no raw `new HttpClient()` were found.

The most important issue is hardcoded client secrets that are seeded into **any** freshly bootstrapped deployment, not just dev/test. Tenant isolation is enforced manually per query (no EF global query filters), which makes missing tenant predicates a recurring risk.

| Severity | Count |
|----------|-------|
| Critical | 1 |
| High | 3 |
| Medium | 5 |
| Low | 4 |

---

## Security Findings

### SEC-1 (Critical) — Hardcoded client secrets seeded on production bootstrap

- **Files:** [MrWhoOidc.Auth/Services/Seeder.cs](../MrWhoOidc.Auth/Services/Seeder.cs#L21) (constants L21, L25, L29; used at L197, L234, L362, L371, L395, L404)
- **Issue:** `InitialBlazorWebClientSecret`, `M2MClientSecret` (`"m2m-test-secret"`), and `TestApiClientSecret` (`"T3stApiSecret!"`) are compiled-in constants. `Seeder.SeedAsync` is invoked by the **public** `/bootstrap` endpoint ([BootstrapEndpointMappingExtensions.cs L131](../MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/BootstrapEndpointMappingExtensions.cs#L131)) — the path the README recommends for production setup — not only by the dev-gated `AutoSeedMiddleware`. Any deployment bootstrapped this way ships with confidential clients whose secrets are public in source/image. An attacker can obtain tokens via the client-credentials/M2M flow.
- **Fix:**
  - Do not seed confidential clients with built-in secrets on the production bootstrap path. Generate a random secret per deployment (`RandomNumberGenerator.GetBytes`) and return/log it once, or require the operator to supply it.
  - Restrict the `m2m-test-client` and `test-api` demo clients to the dev/test seed path only (`AutoSeedMiddleware`, which is gated by environment + `Testing:EnableAutoSeed`).
  - Treat the existing constants as compromised and rotate anywhere they may already be deployed.

### SEC-2 (High) — Missing tenant predicate on client-delete in-use check

- **File:** [MrWhoOidc.ApiService/Program.cs](../MrWhoOidc.ApiService/Program.cs#L313) (L313-L317)
- **Issue:** The "client in use" check queries `AuthorizationCodes`, `Consents`, `Tokens`, etc. by `ClientId` with no `TenantId` filter. There are **no EF global query filters** in `MrWhoOidc.Auth/Persistence` (confirmed — no `HasQueryFilter` anywhere), so tenant isolation depends entirely on each query including the predicate. This admin endpoint leaks cross-tenant existence and is representative of a broader IDOR risk class.
- **Fix:** Add `&& x.TenantId == tenantAccessor.CurrentTenant.TenantId` to each clause. Strategically, add EF Core global query filters keyed on the ambient tenant so isolation is enforced by default rather than per query.

### SEC-3 (High) — DPoP `iat` acceptance window too wide

- **File:** [MrWhoOidc.Security/DPoP.cs](../MrWhoOidc.Security/DPoP.cs#L115) (~L115-L121)
- **Issue:** DPoP proof `iat` is accepted within ±5 minutes. RFC 9449 expects a tight window; 10 minutes of tolerance widens the replay opportunity, especially if the replay/jti cache TTL does not fully cover it.
- **Fix:** Reduce to ~60 seconds, make it configurable via `AuthOptions`, and ensure the `jti` replay cache TTL is at least as long as the acceptance window.

### SEC-4 (High) — `PasswordHasher` swallows all exceptions during verification

- **File:** [MrWhoOidc.Auth/Services/PasswordHasher.cs](../MrWhoOidc.Auth/Services/PasswordHasher.cs#L62) (`VerifyV2` ~L62, `VerifyV1` ~L95)
- **Issue:** Both verify paths use a bare `catch { return false; }`. Any failure (malformed hash, but also `OutOfMemoryException`, etc.) silently becomes "password invalid", masking misconfiguration/operational faults and hindering incident detection. This is both a security-observability and a code-quality issue.
- **Fix:** Catch only expected exceptions (`FormatException` for Base64; the specific exception Argon2 raises for malformed input) and let unexpected exceptions propagate to be logged.

### SEC-5 (Medium) — Tenant slug not syntactically validated

- **File:** [MrWhoOidc.Auth/MultiTenancy/TenantResolver.cs](../MrWhoOidc.Auth/MultiTenancy/TenantResolver.cs) and bootstrap slug handling in [BootstrapEndpointMappingExtensions.cs L88](../MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/BootstrapEndpointMappingExtensions.cs#L88)
- **Issue:** Slugs taken from `/t/{slug}` and from the bootstrap request body are trimmed but not validated against an allowlist pattern. Unexpected characters can flow into issuer URIs and lookups.
- **Fix:** Validate against `^[a-z0-9-]{1,63}$` at resolution and at tenant creation; reject otherwise.

### SEC-6 (Medium) — Redirect URI comparison lacks scheme/port hardening

- **File:** [MrWhoOidc.Auth/Utils/UrlComparison.cs](../MrWhoOidc.Auth/Utils/UrlComparison.cs)
- **Issue:** Normalization does not explicitly restrict to `http`/`https`, validate port ranges, or canonicalize percent-encoding before comparison — increasing the surface for open-redirect bypass variants.
- **Fix:** Enforce an `http`/`https` scheme allowlist, validate port (1–65535), and decode/normalize percent-encoding before equality checks.

### SEC-7 (Medium) — DPoP nonce cache key embeds raw client IP

- **File:** [MrWhoOidc.Security/DPoP.cs](../MrWhoOidc.Security/DPoP.cs#L260) (~L260)
- **Issue:** Nonce cache key is `dpop:nonce:{endpoint}:{clientIp}:{jkt}`. If backed by Redis or surfaced in diagnostics, raw client IPs are exposed; if `clientIp` derives from an untrusted `X-Forwarded-For`, the nonce store can be polluted.
- **Fix:** Hash the IP component; ensure the IP is sourced only from a validated, trusted-proxy forwarded-headers chain (see [ForwardedHeadersConfigurator.cs](../MrWhoOidc.WebAuth/Infrastructure/Pipeline/ForwardedHeadersConfigurator.cs)).

### SEC-8 (Medium) — Refresh token lifetime settings not validated

- **File:** [MrWhoOidc.Auth/Services/RefreshTokenService.cs](../MrWhoOidc.Auth/Services/RefreshTokenService.cs) (~L40-L60)
- **Issue:** Magic numbers (`1296000`/`2592000`) and no guard against non-positive lifetime values, so a zero/negative misconfiguration could produce already-expired or invalid tokens.
- **Fix (applied):** Introduced named defaults (`DefaultRefreshTokenLifetimeSeconds`, `DefaultRefreshTokenAbsoluteLifetimeSeconds`) and fall back to them when a configured lifetime is `<= 0`. An absolute lifetime shorter than the sliding lifetime is intentional and preserved — the effective expiry is `min(sliding, absolute)`, so the absolute window caps the sliding one.

### SEC-9 (Low) — KeyGen admin UI CSP allows `unsafe-inline`

- **File:** [MrWhoOidc.KeyGen/Program.cs](../MrWhoOidc.KeyGen/Program.cs) (~L56-L75)
- **Issue:** `script-src`/`style-src` include `'unsafe-inline'`, weakening XSS defenses. Admin-only, hence Low.
- **Fix:** Use nonces/hashes and move inline assets to external files.

> Note: The earlier sweep flagged the ApiService dev JWT fallback and a bootstrap timing attack as Critical. On verification both are mitigated: the JWT fallback throws outside Development ([Program.cs L25, L44](../MrWhoOidc.ApiService/Program.cs#L44)), and bootstrap uses constant-time `CryptographicOperations.FixedTimeEquals` with `NotFound` when no token is configured. The remaining timing nuance (missing-header vs wrong-token) is minor and not separately tracked.

---

## Code Quality Findings

### CQ-1 (High) — Bare catch in `PasswordHasher`

See **SEC-4** — same root cause; tracked under security severity.

### CQ-2 (Medium) — Fire-and-forget `Task.Run` for secret-usage recording

- **File:** [MrWhoOidc.Auth/Services/ClientStore.cs](../MrWhoOidc.Auth/Services/ClientStore.cs#L148) (~L148-L169)
- **Issue:** `_ = Task.Run(async () => { ... })` is untracked; work can be lost on shutdown and has no parent cancellation linkage. It does log internally, but is hard to test/observe.
- **Fix:** Move to a hosted background queue (`IHostedService` / channel-based `IBackgroundTaskQueue`).

### CQ-3 (Medium) — Duplicated Argon2 configuration in admin endpoints

- **File:** [MrWhoOidc.ApiService/Program.cs](../MrWhoOidc.ApiService/Program.cs) (client create/update handlers, ~L195-L260)
- **Issue:** Identical `Argon2Config` (TimeCost 4, MemoryCost 131072, etc.) is duplicated across handlers, with magic numbers inline.
- **Fix:** Extract a single `CreateDefaultArgon2Config(...)` helper (or reuse the central hasher) and name the cost constants.

### CQ-4 (Medium) — Magic numbers across services

- **Files:** [RefreshTokenService.cs](../MrWhoOidc.Auth/Services/RefreshTokenService.cs) (`1296000`, `2592000`), [WebAuthnService.cs](../MrWhoOidc.Auth/Services/WebAuthnService.cs) (`-7`, `-257`), [TenantIconService.cs](../MrWhoOidc.Auth/Services/TenantIconService.cs) (`2*1024*1024`, `100`)
- **Fix:** Replace with named constants (e.g., `DefaultRefreshTokenLifetimeSeconds`, `CoseAlgEs256 = -7`, `CoseAlgRs256 = -257`, `MaxIconFileSizeBytes`).

### CQ-5 (Medium) — `AuthDbContext` resolves logger via service location

- **File:** [MrWhoOidc.Auth/Persistence/AuthDbContext.cs](../MrWhoOidc.Auth/Persistence/AuthDbContext.cs#L176) (~L176)
- **Issue:** `this.GetService<ILogger<AuthDbContext>>()` is service-locator style.
- **Fix:** Inject `ILogger<AuthDbContext>?` via constructor or set in `OnConfiguring`.

### CQ-6 (Low) — Inconsistent null-guard style

- **Issue:** Mix of `ArgumentNullException.ThrowIfNull`, `?? throw`, and redundant guards on non-nullable params under NRT.
- **Fix:** Standardize on `ArgumentNullException.ThrowIfNull` and drop guards that NRT already enforces.

### CQ-7 (Low) — Broadly-scoped `#pragma warning disable`

- **File:** [PasswordHasher.cs](../MrWhoOidc.Auth/Services/PasswordHasher.cs) (file-level `CS0618`)
- **Fix:** Scope pragmas to the specific lines and document the legacy-compat rationale.

### CQ-8 (Low) — Potential repeated in-memory mapping build

- **File:** [ClaimMappingService.cs](../MrWhoOidc.Auth/Services/ClaimMappingService.cs) (~L30-L45)
- **Fix:** Cache the default mapping set if profiling shows it matters; otherwise leave as-is.

---

## Recommended Action Order

1. **SEC-1** — Remove built-in secrets from the production bootstrap seed; rotate. *(Critical)*
2. **SEC-2** — Add tenant predicate to client-delete checks; plan EF global query filters. *(High)*
3. **SEC-4 / CQ-1** — Narrow `PasswordHasher` catches. *(High)*
4. **SEC-3** — Tighten DPoP `iat` window + replay TTL. *(High)*
5. **SEC-5 / SEC-6 / SEC-7 / SEC-8** — Validation hardening. *(Medium)*
6. **CQ-2 → CQ-8** — Maintainability cleanups.

## Positive Observations

- Consistent `ConfigureAwait(false)` and `CancellationToken` flow.
- `AsNoTracking` on read queries; transaction/execution-strategy handling present.
- Constant-time comparisons (`FixedTimeEquals`) used for tokens and PBKDF2 verification.
- Environment-gated production guards (HTTPS metadata, forwarded headers, host allowlist, auto-seed opt-in).
- Solid unit test coverage (client secret rotation, token exchange).
