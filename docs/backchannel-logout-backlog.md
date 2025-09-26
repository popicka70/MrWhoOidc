# Back-Channel Logout (BCL) — Production Backlog

This document captures production-level requirements, hardening items, and test coverage for implementing and running OpenID Connect Back-Channel Logout across the OP (server) and RPs (clients), including our Blazor app.

References
- OpenID Connect Back-Channel Logout 1.0: https://openid.net/specs/openid-connect-backchannel-1_0.html
- OpenID Connect RP-Initiated Logout 1.0: https://openid.net/specs/openid-connect-rpinitiated-1_0.html
- OpenID Connect Session Management (sid claim): https://openid.net/specs/openid-connect-session-1_0.html

---

## 1) OP (Authorization Server) requirements

Status summary
- Core BCL support is implemented end-to-end on the OP: discovery flags, admin config, logout_token builder, durable outbox, background dispatcher with retries/circuit breaker, and admin/health endpoints. Audit logging implemented (structured logger sink with hashing) and alert hooks present; external alert wiring still TODO.

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
  - None for audit of admin backchannel field changes (implemented via structured audit sink).
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

Audit logging requirements (OP)
- What to audit (minimum):
  - Admin changes to backchannel-related client fields (BackChannelLogoutUri, BackChannelLogoutSessionRequired): who (user id/name), when (UTC), where (IP), what changed (old -> new), client_id, correlation id.
  - Dispatcher lifecycle events: notification enqueued, dequeued, delivery attempted, success, failed (status/reason), dead-lettered, manual retry invoked.
  - Outbox admin actions: list/export, single retry, bulk retry, purge.
- Data hygiene:
  - Never log raw logout_token; redact or omit JWT entirely.
  - Hash sid/sub in audit (e.g., SHA-256 with salt/pepper) if included for correlation.
  - Include notification id and client_id to correlate across logs and metrics.
- Storage & retention:
  - Structured, append-only audit stream (e.g., JSON) to central sink (App Insights/Log Analytics/ELK) with retention aligned to policy (e.g., 90 days, configurable).
  - Local fallback to rolling file for dev.
  - Feature flag to enable/disable audit emission (dev/test).
 

Status
- HTTPS enforcement for backchannel URIs in Admin UI (prod) with dev override: Implemented.
- mTLS between OP and RP: Not implemented.
- Audit logs: Implemented (structured events with PII hashing and dev-toggle sink).
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

Planned alerting integration (target by prod cutover)
- Sinks supported (choose one per environment):
  - Azure Application Insights/Log Analytics: export counters/histograms as customMetrics and use Metric Alerts.
  - Prometheus/Alertmanager: expose metrics via existing scraping endpoint; configure recording rules and alerts.
- Configurable thresholds (appsettings):
  - Backchannel:Alerts:Enabled (bool)
  - Backchannel:Alerts:FailureRatePercent (default 5)
  - Backchannel:Alerts:LatencyP95Ms (default 2000)
  - Backchannel:Alerts:OutboxBacklogThreshold (default 50)
  - Backchannel:Alerts:ConsecutiveMinutes (default 5)
- Azure App Insights specifics:
  - Metrics: Oidc.Bcl.SuccessCount, Oidc.Bcl.FailCount, Oidc.Bcl.EmittedCount, Oidc.Bcl.RetryCount, Oidc.Bcl.LatencyMs (histogram), Oidc.Bcl.OutboxBacklog.
  - Create Metric Alerts (static) with Action Group routing to on-call: failure rate percent > threshold over rolling window; P95 latency > threshold; backlog > threshold.
- Prometheus specifics:
  - Rules (examples):
    - alert: BclHighFailureRate
      expr: rate(oidc_bcl_fail_total[5m]) / rate(oidc_bcl_emitted_total[5m]) * 100 > 5
      for: 5m
    - alert: BclHighLatency
      expr: histogram_quantile(0.95, sum(rate(oidc_bcl_latency_ms_bucket[5m])) by (le)) > 2000
      for: 5m
    - alert: BclOutboxBacklogHigh
      expr: oidc_bcl_outbox_backlog > 50
      for: 5m


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
- RP receiver implemented with strict logout_token validation (signature, iss, aud, events, typ, jti replay) via `LogoutTokenValidator` plus distributed/in-memory revocation + replay caches. Cookie revocation hook active. Remaining: richer telemetry, rate limiting, admin visibility, sub-only session mapping, hardened replay (atomic add for Redis), body size config, and documentation.

