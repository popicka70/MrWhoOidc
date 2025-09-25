# Back-Channel Logout (BCL) — Production Backlog

This document captures production-level requirements, hardening items, and test coverage for implementing and running OpenID Connect Back-Channel Logout across the OP (server) and RPs (clients), including our Blazor app.

References
- OpenID Connect Back-Channel Logout 1.0: https://openid.net/specs/openid-connect-backchannel-1_0.html
- OpenID Connect RP-Initiated Logout 1.0: https://openid.net/specs/openid-connect-rpinitiated-1_0.html
- OpenID Connect Session Management (sid claim): https://openid.net/specs/openid-connect-session-1_0.html

---

## 1) OP (Authorization Server) requirements

Status summary
- Core BCL support is implemented end-to-end on the OP: discovery flags, admin config, logout_token builder, durable outbox, background dispatcher with retries/circuit breaker, and admin/health endpoints. Remaining: formal audit logging and external alert integrations.

### 1.1 Discovery and registration
- Implemented
  - Discovery advertises backchannel flags via `DiscoveryHandler`:
    - File: `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs`
    - Emits: `backchannel_logout_supported = true`, `backchannel_logout_session_supported = true`.
  - Client model fields exist with admin UI + validation:
    - Model: `BackChannelLogoutUri`, `BackChannelLogoutSessionRequired` on client entity
      (see `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`).
    - Admin UI/API validation: `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs` enforces absolute URI, HTTPS in prod, trims trailing slash, and supports dev override via `Dev:AllowHttpBackchannel`.
  - Registration CRUD exposed via Admin APIs and Razor UI (see `MrWhoOidc.WebAuth/Program.cs` admin group and `/Admin/Clients`).
- Gaps
  - Audit logging of who/what/when for backchannel field changes: TODO.
- Advertise capability in discovery:
  - backchannel_logout_supported = true
  - backchannel_logout_session_supported = true
- Per-client config stored and exposed via admin:
  - BackChannelLogoutUri (string, validated absolute HTTPS URL)
  - BackChannelLogoutSessionRequired (bool)
- Validation rules:
  - URI must be HTTPS (prod), allow http for dev only via feature flag/be-kind dev mode.
  - Max length, normalize trailing slashes; prevent duplicates.
  - Optional allow/blocklist by host.
- Admin UX/API:
  - UI fields surfaced in Admin > Clients; validation feedback.
  - CRUD via Admin API; audit who changed what and when.

### 1.2 Logout Token (logout_token) contents
- Implemented
  - Created and signed in `LogoutHandler.CreateLogoutToken(...)` using current OP signing key; includes required claims and `typ=logout+jwt` header and short expiry (`exp ~5m`).
    - File: `MrWhoOidc.WebAuth/Handlers/LogoutHandler.cs`.
  - Includes `sid` and/or `sub` when available; enforces at least one present.
- Notes
  - `aud` set to the RP `client_id` and one token is produced per RP that has `BackChannelLogoutUri` configured.
- JWT, signed by current OP signing key; no encryption required.
- Claims (per spec):
  - iss: OP issuer
  - aud: RP client_id (string or array containing it)
  - iat: issued-at (seconds)
  - jti: unique token ID
  - events: {"http://schemas.openid.net/event/backchannel-logout": {}}
  - sid and/or sub (at least one MUST be present)
- Additional rules:
  - Lifetime: accept within small skew window; OP issues with short TTL (e.g., 5 minutes max).
  - Minimize PII; include only mandatory claims.
  - One logout_token per RP BackChannelLogoutUri.

### 1.3 Delivery semantics
- Implemented
  - Delivery is fan-out via durable outbox + background worker:
    - Outbox entity: `BackchannelLogoutNotification` (see `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`).
    - Dispatcher: `BackchannelLogoutDispatcher` with bounded concurrency, exponential backoff with jitter, retry on 5xx/408/429, and per-client circuit breaker.
      - File: `MrWhoOidc.WebAuth/Background/BackchannelLogoutDispatcher.cs`.
  - POST as `application/x-www-form-urlencoded` with `logout_token` to each `BackChannelLogoutUri`.
  - Telemetry: logs attempt/status/duration; metrics counters/histograms via `OidcMetrics`.
