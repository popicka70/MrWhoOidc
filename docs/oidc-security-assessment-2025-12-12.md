# MrWhoOidc OIDC / OAuth Security Assessment (2025-12-12)

This document reviews the **MrWhoOidc** authorization server (IdP/OP) implementation with a focus on security, correctness against OIDC/OAuth specs, and practical hardening steps.

## Scope and constraints

- Codebase scope: `MrWhoOidc.WebAuth` (HTTP surface) + `MrWhoOidc.Auth` (core protocol/services) + `MrWhoOidc.Security` (DPoP helpers).
- Assumptions:
  - The server is deployed behind a trusted reverse proxy and TLS is terminated safely.
  - Multi-tenant mode is used (`/t/{slug}`), but root endpoints exist as fallback.
- Repo constraint: **Do not add OpenIddict / Microsoft Identity Platform packages.**

## What looks solid

- Redirect allow-listing is implemented with normalization and strict allow-list checks (`MrWhoOidc.Auth/Utils/UrlComparison.cs`, `MrWhoOidc.Auth/Services/AuthorizeService.cs`).
- PKCE (S256) and code consumption/expiry checks are present in token issuance flows.
- RFC 9207 `iss` parameter is added to authorization responses to mitigate mix-up style attacks.
- Durable outbox + background fan-out exists for Back-Channel Logout with retry/circuit-breaker (`MrWhoOidc.WebAuth/Background/BackchannelLogoutDispatcher.cs`).
- DPoP is implemented with `jkt` binding, nonce support, and replay cache on resource-style endpoints (`MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs`, `MrWhoOidc.WebAuth/Handlers/Introspection/DPoPValidator.cs`).

## Findings (prioritized)

## Implementation status (as of 2025-12-13)

This checklist reflects what is implemented in code vs what remains as operational/config work.

### Findings

- [x] **C1 (forwarded headers spoofing)**: Safe-by-default forwarded headers configuration (no blanket trust of `X-Forwarded-*`) + regression test coverage.
- [ ] **C1 (deployment)**: Production should explicitly configure `ForwardedHeaders:KnownProxies`/`KnownNetworks` and/or `ForwardedHeaders:AllowedHosts` (and set `Oidc:PublicBaseUrl` or `Oidc:Issuer`) to avoid proxy/host ambiguity.

- [x] **C2 (`aud` validation)**: Audience enforced for access-token validation at `/userinfo` (with tests rejecting unexpected audiences).

- [x] **C3 (DPoP replay at `/token`)**: DPoP proof replay prevention enforced at `/token` (distributed-capable where configured).

- [x] **H1 (authorize query logging)**: No raw `/authorize` query string logging in the request logs.

- [x] **H2 (no-store headers)**: `Cache-Control: no-store` + `Pragma: no-cache` consistently applied on token-like endpoints (including error paths) with regression tests.

- [x] **H3 (auto-seed risk)**: Auto-seed restricted to dev/test only; explicit one-time bootstrap endpoint exists.

- [x] **H4 (PAR rate limiting)**: PAR rate limiting enforced via ASP.NET rate limiting; Redis-backed limiter used when configured.

- [x] **M1 (`typ` enforcement)**: Access tokens emitted with `typ=at+jwt` and enforced at `/userinfo`.

- [x] **M2 (DPoP error normalization)**: DPoP validation avoids leaking raw exception messages.

- [x] **L1 (PII in logs)**: Subject identifiers are tokenized/hashed in logs.

### Roadmap (optional)

- [ ] **FAPI alignment mode**: Require PAR for all clients; tighten redirect URI registration; consider JAR/JARM requirements for high-risk clients.

- [x] **Issuer and host safety (code)**: Canonical issuer derivation prefers configured base + tenant issuer over request host; issuer health endpoint encourages explicit configuration.
- [ ] **Issuer and host safety (deployment)**: Enforce/operate with a strict allowed-host policy (`ForwardedHeaders:AllowedHosts`) in production.

- [x] **Session/logout completeness (RP sample)**: Logout token validation hardened (typ/events/jti replay + `iat` required and bounded).

- [x] **Security headers for Razor Pages (baseline)**: HTML-only security headers middleware added (incl. CSP).
- [ ] **Security headers for Razor Pages (expand)**: Consider adding `Permissions-Policy` and verify CSP against all UI pages.

