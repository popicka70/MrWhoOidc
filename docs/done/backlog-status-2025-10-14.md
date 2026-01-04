# MrWhoOidc Backlog Status & Recommendations

**Date**: 2025-10-14  
**Author**: AI Assistant  
**Scope**: IdP Chaining, OBO/Token Exchange, M2M, and Core Platform

---

## Executive Summary

The MrWhoOidc platform has reached **Phase 3** maturity with functional multi-provider IdP chaining, inbound/outbound JAR/PAR support, Token Exchange (OBO), and Client Credentials (M2M) grants. **90% of core features are complete**, but **production deployment is blocked by 4 critical P0 items** related to performance, testing, and observability.

### Current State
- ✅ **Functional**: Multi-provider external OIDC, JAR/JARM, PAR, Token Exchange with DPoP bridging, Client Credentials
- 🟡 **At Risk**: JWKS endpoint performance (missing index), correlation tracing (no test coverage), integration tests disabled
- 🔴 **Blocking**: No key expiry monitoring, incomplete M2M admin UI, AMR claim inconsistencies

### Recommended Action
**Focus next 2 weeks on P0 items** to unblock production:
1. JWKS index migration + rotation playbook docs
2. Correlation test coverage + developer guide update
3. Fix disabled integration test + CI validation
4. Admin UI security audit (RBAC, CSRF, accessibility)

---

## Detailed Status by Epic

### 1. IdP Chaining & External OIDC (Phase 3)

**Status**: 🟡 90% Complete – Core functionality working; polish and testing pending

#### ✅ Completed
- Data model and migrations (providers, client mappings, claim mappings, keys)
- Admin UI CRUD (providers, client-provider mappings, claim mappings, provider/client keys)
- Authorization endpoint parameterization (`idp`, `idp_hint`, `login_hint`, `acr_values`)
- External OIDC sign-in flow (PKCE, discovery, token exchange, ID token validation, user provisioning)
- Provider picker UI (remembered provider, auto-redirect, accessibility basics, mobile layout)
- Multi-level IdP chaining configuration support (QR + external + local login combinations)
- Discovery metadata updates (JAR/JARM capabilities)

#### 🟡 In Progress
- **Provider picker A11y polish**: Focus management, semantic button markup, screen reader testing
- **Claim mapping propagation**: Core transforms work; need consistent `amr` emission across all flows
- **Correlation propagation**: Middleware live; missing unit/integration test coverage for error paths
- **Key rotation**: Storage + admin UI present; expiry monitoring not implemented

#### 🔴 Blocked / Missing
- **E2E test suite**: No live upstream IdP tests (Azure AD + Auth0/Okta); currently using mock providers
- **Subject linking options**: Email-based account linking with confirmation not implemented
- **Logo upload validation**: Size/type checks, thumbnail generation, alt text field missing

#### Next Steps (P0)
1. Add composite index on `IdentityProviderKeys(IdentityProviderId, Active, Publishable, Purpose)` – **CRITICAL**
2. Fix `ExternalOidcIntegrationTests` redirect issue and re-enable CI validation – **CRITICAL**
3. Add correlation unit tests (invalid header, cache miss, stale handle) – **HIGH**

---

### 2. Inbound JAR & JARM (Phase 4)

**Status**: ✅ 100% Complete – Production-ready with Redis replay cache

#### ✅ Completed
- Request object parsing/validation (`request`, `request_uri`)
- JWT signature validation against client JWKS
- Claim validation (`aud`, `iss`, `exp`, `nbf`) with configurable clock skew
- Replay protection (in-memory + optional Redis-backed `jti` cache with TTL)
- Parameter merge per RFC 9101 precedence
- JARM response modes (`query.jwt`, `form_post.jwt`) with optional JWE encryption
- Discovery metadata advertising JAR/JARM capabilities

#### Next Steps
- None; feature complete and operational

---

### 3. Outbound JAR & PAR to Upstream IdPs (Phase 5)

**Status**: ✅ 100% Complete – Tested with upstream IdPs

#### ✅ Completed
- Outbound JAR: Sign upstream auth requests when `UseJAR=true` (RS256/PS256, `kid` selection)
- Outbound PAR: Push to upstream PAR endpoint when `UsePAR=true`, receive `request_uri`
- Config validation and discovery test-on-save

#### Next Steps
- None; feature complete and operational

---

### 4. Token Exchange (OBO) – RFC 8693 (Phase 5)

**Status**: ✅ 95% Complete – Core grant working; admin UI polish pending