- Gaps
  - External alerting hooks not wired (currently warning logs for thresholds).
- HTTP POST to BackChannelLogoutUri as application/x-www-form-urlencoded with parameter logout_token.
- Backoff/retry strategy:
  - Timeouts (e.g., 5s), retriable status codes (>=500, 408, 429).
  - Exponential backoff with jitter; bounded retries.
  - Circuit breaker per RP to avoid cascades.
- Fan-out safety:
  - Don’t blast hundreds of requests at once; use bounded concurrency.
  - Per-client queue/outbox option for durability (see 1.5).
- Idempotency:
  - OP can resend same logical logout (don’t assume single delivery).
- Telemetry:
  - Log attempt, status, duration, response snippet; tag with client_id.
  - Metrics: success rate, P95 latency, retry counts, circuit open time.

### 1.4 Event sources to trigger BCL
- Implemented
  - RP-initiated logout (`/connect/endsession`) enqueues BCL outbox entries.
    - Files: `MrWhoOidc.WebAuth/Program.cs` route map and `Handlers/LogoutHandler.cs`.
- Planned
  - Admin-forced user logout (global sign-out) — not yet implemented.
  - Session revocation push; key-rotation induced global logout — not yet implemented.
- Admin-forced user logout (global sign-out):
  - For a user (sub), notify all RPs that have sessions for the user (if known), else all clients.
- Session revocation (sid invalidation) — proactive BCL push.
- Key rotation-induced global logouts (optional policy).

### 1.5 Reliability & durability (optional but recommended for prod)
- Implemented
  - Durable outbox table with status/attempts/next-attempt and dead-letter after max retries.
  - Admin endpoints for inspection and manual retry:
    - `GET /admin/api/bcl/outbox?status=...&take=...`
    - `POST /admin/api/bcl/outbox/{id}/retry`
    - File: `MrWhoOidc.WebAuth/Program.cs`.
  - Health endpoint: `GET /health/backchannel` returns backlog and open circuits; logs warnings on thresholds.

### 1.6 Security & compliance
- Only issue standard claims; avoid extra PII.
- Ensure TLS; consider mTLS between OP and RPs in sensitive environments.
- Strong key management for signing (rotation policies; track kid).
- Audit logs for who/what/when of logout events.
- Rate-limit BCL emissions per RP; per-tenant throttles.
- Respect privacy laws (e.g., keep only necessary logs, retention schedules).

Status
- HTTPS enforcement for backchannel URIs in Admin UI (prod) with dev override: Implemented.
- mTLS between OP and RP: Not implemented.
- Audit logs: Not implemented (add as follow-up).
- Explicit rate-limiting for BCL emissions: Not implemented beyond circuit breaker; consider per-client throttles.

### 1.7 Observability & ops
- Structured logs for each POST with correlation id.
- Metrics: emitted_count, success_count, fail_count, retry_count, latency, per-client breakdown.
- Alerts:
  - Failure rate > X% over Y minutes
  - Latency > threshold
  - Outbox backlog > threshold

Status
- Logs and metrics present in dispatcher; admin and health endpoints exposed (see 1.5).
- Alerts: Threshold logging only; integrate with your alerting stack (e.g., App Insights, Prometheus Alertmanager) — TODO.

### 1.8 Testing
- Unit: token creation, claims, signing; fan-out selection; retry decision logic.
- Integration: OP emits logout_token to a test RP endpoint; verify claims and signature.
- Chaos: inject timeouts/5xx; ensure retries and no OP outage.
- Conformance: verify claims against the spec; negative tests (missing events/sid/sub).

Status
- No dedicated BCL unit/integration tests added yet — add coverage as outlined above.