- [ ] **DPoP completeness (consistency)**: Review nonce/replay behavior consistency across `/token`, `/userinfo`, and `/introspect` for your intended threat model.

### Critical

#### C1. Unrestricted trust of forwarded headers (host/proto) can corrupt issuer/endpoint URLs

**Status**
- [x] **Code**: Forwarded headers are configured safe-by-default (no blanket trust of `X-Forwarded-*`).
- [x] **Tests**: Regression coverage exists for forwarded-host spoofing.
- [ ] **Deployment**: Production must explicitly configure `ForwardedHeaders:KnownProxies`/`KnownNetworks` and/or `ForwardedHeaders:AllowedHosts` (and set `Oidc:PublicBaseUrl` or `Oidc:Issuer`).

**Evidence**
- `MrWhoOidc.WebAuth/Infrastructure/Pipeline/PipelineExtensions.cs` clears `KnownNetworks` and `KnownProxies` and enables forwarded headers (`X-Forwarded-For`, `X-Forwarded-Proto`, `X-Forwarded-Host`).

**Why it matters**
- If the service is ever reachable directly (or a proxy misconfig allows spoofed forwarded headers), an attacker can influence `Request.Scheme`/`Request.Host`, which can:
  - change generated absolute endpoint URLs,
  - change computed issuer URLs/tenant issuer URIs,
  - break redirect validation assumptions,
  - cause token endpoint DPoP `htu` validation to use an attacker-controlled host.

**Recommended fix**
- Lock down forwarded headers by configuring *known proxies/networks* (or at minimum enforce an allow-list of hosts).
- Consider setting a canonical public base URL per tenant and using that for issuer + endpoint URL construction, rather than `Request.Host`.

---

#### C2. JWT access token validation does not validate audience (accepts any `aud`)

**Status**
- [x] **Implemented**: Audience is enforced for access-token validation at `/userinfo`.
- [x] **Tests**: Tokens with unexpected audiences are rejected.

**Evidence**
- `MrWhoOidc.Auth/Services/TokenValidator.cs`: `ValidateAudience = false`.
- `/userinfo` uses this validator (`MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs`).

**Why it matters**
- If multiple audiences/resources exist (or if another internal issuer/audience boundary matters), accepting any `aud` can allow tokens minted for *other* resources to be accepted at `/userinfo` (or any endpoint using the same validator).

**Recommended fix**
- Enforce an expected audience (or a policy-based set of allowed audiences) for endpoints that validate access tokens.
  - For `/userinfo`, consider requiring an audience dedicated to userinfo, or enforce `aud` == the client_id / configured userinfo audience (depends on your model).
- Add tests ensuring tokens for unexpected audiences are rejected.

---

#### C3. DPoP proof replay protection is missing at `/token`

**Status**
- [x] **Implemented**: `/token` enforces DPoP proof replay prevention.
- [x] **Note**: Distributed support is available when configured.

**Current status (code as of 2025-12-12 in this workspace)**
- `/token` enforces DPoP replay protection via `MrWhoOidc.WebAuth/Infrastructure/DpopValidationHelper.cs` using `IDPoPReplayCache` (key `${jkt}:${jti}`, 5-minute window).
- `MrWhoOidc.Security/DPoP.cs` remains a pure validator (returns `jti`/`iat`); replay prevention is enforced at endpoint layer.

**Why it matters**
- If an attacker can replay a token request (e.g., via compromised client, telemetry, reverse proxy logs, or request duplication), the DPoP proof does not prevent reuse. DPoP is intended to make replay materially harder; replay cache is a core part of that.

**Recommended fix**
- Keep the current endpoint-layer replay enforcement.
- Prefer a distributed cache implementation in multi-instance deployments.

### High

#### H1. Authorization endpoints log full query strings (risk of leaking sensitive parameters)

**Status**
- [x] **Verified**: No raw `/authorize` query string logging in the request logs.

**Evidence**
- Previously flagged risk: logging raw `/authorize` query strings.

**Current status (code as of 2025-12-12 in this workspace)**
- No raw `/authorize` query string logging was found in `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs` (the entry log includes only Path).

