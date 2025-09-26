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
- [ ] Story: Capture upstream logout capability
  - On successful external OIDC callback:
    - Inspect discovery for `end_session_endpoint`.
    - If present, record capability in `AuthenticationProperties.Items["UpstreamEndSessionEndpoint"]`.
    - Persist minimal metadata: provider `idp`, optional `sid` claim (`idp_sid`), timestamp.
  - Do not store raw ID token unprotected.
  - Acceptance: Subsequent `/logout` GET can determine if federated option is available.

- [ ] Story: Optional encryption helper
  - If `id_token_hint` needed by upstream, encrypt using data protection API and store as `UpstreamIdTokenEnc` in auth properties OR ephemeral cache keyed by a `logout_ctx` (GUID) stored in cookie.
  - Acceptance: Value cannot be read or replayed after expiration (<= 15 min).

### 2) Logout UX (Razor Page / Handler)
- [ ] Story: Present federated option
  - GET `/logout` inspects principal.
  - If `idp` present AND upstream endpoint stored: show two choices (local-only, federated) with provider display name.
  - If not: show existing single-option logout UI.
  - Acceptance: Conditional UI path covered by tests; a11y basics (labels, focus) preserved.

- [ ] Story: Federated selection handling
  - POST `/logout` includes selection (`federated=true|false`).
  - Local-only: perform existing sign-out (cookie clear + tokens revocation logic unchanged) → show logged-out view.
  - Federated: build redirect to upstream `end_session_endpoint` including:
    - Fresh `state` (random, 256-bit) stored server-side or signed cookie.
    - `post_logout_redirect_uri` = server-controlled absolute URL to `/logout/federated-callback`.
    - Optional `id_token_hint` if available; else rely on `sid` if upstream supports; else omit.
  - Acceptance: Redirect verified; local session cleared prior to external navigation.

- [ ] Story: Federated callback finalize
  - Endpoint `/logout/federated-callback` validates `state` then renders final page.
  - Always idempotent: if state invalid or missing show safe generic message + new correlation id.
  - Acceptance: Invalid / replayed state returns 400 (or friendly error) without exceptions; logs capture event.

### 3) Upstream Logout Orchestration Service
- [ ] Story: Introduce `IUpstreamLogoutService`
  - Methods:
    - `CanFederate(ClaimsPrincipal p)` -> capability + provider info.
    - `BuildFederatedRedirectAsync(ClaimsPrincipal p, FederatedLogoutRequest r)` -> URL + correlation id.
    - `ValidateCallbackAsync(string state)` -> result (valid/invalid/expired).
  - Encapsulates token decrypt, state issuance, secure logging.
  - Acceptance: Unit tests cover happy path, missing upstream endpoint, expired state.

### 4) Security & Resilience
- [ ] Story: State & CSRF protection
  - `state` stored in ephemeral server cache (memory or distributed when configured) with TTL 5 min.
  - Single use: consumption removes entry.
  - Acceptance: Replay attempt fails gracefully.

- [ ] Story: Output encoding & redirect safety
  - No user-controlled input influences upstream logout URL except allowed values inserted server-side.
  - Acceptance: Security review checklist passes (no open redirect, no token leakage in referrer).

- [ ] Story: Logging & PII discipline
  - Structured events: `logout.initiated`, `logout.upstream.redirect`, `logout.upstream.callback` with fields: `federated`, `idp`, `correlation_id`, `has_id_token_hint` (bool), `outcome`.
  - Do NOT log raw tokens, state values, or full URLs (strip query except for presence flags).
  - Acceptance: Sample logs reviewed; automated test asserts absence of token substrings.

### 5) Telemetry / Metrics
- [ ] Story: Metrics counters & duration
  - Meter: `MrWhoOidc.WebAuth`
  - Counters: `oidc.logout.requests`, `oidc.logout.federated`, `oidc.logout.local`, `oidc.logout.failures`.
  - Histogram: `oidc.logout.duration.ms` tagged with `mode=local|federated` and `idp_bucket` (hashed or bucketized provider id).
  - Acceptance: Metrics emitted in test harness (can assert via in-memory exporter).

