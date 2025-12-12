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

### Critical

#### C1. Unrestricted trust of forwarded headers (host/proto) can corrupt issuer/endpoint URLs

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

**Evidence**
- `MrWhoOidc.Security/DPoP.cs` (`DPoPValidator.ValidateForEndpointAsync`) returns `jti`/`iat` but does **not** enforce replay.
- `/token` uses `MrWhoOidc.WebAuth/Infrastructure/DpopValidationHelper.cs` which validates proof but does **not** consult `IDPoPReplayCache`.
- Replay checks exist for `/userinfo` and `/introspect` (`TryAdd(jkt:jti)` logic), but not for `/token`.

**Why it matters**
- If an attacker can replay a token request (e.g., via compromised client, telemetry, reverse proxy logs, or request duplication), the DPoP proof does not prevent reuse. DPoP is intended to make replay materially harder; replay cache is a core part of that.

**Recommended fix**
- Add replay-cache enforcement for `/token` requests when `DPoP` is present:
  - require `jti` + `iat`,
  - `TryAdd($"{jkt}:{jti}", iat+5m)` and reject if already present.
- Prefer a distributed cache implementation in multi-instance deployments.

### High

#### H1. Authorization endpoints log full query strings (risk of leaking sensitive parameters)

**Evidence**
- `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs` logs `QueryString` at entry.
- Similar patterns exist in other auth-adjacent flows (QR/external callback handlers/pages).

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

**Evidence**
- `/par` explicitly sets `Cache-Control: no-store` (`MrWhoOidc.WebAuth/Handlers/ParHandler.cs`).
- No `Cache-Control: no-store` / `Pragma: no-cache` header setting was found in `/token` handling paths (e.g., `MrWhoOidc.WebAuth/Handlers/TokenHandler.cs` and token grant handlers under `MrWhoOidc.WebAuth/TokenEndpoint/Grants/*`).

**Why it matters**
- OAuth 2.0 token responses should not be cached by intermediaries.

**Recommended fix**
- Ensure **all** token endpoint responses (success and error) include:
  - `Cache-Control: no-store`
  - `Pragma: no-cache`

---

#### H3. Auto-seeding default tenant/platform admin is risky in non-dev environments

**Evidence**
- `MrWhoOidc.WebAuth/Middleware/AutoSeedMiddleware.cs` creates a default tenant when none exist, with hard-coded values (e.g., `AdminEmail = "admin@mrwho.local"`).

**Why it matters**
- If this middleware is enabled in production, an attacker who hits a fresh deployment first could influence initial state (at minimum, it creates a tenant automatically).
- It also mixes synchronous EF calls inside a lock and relies on tenant context being present later, which is fragile.

**Recommended fix**
- Restrict auto-seeding to development/test environments only.
- Replace with explicit admin bootstrap workflow (one-time setup token, CLI, or guarded admin endpoint).

---

#### H4. PAR rate limiting is in-memory (can be bypassed in multi-instance deployments)

**Evidence**
- `MrWhoOidc.WebAuth/Handlers/ParHandler.cs` uses per-client in-memory limiter with a comment noting distributed replacement.

**Why it matters**
- In multi-instance deployments, clients can spread traffic across instances and bypass local limits.

**Recommended fix**
- Use a distributed limiter (Redis) or ASP.NET rate limiting primitives with a distributed store.

### Medium

#### M1. Access token type (`typ`) is not enforced during validation

**Evidence**
- `MrWhoOidc.Auth/Services/TokenValidator.cs` does not check `typ`.

**Why it matters**
- Without `typ` enforcement (and/or other token-use constraints), endpoints like `/userinfo` can become more permissive than intended (e.g., potentially accepting an `id_token` if signature/lifetime pass and claims look plausible).

**Recommended fix**
- Add a token-type check for endpoints (e.g., require `typ` == `at+jwt` if you emit it, or enforce a custom claim indicating access-token usage).

---

#### M2. DPoP validator returns raw exception messages as error strings

**Evidence**
- `MrWhoOidc.Security/DPoP.cs` catches exceptions and returns `ex.Message` in `DPoPValidationResult.Error`.

**Why it matters**
- If these messages leak to clients (now or in future changes), they can become an information disclosure channel.

**Recommended fix**
- Normalize error codes (already partly done, e.g., `htu_mismatch`, `invalid_iat`) and avoid returning raw exception strings.

### Low

#### L1. Potential PII in logs (e.g., user subject)

**Evidence**
- `/userinfo` logs `Sub` on 200 responses (`MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs`).

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
- Forwarded headers tests (ensure spoofed `X-Forwarded-Host` cannot change issuer/endpoint behavior when not from a known proxy).

## Appendix: key code locations

- Forwarded headers: `MrWhoOidc.WebAuth/Infrastructure/Pipeline/PipelineExtensions.cs`
- `/authorize` query logging: `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs`
- Access token validation: `MrWhoOidc.Auth/Services/TokenValidator.cs`
- DPoP core: `MrWhoOidc.Security/DPoP.cs`
- `/token` DPoP helper: `MrWhoOidc.WebAuth/Infrastructure/DpopValidationHelper.cs`
- Auto seed: `MrWhoOidc.WebAuth/Middleware/AutoSeedMiddleware.cs`
- PAR: `MrWhoOidc.WebAuth/Handlers/ParHandler.cs`
