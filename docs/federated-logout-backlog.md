# Federated Logout (Local vs Upstream) Backlog

Updated: 2025-09-26
Status legend
- [x] Done
- [~] Pending / In progress
- [ ] Not started

Goal
Enable end-user choice (and optional automation) to:
1. Sign out only of this Authorization Server / application (local session + tokens).
2. Sign out locally AND initiate logout at the upstream external Identity Provider (when the session originated from an external IdP that supports RP-initiated logout).

Keep backwards compatibility when: (a) no external IdP used for the session, (b) upstream does not advertise an end_session_endpoint, (c) feature flag disabled.

Non-goals (Phase 1)
- IdP-initiated (front-channel or back-channel) logout consumption.
- SLO propagation to multiple chained upstream IdPs (only one upstream provider per authenticated session currently).
- Coordinated revocation of issued access/refresh tokens (local logout semantics unchanged).

Assumptions / Principles
- A single upstream OIDC provider is involved per authenticated cookie session.
- The local principal already carries `idp` (provider machine name) and optionally upstream `acr`.
- We will not persist the raw upstream ID token long-term; if needed for `id_token_hint` it is either (a) encrypted in the auth cookie properties or (b) stored short-lived in a server cache keyed by a logout context.
- Security first: no open redirect via `post_logout_redirect_uri`; server decides allowed redirect target.

Feature Flag (optional)
`Auth:Features:EnableFederatedLogout` (default true when at least one external provider exists).

## Epics & Stories

### 1) Session Tagging & Capability Detection
- [x] Story: Capture upstream logout capability
  - IMPLEMENTED VARIANT: We do not persist the upstream end_session_endpoint at sign-in; instead we store provider name (`idp` claim) plus encrypted upstream id token (`UpstreamIdTokenEnc`) and `UpstreamSid` (if present) in auth properties. Capability re-evaluated lazily at logout via `CanFederateAsync` which consults provider config + on-demand discovery.
  - Raw upstream ID token never stored unprotected (encrypted with Data Protection API).
  - Acceptance (met): `/logout` GET determines if federated option should be shown by calling service.
  - TODO (optional hardening): Cache a positive capability flag in auth properties to avoid DB/discovery in prompt phase.

- [x] Story: Optional encryption helper
  - Encrypted upstream id token stored as `UpstreamIdTokenEnc` (data protector purpose `federated-logout-idtoken`).
  - TTL currently tied to auth cookie lifetime; optional short-lived cache not yet needed.
  - Acceptance (met): Value unreadable without protector; decrypted only at redirect build; never logged.

### 2) Logout UX (Razor Page / Handler)
- [x] Story: Present federated option
  - Minimal HTML choice page (not yet a Razor Page) displays when capability true; falls back silently to local logout otherwise.
  - A11y basic labels present; further polish deferred.
  - Tests: service logic covered; handler choice path still needs explicit tests (see Epics 6).

- [x] Story: Federated selection handling
  - POST processes `mode=local|federated`; defaults to local if missing/unknown.
  - Federated path clears local session BEFORE redirect.
  - Redirect includes `post_logout_redirect_uri`, `state`, optional `id_token_hint`, optional `sid`.
  - Fallback to local on discovery/build failure audited & metered.

- [x] Story: Federated callback finalize
  - Callback validates single-use protected state; success & failure paths audited.
  - On invalid state we render generic local-success page (HTTP 200) rather than 400 to avoid UX confusion; metric + audit capture failure.
  - Acceptance adjusted: friendly page + metrics/audit rather than status code.

### 3) Upstream Logout Orchestration Service
- [x] Story: Introduce `IUpstreamLogoutService`
  - Implemented as `UpstreamLogoutService` with methods: `CanFederateAsync`, `BuildFederatedRedirectAsync`, `ValidateCallbackAsync`.
  - Discovery retrieval + heuristic fallback (`/v2/logout` then `/logout`) implemented with short cache.
  - Unit tests cover: published endpoint, heuristic fallback, discovery HTTP error, state validation & replay.
  - Follow-up: add test asserting discovery parse failure path & audit emission.

