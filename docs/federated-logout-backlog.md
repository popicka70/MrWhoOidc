# Federated Logout (Local vs Upstream) Backlog

Updated: 2025-01-09
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
  - **COMPLETE**: Razor Page at `/Logout/Prompt/Index.cshtml` implemented with professional UI showing two clear options (local-only vs federated).
  - A11y: labels, button text, and explanatory alert present; dark mode supported via style parameter.
  - Handler logic in `FederatedLogoutEntryHandler` redirects to prompt when capability detected; falls back silently to local logout otherwise.
  - Tests: service logic covered; handler integration test exists (delegation verified in `LogoutHandlerTests`).

- [x] Story: Federated selection handling
  - **COMPLETE**: POST handler in `Index.cshtml.cs` processes `mode=local|federated`; defaults to local if missing/unknown.
  - Federated path implemented in `FederatedLogoutChoiceHandler` which clears local session BEFORE redirect.
  - Redirect includes `post_logout_redirect_uri`, `state`, optional `id_token_hint`, optional `sid`.
  - Fallback to local on discovery/build failure audited & metered via `logout.federated.redirect.fail` event.

- [x] Story: Federated callback finalize
  - **COMPLETE**: `FederatedCallbackHandler` validates single-use protected state; success & failure paths audited.
  - On invalid state renders generic local-success page (`FederatedCallbackError.cshtml`) with HTTP 200 rather than 400 to avoid UX confusion; metric + audit capture failure.
  - Acceptance met: friendly page + metrics/audit rather than status code; test coverage via service layer state validation tests.

### 3) Upstream Logout Orchestration Service
- [x] Story: Introduce `IUpstreamLogoutService`
  - Implemented as `UpstreamLogoutService` with methods: `CanFederateAsync`, `BuildFederatedRedirectAsync`, `ValidateCallbackAsync`.
  - Discovery retrieval + heuristic fallback (`/v2/logout` then `/logout`) implemented with short cache.
  - Unit tests cover: published endpoint, heuristic fallback, discovery HTTP error, state validation & replay.
  - Follow-up: add test asserting discovery parse failure path & audit emission.

### 4) Security & Resilience
- [x] Story: State & CSRF protection
  - **COMPLETE**: Protected JSON payload (`s`, `ts`, `ret`, optional `refId`) using Data Protection API; cached with TTL (default 5 min); single-use removal verified by `FederatedLogoutServiceTests.ValidateCallback_RejectsInvalidState`.

- [x] Story: Output encoding & redirect safety
  - **COMPLETE**: Return URL sanitized (relative only via `UrlComparison.IsAllowed`). Query parameters server-composed with proper escaping. HTML output encoded in Razor pages.

- [x] Story: Logging & PII discipline
  - **COMPLETE**: Structured audit events implemented using `IAuditSink` with hashed SID/subject where relevant (see Audit Events section).
  - Raw tokens & full URLs not logged (presence flags only: `has_id_token_hint`, `has_sid`).
  - **TODO**: Add automated test to assert absence of raw `id_token_hint` substring in audit/log output (test exists for BCL; pattern should be replicated).

### 5) Telemetry / Metrics
- [x] Story: Metrics counters & duration
  - **COMPLETE**: Implemented counters via `OidcMetrics`: `LogoutRequests`, `LogoutFederated`, `LogoutLocal`, `LogoutFailures`; histogram `LogoutDuration` (ms) with mode tag.
  - Metrics emitted at key decision points: prompt, local choice, federated choice, redirect success/failure, callback validation.
  - **TODO**: Add discovery outcome counters (`discovery.success`, `discovery.fail`, `discovery.heuristic`) and optional gauge for active federated states (cache size metric).

### 6) Tests & Quality Gates
- [x] Story: Unit tests (service + handler)
  - **COMPLETE**: `FederatedLogoutServiceTests.cs` covers 6 scenarios: disabled feature, existing provider capability, published endpoint usage, heuristic fallback, discovery HTTP error, state validation & replay.
  - **COMPLETE**: `LogoutHandlerTests.cs` verifies delegation to `FederatedLogoutEntryHandler` and `FederatedCallbackHandler`.
  - **TODO**: Add explicit handler tests for:
    - Prompt page rendering with federated capability (currently implicit via delegation test).
    - POST choice branches (local vs federated mode selection).
    - Redirect failure fallback & audit emission in handler context.
    - Discovery parse failure path with audit event assertion.
    - `id_token_hint` inclusion flag validation in redirect URL.