**Why it matters**
- `/authorize` query strings frequently include values that must not leak (e.g., `state`, `nonce`, `login_hint`, `code_challenge`, and sometimes `request` / `request_uri`).
- Logs often flow to centralized systems; leakage increases risk of session correlation, CSRF token theft, and privacy incidents.

**Recommended fix**
- Stop logging raw query strings for auth endpoints.
- If you must log, log only:
  - `client_id` (or a hash/bucket),
  - `response_type`,
  - presence flags (e.g., “has_request_uri=true”).

---

#### H2. `/token` responses are missing required no-store caching headers

**Status**
- [x] **Implemented**: Token-like endpoints include `Cache-Control: no-store` and `Pragma: no-cache` (including error paths).
- [x] **Tests**: Regression coverage exists.

**Current status (code as of 2025-12-12 in this workspace)**
- `/token` sets `Cache-Control: no-store` and `Pragma: no-cache` in `MrWhoOidc.WebAuth/Handlers/TokenHandler.cs`.
- `/revoke` sets `Cache-Control: no-store` and `Pragma: no-cache` in `MrWhoOidc.WebAuth/Handlers/RevocationHandler.cs`.
- `/introspect` sets `Cache-Control: no-store` and `Pragma: no-cache` in `MrWhoOidc.WebAuth/Handlers/Introspection/IntrospectionHandler.cs`.
- `/par` sets `Cache-Control: no-store` on success (`MrWhoOidc.WebAuth/Handlers/ParHandler.cs`).

**Why it matters**
- OAuth 2.0 token responses should not be cached by intermediaries.

**Recommended fix**
- Ensure **all** token-like endpoints (success and error) include:
  - `Cache-Control: no-store`
  - `Pragma: no-cache`

---

#### H3. Auto-seeding default tenant/platform admin is risky in non-dev environments

**Status**
- [x] **Implemented**: Auto-seed is Development/test only.
- [x] **Implemented**: Explicit one-time bootstrap endpoint exists (`POST /bootstrap`) guarded by `X-Bootstrap-Token` and only allowed when DB is empty.

**Evidence**
- `MrWhoOidc.WebAuth/Middleware/AutoSeedMiddleware.cs` creates a default tenant when none exist, with hard-coded values (e.g., `AdminEmail = "admin@mrwho.local"`).

**Current status (code as of 2025-12-12 in this workspace)**
- Auto-seeding is now **development/test only** (Development or `Testing:EnableAutoSeed=true`).
- A token-guarded explicit bootstrap endpoint exists at `POST /bootstrap` (see `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/BootstrapEndpointMappingExtensions.cs`).
  - It is safe-by-default: returns 404 unless `Bootstrap:Token` is configured.
  - It only runs when the database has **no tenants**.

**Why it matters**
- If this middleware is enabled in production, an attacker who hits a fresh deployment first could influence initial state (at minimum, it creates a tenant automatically).
- It also mixes synchronous EF calls inside a lock and relies on tenant context being present later, which is fragile.

**Recommended fix**
- Restrict auto-seeding to development/test environments only.
- Replace with explicit admin bootstrap workflow (one-time setup token, CLI, or guarded endpoint).
  - This repo implements the guarded endpoint: `POST /bootstrap` with `X-Bootstrap-Token`.

---

#### H4. PAR rate limiting is in-memory (can be bypassed in multi-instance deployments)

**Status**
- [x] **Implemented**: PAR rate limiting enforced via ASP.NET rate limiting; Redis-backed limiter used when Redis is configured.

**Previous evidence**
- `MrWhoOidc.WebAuth/Handlers/ParHandler.cs` previously included a per-client in-memory limiter.

**Current status (code as of 2025-12-13 in this workspace)**
- PAR rate limiting is enforced via ASP.NET rate limiting policy `rl-par` and partitions by client id (or IP fallback).
- When Redis is configured (`ConnectionStrings:redis`), `rl-par` uses a Redis-backed fixed-window limiter (`MrWhoOidc.WebAuth/Infrastructure/RedisFixedWindowRateLimiter.cs`) to make limits effective across multiple instances.

**Why it matters**
- In multi-instance deployments, clients can spread traffic across instances and bypass local limits.

**Recommended fix**
- Use a distributed limiter (Redis) or ASP.NET rate limiting primitives with a distributed store.

### Medium

#### M1. Access token type (`typ`) is not enforced during validation