### 2.1 Endpoint
- Implement POST /backchannel-logout:
  - Content-Type: application/x-www-form-urlencoded; require logout_token param.
  - No cookies/session required; ensure CSRF not applicable.
  - Body size limits; reject excessively large payloads.

Status
- Implemented receiver in `MrWhoOidc.Web/Program.cs`:
  - Route: `POST /backchannel-logout` reads form `logout_token` (form-encoded), enforces content length (<= 8KB), invokes validator, revokes `sid`, returns 200.
  - Validation + revocation store now integrated (see sections below).
- TODO: make max body size configurable; add 415 rejection for wrong content type; structured logging of reasons for 4xx/401 responses.

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
- Implemented in `LogoutTokenValidator`:
  - Discovers metadata, validates issuer/audience, signature (with JWKS and rollover retry), `typ=logout+jwt`, `events` claim structure, `jti` uniqueness (replay cache), presence of `sid` or `sub`.
  - Uses `IJwksCache`, `IReplayCache`, and distributed or in-memory implementations.
- Remaining:
  - Add explicit logging for each validation failure reason (currently generic warning on exception + terse error codes).
  - Optional: enforce `exp` <= AllowedClockSkew window (already handled by token lifetime validation but could double-check short TTL policy).
  - Metrics counters for validation outcomes (success, replay, bad_sig, bad_issuer, etc.).

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
- Cookie validation hook implemented (`OnValidatePrincipal`) uses `IRevocationStore` to check revoked `sid`.
- Storage
  - Distributed: `DistributedRevocationStore` + `DistributedReplayCache` (via `IDistributedCache`) available and wired when cache configured.
  - In-memory fallbacks for dev: `MemoryRevocationStore`, `MemoryReplayCache`.
- Remaining:
  - Provide session index mapping for sub-only logout (currently no action if only `sub`).
  - Add background cleanup/metrics for revoked sid cardinality.

### 2.4 Reliability & safety
- Endpoint should return 200 OK upon successful processing (after validation & revocation).
- For invalid tokens:
  - Return 400/401; log why; rate-limit to mitigate abuse.
- Idempotency: repeated logout_token requests result in same final state; safe no-ops.
- Concurrency: atomic revocation; avoid races on multi-instance.

Status
- Returns 200 on success; 400 for malformed form; 401 for failed validation; 413 for oversized payload.
- Remaining: fine-grained reason response codes vs generic 401, rate limiting (e.g., token bucket on endpoint), and idempotent structured logging of attempts.

### 2.5 Security
- Accept only POST; require form-encoded; reject other content types.
- Validate signature and issuer strictly; do not rely on IPs.
- Enforce small body limit; short read timeout.
- Protect JWKS retrieval with caching; guard against cache poisoning.
- Observability and alerting:
  - Log reason, sid/sub, iss (anonymize/pseudonymize if needed).
  - Metrics for accepted vs. rejected, reason codes, per-issuer counts.

Status
- POST/form-only with size cap implemented; strict validation implemented (see 2.2).
- Remaining: stricter Content-Type check (currently relies on HasFormContentType), configurable max size, per-issuer allowlist, metric emission, anonymized logging.

### 2.6 Admin & ops
- Feature flags: enable/disable backchannel processing.
- Admin UI:
  - Inspect recent logout notifications (summary), last error.
  - View currently revoked sids (with TTL), purge entries.
- Configurable policy:
  - Skew window, jti cache TTL, sid TTL.
  - Allowed issuers; JWKS cache TTL; backoff for JWKS refresh.

Status
- RP processing flag: uses `BackchannelOptions.Enabled` already (settable via configuration) to short-circuit validation.
- No dedicated admin UI for revoked SIDs or stats yet.
- Remaining: add minimal admin/debug endpoint (e.g., /admin/backchannel/state) listing counts & recent reasons (guarded by admin policy).

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
- Draft docs pending — need to document RP validation, configuration knobs, and operator runbook.

---