- [ ] Story: Integration tests
  - **NOT STARTED**: Full round-trip with TestServer (E2E flow from `/logout` → prompt → POST federated → upstream redirect → callback validation).
  - Recommended: Add to `MrWhoOidc.UnitTests` or new integration test project using `WebApplicationFactory`.
  - See `test-coverage-backlog.md` Story 4.5 for E2E federated logout scenarios.

- [~] Story: Negative tests
  - **PARTIAL**: Discovery failure & state replay covered at service layer; handler fallback & invalid callback page rendered (needs explicit assertion in handler tests).
  - **TODO**: Expired state TTL boundary test (requires time manipulation or TTL override in test harness).

### 7) Documentation
- [~] Story: Admin / Operator guide section
  - **PARTIAL**: Feature behavior and architecture documented in `logout-handler-architecture.md` and `logout-handler-refactoring.md`.
  - **TODO**: Create dedicated admin guide section covering:
    - Feature flag configuration (`Auth:Features:EnableFederatedLogout`).
    - Provider-specific notes (Auth0, Azure AD, Google logout endpoint variations).
    - Metrics/audit event catalog for monitoring.
    - Common troubleshooting scenarios (discovery failure, state expiry, upstream redirect issues).
    
- [ ] Story: Developer guide additions
  - **NOT STARTED**: Add section to `developer-guide.md` covering:
    - Federated logout flow architecture.
    - How to extend for new providers.
    - State management and protector patterns.
    - Testing patterns for federated scenarios.
    
- [x] Story: Backlog cross-link
  - **COMPLETE**: Cross-linked to `backchannel-logout-backlog.md`, `logout-handler-architecture.md`, `logout-handler-refactoring.md`, and `test-coverage-backlog.md` Story 4.5.

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
| Local-only logout (no idp claim) | Immediate local sign-out (prompt skipped) | ✅ Covered (implicit via delegation) |
| External session; upstream supports endpoint | Choice page rendered | ✅ Covered (service + handler delegation) |
| Federated chosen with id_token_hint | Redirect includes id_token_hint + sid (if provided) | ✅ Service test covers |
| Federated chosen without id_token_hint (sid present) | Redirect includes sid only | ✅ Service test covers |
| Federated chosen but discovery HTTP fails | Fallback to local-only; audit + metric failure | ✅ Covered (service) |
| Discovery parse failure | Fallback to failure → local-only | ⏳ Pending handler test |
| Heuristic endpoint used | Redirect to /v2/logout | ✅ Covered (service) |
| Callback valid state | Final page success (200) | ✅ Service validates state; handler renders page |
| Callback invalid state | Generic local-complete page (200) + audit fail | ✅ Service test validates; handler renders |
| Callback replayed state | Invalid (audit fail) | ✅ Tested |
| Expired state | Invalid (needs TTL manipulation test) | ⏳ Not tested (requires time mock) |
| POST federated but capability missing | Local fallback + audit capability_missing | ⏳ Needs handler test |
| Unknown mode value | Treat as local-only | ⏳ Needs handler test |
| E2E round-trip (TestServer) | Full flow from entry to callback | ❌ Not started |
| Provider-specific POST form requirement | Auto-submit form (not GET redirect) | ❌ Not implemented (Phase 2) |

## Metrics (Initial Definition / Implemented)
- Implemented: logout.requests, logout.federated, logout.local, logout.failures (with reason tag), logout.duration.ms.
- Pending: discovery outcome counters, per-provider bucketization (currently omitted for cardinality control).

## Rollout Plan
1. ✅ Implement service + tagging (dark) behind feature flag.
2. ✅ Add UI choice (flagged) + unit tests.
3. ✅ Enable in dev; verify metrics/logs.
4. 🔄 **IN PROGRESS**: Add integration tests (E2E TestServer scenarios).
5. 🔄 **IN PROGRESS**: Documentation updates (admin/operator guide sections).
6. ⏳ **PENDING**: Enable in staging → production (awaiting integration tests + docs completion).
7. ⏳ **PENDING**: Evaluate need for session index + IdP-initiated support (Phase 2 feature).