---

## 2) RP (Blazor app) requirements

Status summary
- Minimal RP receiver implemented with cookie revocation hook; strict validation, JWKS signature verification, and replay protection still TODO. In-memory store used for dev; move to distributed cache for prod.

### 2.1 Endpoint
- Implement POST /backchannel-logout:
  - Content-Type: application/x-www-form-urlencoded; require logout_token param.
  - No cookies/session required; ensure CSRF not applicable.
  - Body size limits; reject excessively large payloads.

Status
- Implemented receiver in `MrWhoOidc.Web/Program.cs`:
  - Route: `POST /backchannel-logout` reads form `logout_token`, extracts `sid` (no full validation), revokes sid in store, returns 200.
  - Dev store: `BackchannelLogoutStore` (in-memory) with `IsSidRevoked`/`RevokeSid`.
- TODO: enforce body size limits and content length guard.

### 2.2 Validation of logout_token
- Validate signature using OP’s JWKS (discover via authority).
- Validate claims:
  - iss matches configured authority’s issuer.
  - aud includes our client_id.
  - events contains key "http://schemas.openid.net/event/backchannel-logout".
  - iat within acceptable skew (e.g., 2–5 minutes).
  - jti not seen before (replay cache with TTL ≥ skew window).
  - sid and/or sub present; process per session model below.
- Only accept tokens from allowed issuers; reject unknown.
- Handle key rollover: refresh JWKS on kid miss; cache with TTL and backoff.

Status
- Not implemented yet in `MrWhoOidc.Web`. A reusable `IJwksCache` exists (`MrWhoOidc.Auth/Services/JwksCache.cs`) and can be used for RP validation logic.
- Next steps:
  - Add JWKS-based signature validation + issuer/audience/events checks.
  - Add replay cache for `jti` with TTL.

### 2.3 Session mapping and invalidation
- If sid present:
  - Map sid to one or more local sessions; mark them revoked immediately.
- If only sub present:
  - Revoke all sessions for that subject and issuer for this RP.
- Storage:
  - Use distributed cache (Redis) to store revoked sids and consumed jtis with TTL.
  - Memory fallback for single-instance dev.
- Cookie validation hook:
  - On each request, reject cookies with a revoked sid (sign out + redirect to login).

Status
- Cookie validation hook implemented: in `MrWhoOidc.Web/Program.cs` `CookieAuthenticationOptions.Events.OnValidatePrincipal` checks `sid` and signs out if revoked (via `BackchannelLogoutStore`).
- Storage
  - Dev: in-memory `BackchannelLogoutStore` (single-instance only).
  - TODO: move to distributed cache (e.g., Redis) and implement `jti` replay cache.

### 2.4 Reliability & safety
- Endpoint should return 200 OK upon successful processing (after validation & revocation).
- For invalid tokens:
  - Return 400/401; log why; rate-limit to mitigate abuse.
- Idempotency: repeated logout_token requests result in same final state; safe no-ops.
- Concurrency: atomic revocation; avoid races on multi-instance.

Status
- Returns 200 after best-effort revocation; detailed error codes/rate-limiting: TODO.

### 2.5 Security
- Accept only POST; require form-encoded; reject other content types.
- Validate signature and issuer strictly; do not rely on IPs.
- Enforce small body limit; short read timeout.
- Protect JWKS retrieval with caching; guard against cache poisoning.
- Observability and alerting:
  - Log reason, sid/sub, iss (anonymize/pseudonymize if needed).
  - Metrics for accepted vs. rejected, reason codes, per-issuer counts.

Status
- Basic POST/form-only: implemented.
- Strict validation/signature + observability: TODO.

### 2.6 Admin & ops
- Feature flags: enable/disable backchannel processing.
- Admin UI:
  - Inspect recent logout notifications (summary), last error.
  - View currently revoked sids (with TTL), purge entries.
