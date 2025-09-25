# Back-Channel Logout (BCL) — Production Backlog

This document captures production-level requirements, hardening items, and test coverage for implementing and running OpenID Connect Back-Channel Logout across the OP (server) and RPs (clients), including our Blazor app.

References
- OpenID Connect Back-Channel Logout 1.0: https://openid.net/specs/openid-connect-backchannel-1_0.html
- OpenID Connect RP-Initiated Logout 1.0: https://openid.net/specs/openid-connect-rpinitiated-1_0.html
- OpenID Connect Session Management (sid claim): https://openid.net/specs/openid-connect-session-1_0.html

---

## 1) OP (Authorization Server) requirements

### 1.1 Discovery and registration
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
- RP-initiated logout (/connect/endsession) — already triggers.
- Admin-forced user logout (global sign-out):
  - For a user (sub), notify all RPs that have sessions for the user (if known), else all clients.
- Session revocation (sid invalidation) — proactive BCL push.
- Key rotation-induced global logouts (optional policy).

### 1.5 Reliability & durability (optional but recommended for prod)
- Durable outbox for logout notifications:
  - Persist pending delivery with status; background worker dispatches.
  - Retain last N attempts, status; dead-letter queue after max retries.
- Admin pulse:
  - Inspect pending/failed notifications; manual retry.
- Health endpoint:
  - Report backlog size; alert on thresholds.

### 1.6 Security & compliance
- Only issue standard claims; avoid extra PII.
- Ensure TLS; consider mTLS between OP and RPs in sensitive environments.
- Strong key management for signing (rotation policies; track kid).
- Audit logs for who/what/when of logout events.
- Rate-limit BCL emissions per RP; per-tenant throttles.
- Respect privacy laws (e.g., keep only necessary logs, retention schedules).

### 1.7 Observability & ops
- Structured logs for each POST with correlation id.
- Metrics: emitted_count, success_count, fail_count, retry_count, latency, per-client breakdown.
- Alerts:
  - Failure rate > X% over Y minutes
  - Latency > threshold
  - Outbox backlog > threshold

### 1.8 Testing
- Unit: token creation, claims, signing; fan-out selection; retry decision logic.
- Integration: OP emits logout_token to a test RP endpoint; verify claims and signature.
- Chaos: inject timeouts/5xx; ensure retries and no OP outage.
- Conformance: verify claims against the spec; negative tests (missing events/sid/sub).

---

## 2) RP (Blazor app) requirements

### 2.1 Endpoint
- Implement POST /backchannel-logout:
  - Content-Type: application/x-www-form-urlencoded; require logout_token param.
  - No cookies/session required; ensure CSRF not applicable.
  - Body size limits; reject excessively large payloads.

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

### 2.4 Reliability & safety
- Endpoint should return 200 OK upon successful processing (after validation & revocation).
- For invalid tokens:
  - Return 400/401; log why; rate-limit to mitigate abuse.
- Idempotency: repeated logout_token requests result in same final state; safe no-ops.
- Concurrency: atomic revocation; avoid races on multi-instance.

### 2.5 Security
- Accept only POST; require form-encoded; reject other content types.
- Validate signature and issuer strictly; do not rely on IPs.
- Enforce small body limit; short read timeout.
- Protect JWKS retrieval with caching; guard against cache poisoning.
- Observability and alerting:
  - Log reason, sid/sub, iss (anonymize/pseudonymize if needed).
  - Metrics for accepted vs. rejected, reason codes, per-issuer counts.

### 2.6 Admin & ops
- Feature flags: enable/disable backchannel processing.
- Admin UI:
  - Inspect recent logout notifications (summary), last error.
  - View currently revoked sids (with TTL), purge entries.
- Configurable policy:
  - Skew window, jti cache TTL, sid TTL.
  - Allowed issuers; JWKS cache TTL; backoff for JWKS refresh.

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

### 3.2 Performance & scale
- Fan-out bounded concurrency configuration.
- RP endpoint optimized: minimal allocations, fast signature check via cached validation parameters.
- Consider batching global logout triggers (admin-induced) to avoid thundering herds.

### 3.3 Security reviews
- Threat modeling: replay, token forgery, endpoint abuse, SSRF risks.
- Pen-test scenarios specific to BCL endpoints and token validation.

### 3.4 Documentation & runbooks
- Operator runbook for mass logout.
- Troubleshooting guide: reading metrics, locating failures, replays, JWKS issues.
- Client integration guide: how to configure BackChannelLogoutUri and verify behavior.

---

## 4) Deliverables checklist

- [ ] OP: Discovery metadata includes backchannel flags
- [ ] OP: Client model + migration for BackChannelLogoutUri + session required
- [ ] OP: Admin UI/API for backchannel fields with validation and audit
- [ ] OP: logout_token builder (iss, aud, iat, jti, events, sid/sub)
- [ ] OP: Fan-out dispatcher with retries, backoff, circuit breaker
- [ ] OP: Telemetry (logs, metrics, alerts), outbox (optional but recommended)
- [ ] RP: /backchannel-logout endpoint (POST, form), strict validation
- [ ] RP: JWKS validation with caching and rollover handling
- [ ] RP: Replay protection (jti cache) + sid revocation store (distributed)
- [ ] RP: Cookie validation hook rejects revoked sids
- [ ] RP: Telemetry (logs, metrics, alerts)
- [ ] Tests: unit + integration + chaos; conformance checks
- [ ] Docs: operator runbook, client integration guide
- [ ] Feature flags & safe rollout plan

---

## 5) Open questions / future enhancements

- Should OP authentication to RP backchannel be added (e.g., mTLS) in certain environments?
- Multi-tenant: per-tenant issuer isolation and limits.
- Long-lived sessions: automated periodic cleanup of stale revoked-sid entries.
- Admin UI for live fan-out monitoring and per-client pause/resume.