## Risks / Open Questions
- Some providers require POST (form) for logout; initial scope assumes GET. Mitigation: detect via metadata override and render auto-submitting form.
- `sid` usage varies; fallback to `id_token_hint` may be mandatory (Auth0 / Azure AD scenarios differ). Need provider-specific notes.
- User confusion: Choosing local-only but expecting full global sign-out. Solution: tooltip / explanatory text.

## Deferred Items
- Back-channel / front-channel consumption.
- Multi-upstream-providers per session.
- Token revocation coordination across microservices.

## Acceptance (Phase 1) (Updated Progress)
- [x] User with external session sees choice and both flows work (service unit coverage + handler delegation tests complete; UI manually verified).
- [x] Local-only flow unchanged for non-external sessions (implicit coverage via delegation tests).
- [x] Logs & metrics present without sensitive data (instrumented; automated PII absence test remains TODO).
- [x] Prompt page is accessible and mobile-friendly (Razor page implemented with Bootstrap classes, dark mode support).
- [~] Integration tests validate end-to-end flow (not started; required for production rollout).
- [~] Documentation complete for admins and developers (architecture docs complete; admin/dev guide sections pending).

---

## 📋 Proposed Next Steps (Priority Order)

Based on the code assessment, here are the recommended next actions to complete Phase 1 and prepare for production rollout:

### 🔴 HIGH PRIORITY (Required for Production)

#### 1. Integration Tests (Story 6 - Integration tests)
**Why:** Critical for validating end-to-end flow before production deployment.

**Tasks:**
- [ ] Add `FederatedLogoutIntegrationTests.cs` to `MrWhoOidc.UnitTests`
- [ ] Use `WebApplicationFactory<Program>` to create TestServer
- [ ] Test E2E flows:
  - GET `/logout` → prompt page → POST `mode=federated` → redirect to upstream → callback validation
  - State lifecycle (creation → use → expiry)
  - Discovery failure fallback
  - Invalid callback handling
- [ ] Mock upstream IdP responses using `TestHandler`
- [ ] Verify metrics and audit events emitted correctly

**Estimated effort:** 4-6 hours  
**Priority:** Critical - blocks production rollout

#### 2. Admin/Operator Documentation (Story 7)
**Why:** Operators need configuration and troubleshooting guidance.

**Tasks:**
- [ ] Create `docs/federated-logout-admin-guide.md` covering:
  - Feature flag: `Auth:Features:EnableFederatedLogout` (default: true)
  - Provider configuration requirements (Authority, ClientId, end_session_endpoint)
  - State TTL configuration: `FederatedLogoutOptions.StateTtlSeconds` (default: 300)
  - Monitoring: audit events and metrics catalog
  - Common issues: discovery failures, state expiry, HTTPS enforcement
  - Provider-specific notes (Auth0 `/v2/logout`, Azure AD variations, Google)
- [ ] Add section to `developer-guide.md`:
  - Architecture overview (service, handlers, state management)
  - Extension points (custom discovery logic, provider-specific adapters)
  - Testing patterns

**Estimated effort:** 3-4 hours  
**Priority:** High - required for operational readiness

#### 3. Handler-Level Tests (Story 6 - Unit tests)
**Why:** Improve coverage of handler logic and edge cases.

**Tasks:**
- [ ] Add `FederatedLogoutHandlerTests.cs` or extend `LogoutHandlerTests.cs`:
  - Test prompt page rendering with capability detection
  - Test POST choice handling (local vs federated mode)
  - Test redirect failure fallback with audit emission
  - Test discovery parse failure path
  - Test `id_token_hint` inclusion flag validation
  - Test unknown mode value fallback to local-only
  - Test capability race condition (federated chosen but capability lost)
- [ ] Add TTL expiry test (requires time provider injection or TTL override)

**Estimated effort:** 3-4 hours  
**Priority:** High - improves confidence in handler logic

### 🟡 MEDIUM PRIORITY (Quality & Observability)

#### 4. Discovery Metrics Enhancement (Story 5)
**Why:** Improve observability of upstream IdP interactions.

**Tasks:**
- [ ] Add `OidcMetrics` counters:
  - `DiscoverySuccess` (with provider tag)
  - `DiscoveryFailed` (with provider + reason tags)
  - `DiscoveryHeuristicUsed` (with provider tag)
- [ ] Add gauge for active federated states (cache size monitoring)
- [ ] Emit metrics in `UpstreamLogoutService.DiscoverEndSessionAsync`
- [ ] Document new metrics in admin guide