**Status**
- [x] **Implemented**: Access tokens emitted with `typ=at+jwt` and enforced at `/userinfo`.

**Evidence**
- `MrWhoOidc.Auth/Services/TokenValidator.cs` does not check `typ`.

**Current status (code as of 2025-12-12 in this workspace)**
- Access tokens are emitted with `typ=at+jwt` (via `MrWhoOidc.Auth/Services/JwtService.cs`).
- `/userinfo` enforces `typ=at+jwt` and also requires the OAuth `scope` claim to reduce accidental acceptance of non-access tokens.

**Why it matters**
- Without `typ` enforcement (and/or other token-use constraints), endpoints like `/userinfo` can become more permissive than intended (e.g., potentially accepting an `id_token` if signature/lifetime pass and claims look plausible).

**Recommended fix**
- Add a token-type check for endpoints (e.g., require `typ` == `at+jwt` if you emit it, or enforce a custom claim indicating access-token usage).

---

#### M2. DPoP validator returns raw exception messages as error strings

**Status**
- [x] **Implemented**: DPoP validation normalizes unexpected exceptions (no raw exception messages).

**Current status (code as of 2025-12-12 in this workspace)**
- `MrWhoOidc.Security/DPoP.cs` normalizes unexpected exceptions to `validation_error` (does not return raw exception messages).

**Why it matters**
- If these messages leak to clients (now or in future changes), they can become an information disclosure channel.

**Recommended fix**
- Normalize error codes (already partly done, e.g., `htu_mismatch`, `invalid_iat`) and avoid returning raw exception strings.

### Low

#### L1. Potential PII in logs (e.g., user subject)

**Status**
- [x] **Implemented**: Logs tokenize/hash subject identifiers.

**Evidence**
- `/userinfo` previously logged raw `sub` on 200 responses (`MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs`).

**Current status (code as of 2025-12-12 in this workspace)**
- `/userinfo` logs a short hash token (`sub_hash`) instead of the raw subject.
- Tenant switching and password flows also tokenize `sub`/subject identifiers in logs.

**Recommended fix**
- Hash/tokenize identifiers in logs, or downgrade to debug with sampling.

## OIDC / OAuth hardening roadmap (extensions)

These are optional but recommended enhancements depending on your target threat model and compliance goals.

1. **FAPI alignment mode (optional):**
   - Require PAR for all clients.
   - Tighten redirect URI registration and enforce exact matching.
   - Consider requiring JAR with signed request objects for high-risk clients.

2. **Issuer and host safety:**
   - Single canonical issuer per tenant derived from configuration, not request headers.
   - Enforce strict allowed hosts list.

3. **DPoP completeness:**
   - Add replay cache checks at `/token`.
   - Ensure nonce + replay behavior is consistent across `/token`, `/userinfo`, `/introspect`.

4. **Session/logout completeness:**
   - Evaluate front-channel logout / session management features if relying parties need them.
   - Add strict validation + replay protection for logout tokens at RP sample receiver (noted as TODOs in repo docs).

5. **Security headers for Razor Pages:**
   - Add a CSP appropriate for the login/consent UI.
   - Add `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy` as appropriate.

## Suggested test coverage improvements

- Token audience validation tests for `/userinfo` tokens.
- DPoP replay tests for `/token` when `DPoP` is present.

**Current status (code as of 2025-12-13 in this workspace)**
- Forwarded headers spoof regression test added (ensures unallowed `X-Forwarded-Host` is ignored).
- Cache header regression tests added for `/revoke` and `/introspect` (ensures `Cache-Control: no-store` and `Pragma: no-cache` even on error paths).

## Appendix: key code locations

- Forwarded headers: `MrWhoOidc.WebAuth/Infrastructure/Pipeline/PipelineExtensions.cs`
- `/authorize` query logging: `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs`
- Access token validation: `MrWhoOidc.Auth/Services/TokenValidator.cs`
- DPoP core: `MrWhoOidc.Security/DPoP.cs`
- `/token` DPoP helper: `MrWhoOidc.WebAuth/Infrastructure/DpopValidationHelper.cs`
- Auto seed: `MrWhoOidc.WebAuth/Middleware/AutoSeedMiddleware.cs`
- PAR: `MrWhoOidc.WebAuth/Handlers/ParHandler.cs`