#### ✅ Completed
- `/token` endpoint handles `grant_type=urn:ietf:params:oauth:grant-type:token-exchange`
- Subject token validation (JWT + opaque, local issuer only)
- Single-hop enforcement (reject if `act` claim present in subject)
- Per-client OBO policy (allow-list callers, source/target audiences, scope narrowing, lifetime cap)
- DPoP bridging modes (`Deny`, `RequireSameJkt`, `AllowSameJktOnly`) with `ath` validation
- Delegation depth tracking for opaque tokens (`DelegationDepth` + `OboMaxDelegationDepth`)
- Issued tokens include `act` claim with actor identity
- Discovery advertises `token_exchange` grant when feature flag enabled
- Dedicated metrics (counters + histogram with outcome/dpop_mode/target_aud tags)
- Unit tests covering happy path, audience validation, DPoP modes, delegation depth

#### 🟡 In Progress
- **OBO Admin UI**: Basic CRUD present under Clients → Edit → OBO tab; needs polish and validation UX improvements
- **Test coverage**: Core paths covered; full policy matrix and additional DPoP mode combinations pending

#### Next Steps (P1)
1. Complete full policy matrix test coverage (all audience/scope/caller combinations) – **MEDIUM**
2. Polish OBO admin UI (field-level validation, inline help text, example configs) – **MEDIUM**

---

### 5. Client Credentials (M2M) – RFC 6749 (Phase 5)

**Status**: 🟡 70% Complete – Grant working; admin UI and policy management missing

#### ✅ Completed
- `/token` endpoint handles `grant_type=client_credentials`
- Client authentication (`client_secret_basic`, `client_secret_post`, `private_key_jwt`)
- Audience vs resource validation
- Scope enforcement via `ClientScopes` table
- JWT issuance (15 min lifetime) with optional DPoP binding
- Discovery advertises `client_credentials` grant and auth methods

#### 🔴 Missing (Blocks Production Use)
- **No Admin UI**: Policy configuration requires manual DB edits
- **No per-client lifetime/format**: All M2M tokens use global 15-min JWT default
- **No mTLS support**: Optional mTLS thumbprint validation not implemented
- **Weak telemetry**: Logs lack audience/scope buckets for M2M-specific monitoring

#### Next Steps (P1)
1. Build M2M admin UI (allowed scopes/audiences, lifetime, auth methods, mTLS thumbprints) – **HIGH**
2. Add unit tests for scope/audience validation and DPoP with M2M – **HIGH**
3. Implement per-client token lifetime and format preferences – **MEDIUM**
4. Add M2M-specific telemetry (audience/scope distribution metrics) – **MEDIUM**

---

### 6. JWKS Endpoints (Provider/Client Key Publication)

**Status**: 🟡 80% Complete – Endpoints live; optimization and docs pending

#### ✅ Completed
- `/providers/{providerName}/jwks` endpoint (per-provider signing keys)
- `/providers/jwks` aggregated endpoint (all providers)
- `/clients/{clientId}/jwks` optional endpoint (client public keys)
- Feature flags (`ExposeProviderJwks`, `ExposeAggregatedProviderJwks`, `ExposeClientJwks`)
- Response shaping (strip private params, include `kty`/`kid`/`use`/`alg`)
- Caching (`Cache-Control: public, max-age=300`, `ETag`, `If-None-Match` 304 support)
- Metrics (requests, cache hit/miss, zero keys warnings, keys returned, ETag changes)
- Admin UI publish/unpublish actions with guard preventing unpublishing active JAR keys

#### 🔴 Missing (Performance Risk)
- **No composite index**: `PublicJwksCache` queries filter on `(IdentityProviderId, Active, Publishable, Purpose)` without index – **CRITICAL**
- **No rotation playbook**: Operators lack documented procedure for key rollover with overlap strategy
- **No ADR**: Design decisions (per-provider vs aggregated, feature flags, no discovery advertisement) undocumented

#### Next Steps (P0)
1. Add composite index migration for `IdentityProviderKeys` – **CRITICAL**
2. Author key rotation playbook with overlap timeline (T-7, T-2, T+0, T+2) – **HIGH**
3. Add curl examples and feature flag guidance to admin guide – **HIGH**
4. Write ADR-0009 capturing JWKS endpoint design rationale – **HIGH**

---

### 7. Correlation & Observability

**Status**: 🟡 75% Complete – Core middleware live; test coverage and docs pending