**Estimated effort:** 2-3 hours  
**Priority:** Medium - enhances operational monitoring

#### 5. PII Logging Automated Test (Story 4)
**Why:** Prevent accidental logging of sensitive tokens.

**Tasks:**
- [ ] Add test `FederatedLogoutServiceTests.Audit_Does_Not_Log_Raw_IdTokenHint`
- [ ] Use in-memory logger or audit sink capture
- [ ] Assert absence of substrings: `id_token_hint=`, `Bearer`, JWT header patterns
- [ ] Similar pattern to BCL token logging tests

**Estimated effort:** 1-2 hours  
**Priority:** Medium - security hygiene

#### 6. External PostLogoutRedirectUri Support Validation (Story 3)
**Why:** Ensure client-specific redirect validation works correctly.

**Tasks:**
- [ ] Add test validating `LogoutRedirectReference` entity creation
- [ ] Test `UrlComparison.IsAllowed` logic with client `AllowedLogoutRedirectUrisJson`
- [ ] Test failure case when external redirect not in allowed list
- [ ] Verify audit event emission

**Estimated effort:** 2 hours  
**Priority:** Medium - validates existing security feature

### 🟢 LOW PRIORITY (Phase 2 / Future Enhancements)

#### 7. POST Form Support for Providers (Story 8 - Optional Future)
**Why:** Some providers (e.g., certain OAuth2 implementations) require POST instead of GET redirect.

**Tasks:**
- [ ] Add provider config flag: `"LogoutMethod": "POST"`
- [ ] Modify `BuildFederatedRedirectAsync` to return HTML form page instead of redirect
- [ ] Implement auto-submitting form Razor page
- [ ] Add tests for POST flow

**Estimated effort:** 4-6 hours  
**Priority:** Low - defer unless specific provider requirement identified

#### 8. Auto-Federated Mode (Story 8 - Optional Future)
**Why:** Allow automatic upstream logout without user prompt for specific providers.

**Tasks:**
- [ ] Add provider config: `"LogoutBehavior": "AutoFederated"`
- [ ] Skip prompt page when configured
- [ ] Support `prompt=local_logout` query param to override
- [ ] Add tests

**Estimated effort:** 3-4 hours  
**Priority:** Low - UX enhancement for advanced scenarios

#### 9. IdP-Initiated Logout Support (Story 8 - Phase 2)
**Why:** Handle logout initiated by upstream IdP (front-channel or back-channel).

**Tasks:**
- [ ] Implement endpoint to receive upstream logout notifications
- [ ] Map `sid` or `sub` to local sessions
- [ ] Coordinate with existing back-channel logout (`BackchannelLogoutDispatcher`)
- [ ] Implement `ExternalSessions` table persistence
- [ ] Add comprehensive tests

**Estimated effort:** 12-16 hours  
**Priority:** Low - Phase 2 feature; requires session index design

---

## 🎯 Recommended Sprint Plan

### Sprint 1 (Week 1): Critical Production Readiness
- Integration tests (Task 1) - Days 1-3
- Admin documentation (Task 2) - Days 4-5

### Sprint 2 (Week 2): Quality & Observability
- Handler-level tests (Task 3) - Days 1-2
- Discovery metrics (Task 4) - Day 3
- PII logging test (Task 5) - Day 4
- External redirect validation test (Task 6) - Day 5

### Post-Rollout: Phase 2 Enhancements
- Defer POST form support, auto-federated mode, and IdP-initiated logout until production feedback received

---

## Summary of Current State

**✅ Phase 1 Core Implementation: COMPLETE**
- Service layer fully implemented with discovery, redirect building, and callback validation
- Razor UI with professional prompt page and result pages
- Security: state protection, single-use tokens, CSRF mitigation
- Observability: comprehensive audit events and metrics
- Unit test coverage for service logic and handler delegation

**🔄 Phase 1 Remaining Work: IN PROGRESS**
- Integration tests (E2E validation)
- Documentation (admin/operator guide)
- Handler-level edge case tests

**⏳ Phase 1 Blockers for Production:**
1. Integration tests must pass
2. Admin documentation must be complete
3. Handler edge case tests recommended (non-blocking but high value)

**Confidence Level:** 85% - Core implementation solid, needs validation layer and operational docs to reach 100% production readiness.
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
