# Phase 3 – Next Steps Backlog (Multi‑provider GA)

Updated: 2025-09-25

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
  - Deliverables
    - Structured logs with correlation IDs across start and callback.
    - User‑facing error pages for cancel/timeout/invalid_scope with localization plumbing.
    - Metrics: start/callback duration histograms; outcome counters (success/cancel/error); provider and prompt/acr buckets (no PII).
  - Tests
    - UI smoke test for cancel path; unit test for correlation propagation across the flow.
  - Acceptance
    - Operators can correlate failures quickly; end‑users see clear, localized messages.

- [ ] Inbound JAR hardening with Redis replay cache
  - Deliverables
    - Enable Redis‑backed replay cache in production profile.
    - Expose TTL and clock skew in `AuthOptions` and document recommended values.
    - Ensure replay cache key includes `iss+aud+jti` (and nonce where applicable).
    - Align discovery `request_object_signing_alg_values_supported` with allowed algs.
  - Tests
    - Conformance tests for replay rejection; boundary checks for exp/nbf skew.
  - Acceptance
    - Replay attempts are reliably blocked; discovery reflects actual capabilities.

- [ ] Provider picker polish (a11y + mobile)
  - Deliverables
    - Remembered provider hint UI; a11y roles/labels/tab order; responsive/mobility tweaks.
  - Tests
    - Accessibility checks; cookie‑based last‑provider preference behavior.
  - Acceptance
    - Picker works well on desktop/mobile with accessibility basics covered.

- [ ] Integration tests and CI gates
  - Deliverables
    - Integration tests: OBO happy path + error cases (JWT and opaque subjects); multi‑provider selection logic.
    - CI: run integration tests on PRs; spin up Redis for replay cache tests.
    - Artifacts: update `docs/http/obo-token-exchange.http` if needed; add one end‑to‑end script using a fake upstream.
  - Acceptance
    - CI turns red on regressions in critical OBO and chaining paths; Redis‑backed tests are stable.

- [ ] Documentation first draft
  - Deliverables
    - Admin guide: providers, mappings, keys, OBO policy editors (with screenshots).
    - Developer guide: authorize params (`idp`, `acr_values`), inbound JAR/JARM, token exchange usage and DPoP modes.
  - Acceptance
    - New clients and admins can complete basic setups without code changes.

### Quick wins (parallel with P0)

- [x] Discovery hygiene
  - Ensure `request_object_signing_alg_values_supported` exactly matches `AuthOptions` allow‑list; verify well‑known with an external validator.

- [ ] Rate‑limit headers verification
  - Add tests asserting `Retry-After` and rate‑limit headers on 429 for `/token` and `/introspect` when Redis is configured.

- [ ] Caching guardrails
  - Verify discovery/JWKS caching with ETag/Cache‑Control; add configurable max‑age and retry/backoff settings.

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

---

## Documentation structure (proposed)

- Admin guide
  - Providers → Config → Keys (PEM/JWK) → Client mappings → Claim mappings → OBO policy
- Developer guide
  - Authorize params and hints → JAR/JARM → Token Exchange (OBO) flows and DPoP modes → Discovery examples
- E2E guides
  - “RequireSameJkt” OBO (exists) + “Two OIDC providers” flow with cancel/error variants

Notes
- Current test status: CI green locally; 65/65 unit tests passing (2025‑09‑25). Integration coverage will expand in P0.