### 6) Tests & Quality Gates
- [ ] Story: Unit tests (service + handler)
  - Cases: local-only path, federated path with id_token_hint, without hint (sid only), missing endpoint, replayed state, invalid state.
  - Acceptance: 100% branch coverage for `IUpstreamLogoutService`.

- [ ] Story: Integration tests
  - Simulated external provider discovery with logout endpoint.
  - Flow: sign-in (external) → GET logout (option visible) → federated POST → redirected URL shape validated → callback finalizes.
  - Local-only variant: option appears; choose local-only leads to no upstream redirect.
  - Acceptance: Tests green in CI (Windows + Linux runners if applicable).

- [ ] Story: Negative tests
  - Attempt federated POST when no upstream endpoint (expect local fallback or 400).
  - Callback with wrong state.
  - Expired state (advance clock or manipulate TTL).
  - Acceptance: Proper error shaping (ProblemDetails or friendly page) and no unhandled exceptions.

### 7) Documentation
- [ ] Story: Admin / Operator guide section
  - How federated logout works, prerequisites (provider with `end_session_endpoint`), risk notes (user expectation of staying signed in upstream when selecting local-only).
  - Acceptance: Added to `admin-guide.md`.

- [ ] Story: Developer guide additions
  - Parameters unaffected (logout path remains) but mention presence of federated option; guidance on customizing UI.
  - Acceptance: Added to `developer-guide.md`.

- [ ] Story: Backlog cross-link
  - Link from IdP chaining backlog to this document for traceability.
  - Acceptance: PR references both docs.

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
  - Fields: `SupportsFederatedLogout` (bool?; null => infer), `LogoutBehavior` (enum: Offer, AutoFederated, Disabled), `PreferSidForLogout` (bool).
  - Validation: if `LogoutBehavior=AutoFederated` and no upstream endpoint -> reject.
  - Acceptance: Validation tests.

## Data & Persistence Impact
Phase 1: No DB migration required (using auth cookie properties + ephemeral cache). Optional Phase 2 adds `ExternalSessions` table for IdP-initiated logout.

## Security Considerations
Threats & mitigations
- CSRF / state replay -> random, single-use, short TTL state.
- Token leakage in logs -> never log `id_token_hint`, only presence flag.
- Open redirect -> server determines `post_logout_redirect_uri`, no user param.
- DoS via repeated callback -> cheap state lookup + early reject.

## Observability
Key log fields (structured): `event`, `correlation_id`, `idp`, `mode`, `outcome`, `has_hint`, `elapsed_ms`.
Do not include full external URL / tokens.

## Test Matrix
| Scenario | Expectation |
|----------|-------------|
| Local-only logout (no idp claim) | Single option UI, local sign-out success |
| External session; upstream supports endpoint | Two options shown |
| Federated chosen with id_token_hint | Redirect contains post_logout + state + id_token_hint |
| Federated chosen without id_token_hint (sid present) | Redirect no id_token_hint; still upstream logout |
| Federated chosen but endpoint missing (race) | Fallback to local-only + warning log |
| Callback valid state | Final page success, metrics incremented |
| Callback invalid state | 400 / friendly error, failure counter incremented |
| Callback replayed state | Treated as invalid (single-use) |
| Expired state | Invalid (friendly) |
| Attempt POST federated w/o selection flag | Default to local-only (safe) |

## Metrics (Initial Definition)
- `oidc.logout.requests` (counter) tags: `mode=unknown|local|federated` (decided after processing)
- `oidc.logout.federated` (counter)
- `oidc.logout.local` (counter)
- `oidc.logout.failures` (counter) tags: `reason=state_invalid|upstream_missing|exception`
- `oidc.logout.duration.ms` (histogram) tags: `mode`, `idp_bucket`

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

## Acceptance (Phase 1)
- User with external session sees choice and both flows work.
- Local-only flow unchanged for non-external sessions.
- Logs & metrics present without sensitive data.
- All new tests pass in CI; coverage thresholds maintained.
- No DB migration required.

---
Owner: TBD
Initial PRs:
1. Service + handler wiring
2. UI update + tests
3. Metrics/logging instrumentation
4. Docs update

End of document.