### 4) Security & Resilience
- [x] Story: State & CSRF protection
  - Protected JSON payload (`s`, `ts`, `ret`) using Data Protection; cached with TTL (default 5 min); single-use removal verified by test.

- [x] Story: Output encoding & redirect safety
  - Return URL sanitized (relative only). Query parameters server-composed. HTML output encoded.

- [~] Story: Logging & PII discipline
  - Structured audit events implemented (see Audit Events section) using hashed SID/subject where relevant.
  - Raw tokens & full URLs not logged (presence flags only). Need automated test to assert absence => remaining task.

### 5) Telemetry / Metrics
- [x] Story: Metrics counters & duration
  - Implemented counters: LogoutRequests, LogoutFederated, LogoutLocal, LogoutFailures; histogram LogoutDuration (ms) with mode tag.
  - TODO: add discovery outcome counters & gauge for active federated states (optional).

### 6) Tests & Quality Gates
- [~] Story: Unit tests (service + handler)
  - Service scenarios covered (published endpoint, fallback, discovery fail, state replay). Handler scenarios (prompt rendering, choice branches, redirect failure audit) still missing.
  - Remaining: tests for id_token_hint inclusion flag & audit absence of raw token.

- [ ] Story: Integration tests
  - Not started. Will add full round-trip with TestServer.

- [~] Story: Negative tests
  - Discovery failure & state replay covered at service layer; handler fallback & invalid callback page rendered (needs assertion). Expired state TTL boundary not simulated yet.

### 7) Documentation
- [ ] Story: Admin / Operator guide section
- [ ] Story: Developer guide additions
- [ ] Story: Backlog cross-link
  - None started yet.

### 8) Optional Future (Not Phase 1)
- [ ] Story: Auto-federated mode
  - Provider config: `"LogoutBehavior":"AutoFederated"` triggers automatic upstream redirect (skip choice) unless `prompt=local_logout` param present.
- [ ] Story: Policy control per client
  - Allow / deny offering federated logout depending on client security posture.
- [ ] Story: Front-channel IdP-initiated logout support
  - Endpoint receiving provider iframe request using `sid` + `iss`.
- [ ] Story: Back-channel logout from upstream
  - Accept `logout_token` per OIDC Back-Channel Logout; map to session(s) by `sid` or `sub`; clear cookies.
- [ ] Story: Session index persistence
  - Table `ExternalSessions` (Id, Idp, SidHash, LocalSessionIdHash, CreatedAt, ExpiresAt).

### 9) Configuration & Extensibility
- [ ] Story: Provider config schema extension
  - Not implemented. Currently inference happens dynamically; no explicit flags.

## Data & Persistence Impact
Phase 1: No DB migration required (using auth cookie properties + ephemeral cache). Optional Phase 2 adds `ExternalSessions` table for IdP-initiated logout.

## Security Considerations
Threats & mitigations
- CSRF / state replay -> random, single-use, short TTL state.
- Token leakage in logs -> never log `id_token_hint`, only presence flag.
- Open redirect -> server determines `post_logout_redirect_uri`, no user param.
- DoS via repeated callback -> cheap state lookup + early reject.

