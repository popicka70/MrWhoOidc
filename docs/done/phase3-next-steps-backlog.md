# Phase 3 – Next Steps Backlog (Multi‑provider GA)

Updated: 2025-09-26

Status legend
- [x] Done
- [~] In progress
- [ ] Not started

Goals
- Ship Phase 3 (Multi‑provider GA) with solid UX, security hardening, and docs.
- Reduce operational risk via telemetry, rate limits, and replay protections.
- Ensure CI is green with meaningful integration coverage.

Milestones
- P0 (next 2 weeks): Critical UX + security + tests + docs drafts.
- P1 (next 2–4 weeks): Interop features, richer telemetry, and M2M polish.

---

## P0 Sprint (2 weeks)

- [x] Keys UI: PEM import and JWKS validation hardening
  - Deliverables
    - Import PEM (PKCS8/PKCS1/EC) and convert to JWK in Admin UI.
    - Pretty‑print/compact toggle and richer JWKS preview (kid/alg/kty/use/thumbprint summary).
    - Validation that alg/kty/use combinations are consistent (e.g., RS256→RSA, ES256→EC P‑256; use=enc vs sig).
    - Duplicate kid detection and basic expiry metadata display.
  - Tests
    - Unit tests for PEM→JWK conversion (RSA/EC) and invalid PEM failure modes.
    - API validation tests for alg/kty/use mismatches and duplicate kids.
  - Acceptance
    - Admin can import keys reliably; invalid inputs are rejected with actionable errors; JWKS preview is informative.

- [ ] External OIDC UX telemetry + friendly errors
  - Progress
    - Not started (no correlation propagation helper or friendly error page assets committed yet).
  - Deliverables
    - Structured logs with correlation IDs (or Activity TraceId) across start and callback.
    - User‑facing error pages for cancel/timeout/invalid_scope with localization plumbing.
    - Metrics: start/callback duration histograms; outcome counters (success/cancel/error); provider and prompt/acr buckets (no PII).
  - Tests
    - UI smoke test for cancel path; unit test for correlation propagation across the flow.
  - Acceptance
    - Operators can correlate failures quickly; end‑users see clear, localized messages.

- [x] Inbound JAR hardening with Redis replay cache
  - Deliverables
    - [x] Enable Redis‑backed replay cache in production profile (DI overrides in WebAuth when `ConnectionStrings:redis` is set).
    - [x] Expose TTL and clock skew in `AuthOptions` and document recommended values (see `docs/jar-replay-cache.md`).
    - [x] Ensure replay cache key includes `iss+aud+jti` (and nonce where applicable).
    - [x] Align discovery `request_object_signing_alg_values_supported` with allowed algs (reads from `AuthOptions`).
  - Tests
    - [x] Conformance tests for replay rejection; boundary checks for exp/nbf skew.
  - Acceptance
    - [x] Replay attempts are reliably blocked; discovery reflects actual capabilities.

- [x] Provider picker polish (a11y + mobile)
  - Progress
    - Implemented: ARIA landmarks/labels (`role="main"`, heading association, list/listitem semantics), recommended provider alert with `role="status"` + `aria-live="polite"`, hidden descriptive text for SR users, focus-visible outline styling, recommended highlight (badge + thicker border), responsive touch target sizing & layout tweaks (`@media (max-width:576px)`).
    - Logic already covered by `ProviderPickerTests` (cookie + idp hint ordering, recommendation ordering).
    - Remaining follow-up (moved to quick win / test hardening): basic analytics hook (emit provider selection event without PII) and automated a11y/static regression test + mobile viewport snapshot.
  - Deliverables (remaining follow-up)
    - Add lightweight analytics instrumentation (e.g., server log event or JS data-* beacon) – P0 nice-to-have.
    - Add test: verify recommended provider gets `aria-label` including "Recommended" and presence of status alert; static check for list semantics.
    - Add mobile viewport screenshot (for docs) & include in Admin/Developer guide screenshots section.
  - Tests
    - Existing: cookie + idp hint ordering in `ProviderPickerTests`.
    - Pending: accessibility markup assertions and mobile snapshot (see follow-up above).
  - Acceptance
    - Core picker UX, a11y, and mobile responsiveness complete; only telemetry/test reinforcement outstanding.

- [~] Integration tests and CI gates
  - Status (2025-09-26)
    - All tests green (74/74). Rate‑limit header integration tests now succeed when Redis reachable; skip via Inconclusive if Redis absent (needs CI service to avoid silent skips).
    - OBO tests present; need multi‑provider end‑to‑end and negative DPoP bridging scenarios.
  - Deliverables (remaining)
    - Provide Redis in CI; fail build if not reachable.
    - Add multi‑provider selection + successful authorization test.
    - Add negative tests: cancelled external login (after friendly error pages), invalid provider hint.
    - Add `docs/http` scripted example for OBO negative + positive flows.
  - Next
    - Add CI job/service definition for Redis.
    - Mark Redis‑dependent tests with category and assert they ran (no skip) in CI summary.
  - Acceptance
    - CI turns red on regressions in critical OBO, rate limiting, and multi‑provider selection; no silent skips.