#### ✅ Completed
- `CorrelationTrackingMiddleware` (generate or accept `X-Correlation-Id` header)
- `CorrelationStateCache` (opaque `cid_ref` handle in state, maps to CID via Memory/Redis)
- External start/callback handlers enrich logs and Activity with CID
- Admin APIs accept and propagate CID
- External callback metrics (duration histogram + outcome counters with correlation tags)

#### 🔴 Missing (Debugging Risk)
- **No unit tests**: Invalid header, cache miss, stale handle scenarios lack coverage – **HIGH**
- **Incomplete docs**: Developer guide missing CID propagation examples and retention policy
- **No security review**: Privacy expectations and PII handling policy undocumented

#### Next Steps (P0)
1. Add unit/integration tests for correlation error paths – **CRITICAL**
2. Update developer guide with CID propagation best practices – **HIGH**
3. Document CID retention policy (ephemeral, not persisted) – **HIGH**
4. Conduct security review of correlation data flow – **HIGH**

---

### 8. Testing & Quality Assurance

**Status**: 🔴 60% Complete – Unit tests strong; integration/E2E tests weak

#### ✅ Completed
- Unit tests: JAR parsing, client assertion, PAR store, auth code, key rotation, token service, claim mapping, provider picker ordering, OBO policy scenarios
- Integration tests: Multi-provider mapping, PAR stress tests, external OIDC dual-provider cancel path, discovery metadata

#### 🔴 Missing / Broken
- **`ExternalOidcIntegrationTests` happy path disabled**: Currently `[Ignore]` due to release build redirect issue – **CRITICAL**
- **No E2E tests**: Zero live upstream IdP tests (Azure AD, Auth0, Okta) – **HIGH**
- **Missing M2M tests**: No unit tests for scope/audience validation, auth method checks, DPoP with M2M
- **No correlation tests**: Error path scenarios (invalid header, cache miss) lack coverage

#### Next Steps (P0 + P1)
1. Fix redirect issue and re-enable `ExternalOidcIntegrationTests` – **CRITICAL (P0)**
2. Add correlation error path tests (invalid header, cache miss, stale handle) – **CRITICAL (P0)**
3. Set up E2E test suite with live upstream IdPs (Azure AD + Auth0) – **HIGH (P1)**
4. Add M2M unit tests (scope/audience, auth methods, DPoP) – **HIGH (P1)**
5. Add OBO full policy matrix tests – **MEDIUM (P1)**

---

### 9. Documentation

**Status**: 🟡 70% Complete – Admin/developer guides exist; needs examples and screenshots

#### ✅ Completed
- Admin guide draft (providers, keys, mappings, OBO policy, JWKS endpoints)
- Developer guide draft (authorize parameters, JAR/JARM, discovery)
- Architecture doc (projects, flows, DB schema)
- HTTP request samples (`docs/http/obo-token-exchange.http`)

#### 🔴 Missing
- **No CID propagation guide**: Developer guide lacks header usage examples and retention policy
- **No key rotation playbook**: Operators lack step-by-step procedures with timelines
- **No screenshots**: Admin guide has no UI screenshots for visual guidance
- **No E2E setup guide**: Instructions for configuring live upstream IdPs missing

#### Next Steps (P0 + P1)
1. Add CID propagation section to developer guide with examples – **HIGH (P0)**
2. Author key rotation playbook with overlap strategy and timelines – **HIGH (P0)**
3. Add screenshots to admin guide (provider config, key management, OBO settings) – **MEDIUM (P1)**
4. Document E2E test setup (Azure AD + Auth0 dev tenants) – **MEDIUM (P1)**

---

## Production Readiness Checklist

### ✅ Complete
- [x] Multi-provider external OIDC sign-in working
- [x] JAR/JARM inbound with replay protection
- [x] Outbound JAR/PAR to upstream IdPs
- [x] Token Exchange (OBO) grant with DPoP bridging
- [x] Client Credentials (M2M) grant with scope enforcement
- [x] Rate limiting (in-memory + Redis-backed distributed)
- [x] Discovery metadata complete
- [x] Correlation middleware operational
- [x] JWKS endpoints live (per-provider + aggregated)

### 🔴 Blocking Production
- [ ] **JWKS index migration** (performance degradation risk)
- [ ] **Correlation test coverage** (debugging capability at risk)
- [ ] **Integration test fix** (CI pipeline not validating external flow)
- [ ] **Admin UI security audit** (RBAC, CSRF, accessibility)