## Observability
Implemented Audit Event Names (current instrumentation)
| Event | Description |
|-------|-------------|
| logout.federated.prompt | Federated choice page rendered |
| logout.federated.prompt.skip_disabled | Feature disabled -> skipped |
| logout.federated.prompt.skip_no_capability | No upstream capability -> fall back local |
| logout.federated.choice.local | User selected local-only |
| logout.federated.choice.federated | User selected federated |
| logout.federated.choice.federated.capability_missing | Race: user chose federated but capability not present at POST |
| logout.federated.redirect.fail | Redirect build failed (reason) |
| logout.federated.discovery.ok | Discovery succeeded |
| logout.federated.discovery.fail | Discovery HTTP failure |
| logout.federated.discovery.exception | Exception during discovery request |
| logout.federated.discovery.parsefail | JSON parse failure |
| logout.federated.discovery.heuristic | Heuristic endpoint guess used |
| logout.federated.redirect | Successful redirect assembled (flags: has_id_token_hint, has_sid) |
| logout.federated.callback.ok | Callback state valid (service-level) |
| logout.federated.callback.fail | Callback state invalid (service-level) reason detail |
| logout.federated.callback.page.ok | Final user page after valid federated logout |
| logout.federated.callback.page.fail | Final page when callback invalid (local-only outcome) |

PII Handling
- SID and subject hashed where present in other existing BCL audits; federated events avoid raw values entirely.
- No raw tokens, state, or full upstream URLs emitted (only provider name & presence flags).

Planned Additions
- discovery counters (success/fail/heuristic) metrics
- automated test asserting absence of `id_token_hint` substring in audit/log output.

## Test Matrix (Revised Current vs Planned)
| Scenario | Current Behavior | Status |
|----------|------------------|--------|
| Local-only logout (no idp claim) | Immediate local sign-out (prompt skipped) | Covered implicitly |
| External session; upstream supports endpoint | Choice page rendered | Needs handler test |
| Federated chosen with id_token_hint | Redirect includes id_token_hint + sid (if provided) | Service test covers hint inclusion |
| Federated chosen without id_token_hint (sid present) | Redirect includes sid only | Service test covers |
| Federated chosen but discovery HTTP fails | Fallback to local-only; audit + metric failure | Covered (service) |
| Discovery parse failure | Fallback to failure -> local-only | Pending test |
| Heuristic endpoint used | Redirect to /v2/logout | Covered (service) |
| Callback valid state | Final page success (200) | Service test validates state; handler page needs test |
| Callback invalid state | Generic local-complete page (200) + audit fail | Service test (invalid) |
| Callback replayed state | Invalid (audit fail) | Tested |
| Expired state | Invalid (needs TTL manipulation test) | Not tested |
| POST federated but capability missing | Local fallback + audit capability_missing | Needs handler test |
| Unknown mode value | Treat as local-only | Needs handler test |

## Metrics (Initial Definition / Implemented)
- Implemented: logout.requests, logout.federated, logout.local, logout.failures (with reason tag), logout.duration.ms.
- Pending: discovery outcome counters, per-provider bucketization (currently omitted for cardinality control).

## Rollout Plan
1. Implement service + tagging (dark) behind feature flag.
2. Add UI choice (flagged) + unit tests.
3. Enable in dev; verify metrics/logs.
4. Add integration tests.
5. Documentation updates.
6. Enable in staging → production.
7. Optional: Evaluate need for session index + IdP-initiated support.

## Risks / Open Questions
- Some providers require POST (form) for logout; initial scope assumes GET. Mitigation: detect via metadata override and render auto-submitting form.
- `sid` usage varies; fallback to `id_token_hint` may be mandatory (Auth0 / Azure AD scenarios differ). Need provider-specific notes.
- User confusion: Choosing local-only but expecting full global sign-out. Solution: tooltip / explanatory text.

## Deferred Items
- Back-channel / front-channel consumption.
- Multi-upstream-providers per session.
- Token revocation coordination across microservices.

## Acceptance (Phase 1) (Updated Progress)
- [x] User with external session sees choice and both flows work (manual & service unit coverage; handler test pending).
- [x] Local-only flow unchanged for non-external sessions.
- [~] Logs & metrics present without sensitive data (instrumented; add automated PII absence test).
- [~] All new tests pass (service tests done; handler & integration tests outstanding).
- [x] No DB migration required.

---
Owner: TBD
Initial PRs:
1. Service + handler wiring
2. UI update + tests
3. Metrics/logging instrumentation
4. Docs update

End of document.