## 4) Deliverables checklist

- [x] OP: Discovery metadata includes backchannel flags (DiscoveryHandler)
- [x] OP: Client model + migration for BackChannelLogoutUri + session required (AuthDbContext) — ensure migration applied in all envs
- [x] OP: Admin for backchannel fields
  - [x] UI/API + validation
  - [x] Audit logging
- [x] OP: logout_token builder (iss, aud, iat, jti, events, sid/sub)
- [x] OP: Fan-out dispatcher with retries, backoff, circuit breaker
- [ ] OP: Telemetry/outbox
  - [x] Durable outbox + admin + health
  - [x] Logs + metrics
  - [ ] Alerts integration (external)
    - [ ] Choose sink per env (App Insights or Prometheus) and wire exporter
    - [ ] Failure rate alert (Fail/Emitted > threshold over window)
    - [ ] Latency alert (P95 > threshold)
    - [ ] Backlog alert (outbox backlog > threshold)
    - [ ] Action routing (email/Teams/PagerDuty) configured
  
- [x] OP: Audit logging
  - [x] Admin changes to backchannel fields are audited with who/what/when/where
  - [x] Dispatcher audit events: enqueue, attempt, success, fail (status/reason), dead-letter, manual retry
  - [x] No raw JWTs logged; sid/sub redacted or hashed
  - [x] Dev fallback works (structured audit to app logger)
  - [ ] Central sink + retention configured (App Insights/ELK)
- [x] RP: /backchannel-logout endpoint
  - [x] Endpoint (POST form), revokes sid
  - [x] Strict validation (sig, iss, aud, events, iat, jti replay, typ)
- [x] RP: JWKS validation with caching and rollover handling (uses `IJwksCache` + rollover retry)
- [x] RP: Replay protection (jti cache) + sid revocation store (distributed + memory fallback)
- [x] RP: Cookie validation hook rejects revoked sids (distributed or in-memory)
- [ ] RP: Telemetry (logs, metrics, alerts)
  - [ ] Structured reason logs (success, replay, bad_sig, bad_issuer, etc.)
  - [ ] Metrics counters & histogram (validation latency)
- [ ] RP: Sub-only logout (session index for sub w/out sid)
- [ ] RP: Rate limiting / abuse protection on endpoint
- [ ] RP: Admin/debug endpoint for revoked SID count & recent failures
- [ ] Tests: unit (validator edge cases, replay), integration (OP->RP), chaos (timeouts, invalid JWKS), conformance checks
- [ ] Docs: operator runbook, client integration guide (includes RP validation & configuration)
- [x] Feature flags & safe rollout plan (emission flag, allow/block host lists)

---

## 5) Open questions / future enhancements

- Should OP authentication to RP backchannel be added (e.g., mTLS) in certain environments?
- Multi-tenant: per-tenant issuer isolation and limits.
- Long-lived sessions: automated periodic cleanup of stale revoked-sid entries.
- Admin UI for live fan-out monitoring and per-client pause/resume.

Next steps (near-term)
- OP: Wire external alerts (failure rate, P95 latency, backlog) to chosen sink; document runbook.
- OP: Forward audit events to central sink (if not already) with retention policy config.
- RP: Add structured telemetry (logs + metrics), rate limiting, and admin/debug endpoint.
- RP: Implement sub-only session mapping strategy (session index) for tokens missing `sid`.
- RP: Harden replay cache (atomic add using Redis SET NX if using StackExchange.Redis directly) — evaluate replacing IDistributedCache for that path.
- Docs: Author operator runbook & client integration guide; add troubleshooting section.
- Tests: Add comprehensive unit + integration + chaos tests for dispatcher & validator.

Configuration
- Audit:Enabled (bool, default true)
- Audit:Pepper (string; optional salt used for hashing sid/sub)
  - Alerting
    - Export dispatcher metrics to chosen sink (App Insights customMetrics or Prometheus)
    - Create FailureRate, LatencyP95, and OutboxBacklog alerts with agreed thresholds
    - Add environment-specific configuration flags and thresholds in appsettings
    - Validate alerts fire in a dry-run/test environment and document runbook
- Tests: add unit tests for token builder and dispatcher retry logic; integration test OP->RP flow.