### 🟡 Recommended Before Production
- [ ] Key expiry monitoring (proactive alerts)
- [ ] AMR claim consistency (downstream step-up reliability)
- [ ] M2M admin UI (eliminate manual DB edits)
- [ ] E2E test suite (live upstream IdP validation)
- [ ] Key rotation playbook (operational procedures)

---

## Prioritized Roadmap

### Week 1-2: P0 – Unblock Production (CRITICAL)
**Goal**: Address blockers preventing production deployment

1. **JWKS Index Migration** [2 days]
   - Add composite index on `IdentityProviderKeys(IdentityProviderId, Active, Publishable, Purpose)`
   - Deploy to staging and validate query performance
   - Update admin guide with index creation notes

2. **Correlation Testing** [2 days]
   - Add unit tests: invalid header, cache miss, stale handle scenarios
   - Add integration test: CID round-trip through external OIDC flow
   - Update developer guide with CID propagation examples and retention policy

3. **Integration Test Fix** [1 day]
   - Debug and fix `ExternalOidcIntegrationTests` redirect issue in release builds
   - Re-enable test and add CI gate to prevent future regressions
   - Validate test passes in both debug and release configurations

4. **Admin UI Security Audit** [2 days]
   - Review RBAC enforcement on all admin endpoints (providers, clients, keys, OBO)
   - Verify CSRF protection on state-changing operations
   - Run axe-core accessibility scan and remediate critical violations
   - Document audit findings and remediation plan

5. **Key Rotation Documentation** [1 day]
   - Author ADR-0009 (JWKS endpoint design rationale)
   - Write key rotation playbook with overlap strategy (T-7, T-2, T+0, T+2 timeline)
   - Add curl examples and feature flag guidance to admin guide

**Checkpoint**: Review P0 completion and decide production deployment go/no-go

---

### Week 3-6: P1 – Polish & Production Hardening (HIGH)
**Goal**: Complete feature set and operational readiness

6. **Key Expiry Monitoring** [3 days]
   - Background service scanning `IdentityProviderKeys` and client keys for expiry < 7 days
   - Emit `oidc.keys.expiry_warning` metric and structured warning logs
   - Integration test with simulated near-expiry scenario
   - Admin UI dashboard showing upcoming expirations

7. **AMR Claim Consistency** [2 days]
   - Update `TokenService` to emit `amr` consistently across all flows (authorization code, refresh, token exchange, client credentials)
   - Unit tests validating `amr` presence/absence per `AuthOptions.EmitAmr*` flags
   - Merge upstream + local authentication methods correctly

8. **M2M Admin UI** [4 days]
   - Build Client Credentials admin page (allowed scopes/audiences, lifetime, auth methods, mTLS thumbprints)
   - Add per-client token format preference (JWT vs opaque)
   - Unit + integration tests for scope/audience validation and DPoP with M2M
   - Sample documentation and HTTP request examples

9. **E2E Test Suite** [5 days]
   - Set up Azure AD dev tenant + Auth0/Okta test account
   - Build E2E test matrix: JAR + PAR combinations, multi-provider flows, DPoP bridging
   - Automate CI pipeline for E2E tests (nightly or on-demand trigger)
   - Document E2E setup and maintenance procedures

10. **Telemetry Expansion** [3 days]
    - Provider selection latency histogram with outcome tags
    - Upstream token exchange duration metric (external IdP callback)
    - Structured cancellation taxonomy (user cancel vs timeout vs upstream error)
    - M2M-specific metrics (audience/scope buckets, auth method distribution)
    - Document recommended dashboard queries and alerting thresholds

**Checkpoint**: Production deployment with monitoring

---

### Q1 2026: P2 – Advanced Features (NICE-TO-HAVE)
**Goal**: Improve UX and support advanced scenarios (defer unless customer requirement)

11. **Admin UI UX Polish**
    - Drag & drop provider ordering with keyboard accessibility
    - Claim mapping test mode (preview transforms on sample input)
    - Advanced JWKS visual preview (table: kid/alg/kty/use/expires)
    - Logo upload validation (size/type, thumbnail generation, alt text)
    - Dark mode styling parity for keys & mapping pages

12. **Provider Picker A11y Polish**
    - Focus management on page load and error states
    - Semantic button markup review
    - Screen reader testing and ARIA live regions
    - High-contrast theme compatibility

13. **W3C Trace Context Bridge**
    - Map CID into `ActivityTraceId` when absent
    - Add baggage item `cid` for distributed tracing exporters
    - Optional CID header propagation to downstream APIs (config flag)
    - Admin UI diagnostic panel showing current CID