- [~] Documentation first draft
  - Status
    - Admin + Developer guide drafts committed (2025-09-25). Content present; screenshots & quickstart examples pending.
  - Remaining Deliverables
    - Add screenshots (providers list, key import, OBO policy tab, provider picker with recommendation highlight).
    - Quickstart: external OIDC + token exchange curl/HttpClient example.
    - Cross-link replay cache + rate limiting docs.
  - Acceptance
    - New clients and admins can complete setups without external assistance.
  - Links
    - Draft Admin guide: `docs/admin-guide.md`
    - Draft Developer guide: `docs/developer-guide.md`

### Quick wins (parallel with P0)

- [x] Discovery hygiene
  - Ensure `request_object_signing_alg_values_supported` exactly matches `AuthOptions` allow‑list; verify well‑known with an external validator.

- [x] Rate‑limit headers verification
  - Status: Integration tests green (routing added). Conditional skip still possible if Redis unavailable.
  - Next: Provide Redis in CI; add test asserting headers absent when under limit; ensure skip reported distinctly.

- [ ] Caching guardrails
  - Verify discovery/JWKS caching with ETag/Cache‑Control; add configurable max‑age and retry/backoff settings.
  - Add test: unchanged JWKS returns same ETag; rotation changes ETag & invalidates caches.

- [ ] SDK pinning (global.json)
  - Add global.json to pin .NET 9 preview SDK (remove NETSDK1057 noise; deterministic CI). Acceptance: warning removed in test run.

---

## P1 (next 2–4 weeks)

- [ ] Optional JWKS endpoints
  - Deliverables
    - Public JWKS exposure per provider/client scope (if enabled by policy), with cache headers.
    - Document rotation with `kid` changes and caching expectations.
  - Tests
    - Fetch/caching behavior; rotation test (kid rollover).
  - Acceptance
    - Downstream parties can fetch stable JWKS; rotation works without downtime.

- [ ] Telemetry expansion for external OIDC flow
  - Deliverables
    - Richer start/callback metrics and structured logs (no PII); baseline dashboards.
  - Acceptance
    - Operators have actionable dashboards: latency distribution, error rates, provider distribution.

- [ ] Subject linking options
  - Deliverables
    - Email‑based linking flow (opt‑in), per‑client auto‑provision toggle; confirmation UX.
  - Acceptance
    - Returning users can link identities with minimal friction; duplicates avoided.

- [ ] M2M polish
  - Deliverables
    - Admin UI: per‑client allowed scopes/audiences, token lifetime/format, required auth methods (secret vs `private_key_jwt`), optional mTLS.
    - Tests: unit and integration for scope/audience and auth methods; DPoP acceptance/rejection.
  - Acceptance
    - Admins can configure M2M policy; issuance honors constraints; tests cover critical paths.

---

## OBO follow‑ups

- [ ] Delegation policy UI polish
  - Better validation hints, presets, and error messages; contextual help linking to docs.

- [ ] Test matrix expansion
  - DPoP mode combinations (including `AllowSameJktOnly` specifics) and opaque `DelegationDepth` > 1 within policy bounds.
  - Negative tests for `invalid_target`, `insufficient_scope`, `dpop_same_key_required`.

- [ ] Introspection shaping checks
  - Tests to ensure `act` appears when present and shaping doesn’t leak actor details to unauthorized callers.

---

## CI/CD and ops

- [ ] CI environment services
  - Spin up Redis in CI jobs for replay cache and rate‑limit tests; gate PRs on integration suites.

- [ ] Security/tooling
  - Add secret scanning and dependency audit; optional static analyzers for config drift.

- [ ] .NET 9 preview guard
  - Lock the toolchain in CI to a known‑good version until GA; review updates periodically.
  - Note: `NETSDK1057` (preview SDK in use) observed during local test runs — add a repo `global.json` to pin the intended SDK.

---

## Documentation structure (proposed)

- Admin guide
  - Providers → Config → Keys (PEM/JWK) → Client mappings → Claim mappings → OBO policy
- Developer guide
  - Authorize params and hints → JAR/JARM → Token Exchange (OBO) flows and DPoP modes → Discovery examples
- E2E guides
  - “RequireSameJkt” OBO (exists) + “Two OIDC providers” flow with cancel/error variants

Notes
- Current test status (2025‑09‑26): 74 total; 74 passing; Redis‑dependent tests may skip if Redis absent (address via CI service).

---

## Near-term prioritized next steps (proposed)

1. External OIDC UX telemetry & friendly error pages (P0, unstarted).
2. Provider picker analytics + a11y regression test (core polish DONE; add instrumentation & tests).
3. SDK pinning (add global.json) to stabilize builds.
4. CI Redis service to eliminate skipped rate-limit/replay tests.
5. Additional integration tests: multi-provider success path + negative DPoP bridging + cancel external login.
6. Documentation polish: screenshots & quickstart snippets.
7. Caching guardrails (ETag/Cache-Control tests for discovery/JWKS).

Secondary (start after 1–4):
- Optional JWKS endpoint design clarifications.
- Telemetry dashboard definitions (metrics naming + exemplar queries).
- M2M polish scope validation plan.

Risks / Watch:
- Skipped Redis tests hiding regressions until CI change.
- Missing user-friendly external OIDC error pages → poor UX/support load.
- Unpinned SDK could introduce instability with future previews.

Metric Targets (draft):
- External OIDC start→callback median < 3s (excluding upstream latency), p95 < 6s.
- Token exchange success/error ratio < 98/2 after telemetry.
- Zero skipped critical (Redis) integration tests in CI.