- Configurable policy:
  - Skew window, jti cache TTL, sid TTL.
  - Allowed issuers; JWKS cache TTL; backoff for JWKS refresh.

Status
- Feature flag exists on OP side (`BackchannelFeatureOptions.Enabled`) controlling emissions; no RP-side flag yet.
- Admin UI endpoints exist on OP for backchannel outbox; RP-side: not applicable (yet).

### 2.7 Testing
- Unit: validation logic, sid/sub mapping, cookie validator behavior.
- Integration: end-to-end with OP; ensure session invalidation and subsequent request rejection.
- Negative tests: invalid signature, wrong iss/aud, missing events.

---

## 3) Cross-cutting concerns

### 3.1 Rollout & compatibility
- Backward-compatible migration: existing clients unaffected until they set BackChannelLogoutUri.
- Staged rollout: canary clients first; monitor metrics; expand.
- Rollback: feature flag to disable emissions and/or acceptance.

Status
- Backward-compat supported (only clients with `BackChannelLogoutUri` receive BCL).
- Feature flag present to disable emissions quickly.

### 3.2 Performance & scale
- Fan-out bounded concurrency configuration.
- RP endpoint optimized: minimal allocations, fast signature check via cached validation parameters.
- Consider batching global logout triggers (admin-induced) to avoid thundering herds.

Status
- Bounded concurrency implemented in dispatcher; batching of admin/global triggers: TODO (after 1.4 planned work).

### 3.3 Security reviews
- Threat modeling: replay, token forgery, endpoint abuse, SSRF risks.
- Pen-test scenarios specific to BCL endpoints and token validation.

Status
- Pending once validation is added on RP.

### 3.4 Documentation & runbooks
- Operator runbook for mass logout.
- Troubleshooting guide: reading metrics, locating failures, replays, JWKS issues.
- Client integration guide: how to configure BackChannelLogoutUri and verify behavior.

Status
- Pending — add docs after RP validation impl.

---

## 4) Deliverables checklist

- [x] OP: Discovery metadata includes backchannel flags (DiscoveryHandler)
- [x] OP: Client model + migration for BackChannelLogoutUri + session required (AuthDbContext) — ensure migration applied in all envs
- [ ] OP: Admin for backchannel fields
  - [x] UI/API + validation
  - [ ] Audit logging
- [x] OP: logout_token builder (iss, aud, iat, jti, events, sid/sub)
- [x] OP: Fan-out dispatcher with retries, backoff, circuit breaker
- [ ] OP: Telemetry/outbox
  - [x] Durable outbox + admin + health
  - [x] Logs + metrics
  - [ ] Alerts integration (external)
- [ ] RP: /backchannel-logout endpoint
  - [x] Endpoint (POST form), revokes sid
  - [ ] Strict validation (sig, iss, aud, events, iat, jti replay)
- [ ] RP: JWKS validation with caching and rollover handling (use `IJwksCache`)
- [ ] RP: Replay protection (jti cache) + sid revocation store (distributed)
- [x] RP: Cookie validation hook rejects revoked sids (in-memory) — upgrade to distributed
- [ ] RP: Telemetry (logs, metrics, alerts)
- [ ] Tests: unit + integration + chaos; conformance checks
- [ ] Docs: operator runbook, client integration guide
- [x] Feature flags & safe rollout plan (emission flag, allow/block host lists)

---

## 5) Open questions / future enhancements

- Should OP authentication to RP backchannel be added (e.g., mTLS) in certain environments?
- Multi-tenant: per-tenant issuer isolation and limits.
- Long-lived sessions: automated periodic cleanup of stale revoked-sid entries.
- Admin UI for live fan-out monitoring and per-client pause/resume.

Next steps (near-term)
- RP: Implement strict validation with JWKS signature check, claim validation, and `jti` replay cache; add distributed revocation store.
- OP: Add audit logging and connect dispatcher metrics/thresholds to alerting system.
- Tests: add unit tests for token builder and dispatcher retry logic; integration test OP->RP flow.