14. **Multi-Hop Token Exchange**
    - Support `act` claim chains (delegation depth > 1)
    - Update policy model to allow/deny multi-hop per client
    - Token introspection includes full actor chain
    - Security review and documentation for delegation audit trail

---

## Risk Assessment

### 🔴 Critical Risks (Block Production)
1. **JWKS Performance Degradation**: Missing index on `IdentityProviderKeys` will cause slow queries under load (100ms+ latency at 1000 providers)
2. **Unvalidated External IdP Flow**: Disabled integration test means code changes can silently break external OIDC sign-in
3. **Correlation Debugging Failure**: Missing test coverage for error paths means production incidents may lack traceable CIDs
4. **Admin Endpoint Security Gaps**: RBAC/CSRF/accessibility issues could expose secrets or create compliance liability

**Mitigation**: Address all P0 items before production deployment

### 🟡 High Risks (Operational Impact)
5. **No Key Expiry Alerts**: Operators won't know when signing keys are expiring until service outage occurs
6. **AMR Claim Unreliability**: Downstream RPs relying on `amr` for step-up authentication will have inconsistent behavior
7. **M2M Manual Configuration**: Lack of admin UI increases risk of misconfiguration and security policy violations
8. **No Live E2E Tests**: Changes to external IdP integration may break production without early warning

**Mitigation**: Address P1 items within 4-6 weeks of production deployment

### 🟢 Low Risks (UX/Nice-to-Have)
9. **Admin UI Polish**: Lack of drag-drop ordering and claim test mode affects efficiency but not functionality
10. **Provider Picker A11y**: Functional for sighted keyboard users but may not meet WCAG 2.1 AA for screen readers

**Mitigation**: Defer P2 items to Q1 2026 unless accessibility audit requires remediation

---

## Success Metrics

### Phase 3 GA Acceptance Criteria
**Must Have** (before production deployment):
- [ ] All P0 items complete (JWKS index, correlation tests, integration test fix, security audit)
- [ ] CI pipeline green with no ignored tests
- [ ] Documentation published: developer guide (CID propagation), admin guide (key rotation playbook)
- [ ] Security review sign-off on admin endpoints and correlation handling
- [ ] Performance test: provider picker with 10+ providers, JWKS endpoint under load (< 50ms p99)

**Should Have** (within 4 weeks of production):
- [ ] Key expiry monitoring live with integration test
- [ ] AMR claim emitted consistently with unit test coverage
- [ ] M2M admin UI operational (scopes/audiences/lifetime)
- [ ] E2E test suite running against at least one live upstream IdP

**Nice to Have** (defer to post-GA):
- Admin UI polish (drag-drop, claim test mode, enhanced JWKS preview)
- W3C Trace Context bridge
- Multi-hop token exchange

---

## Recommendations

### Immediate Actions (Next 2 Weeks)
1. **Assign P0 items to engineering team** with strict 2-week deadline
2. **Schedule daily standups** to track P0 blocker resolution
3. **Prepare staging environment** for JWKS index migration deployment
4. **Book security audit slot** for admin endpoints (RBAC, CSRF, a11y)
5. **Document go/no-go criteria** for production deployment decision

### Post-P0 Actions (Weeks 3-6)
6. **Prioritize key expiry monitoring** (most impactful operational safeguard)
7. **Build M2M admin UI** (eliminates risky manual DB edits)
8. **Set up E2E test infrastructure** (Azure AD + Auth0 dev tenants)
9. **Expand telemetry** (provider selection latency, cancellation taxonomy)

### Long-Term Strategy (Q1 2026)
10. **Conduct post-deployment retrospective** to identify technical debt
11. **Gather user feedback** on admin UI workflows (prioritize UX improvements)
12. **Evaluate multi-hop token exchange** demand (defer unless clear customer need)
13. **Plan W3C Trace Context integration** if distributed tracing becomes critical

---

## Conclusion

MrWhoOidc has achieved significant functional maturity with 90% of core IdP chaining, OBO, and M2M features complete. However, **production deployment is blocked by 4 critical P0 items** related to performance, testing, and observability.

**Recommendation**: Focus the next 2 weeks exclusively on P0 items (JWKS index, correlation tests, integration test fix, security audit). Once P0 is complete, conduct a go/no-go review for production deployment. Post-deployment, prioritize P1 items (key expiry monitoring, M2M admin UI, E2E tests) to strengthen operational readiness.

With disciplined execution of the P0 roadmap, MrWhoOidc can safely enter production within 2 weeks and achieve full operational maturity within 6 weeks.
