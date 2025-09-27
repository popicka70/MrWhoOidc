# Program.cs Refactor Backlog

> Progress Assessment Date: 2025-09-27

This document has been updated to reflect the current repository state (branch `UserPage`) and to refine the remaining plan toward a slim `Program.cs` (<=150 lines). A phased approach is retained, but statuses and next actions have been tightened for velocity and safety.

Purpose: Reduce `MrWhoOidc.WebAuth/Program.cs` (~1100 lines) into a thin composition root (< ~150 lines) by extracting discrete concerns into feature/service registration & endpoint mapping modules while preserving behavior and existing public surface (routes, policies, options).

Target End State (Definition of Done for Epic):
`Program.cs` contains only:
- Builder creation & high-level configuration ordering
- A sequence of clearly named extension method calls (Add*/Use*/Map*)
- No inline endpoint lambdas or nested types
- No business logic, DTO definitions, or helper methods

Maximum line count goal: <= 150 lines (stretch: <= 120 lines).

## Guiding Principles
1. Separation by Feature Boundary: Discovery, Authorization, Token, UserInfo, Logout, Introspection, Revocation, PAR, External OIDC, JWKS, Backchannel Logout (BCL), Admin APIs, Security (DPoP/JAR), Rate Limiting, Observability, Persistence/Seeding.
2. Keep Protocol/Core logic in `MrWhoOidc.Auth`; Web-specific wiring & endpoint shape in `MrWhoOidc.WebAuth`.
3. Prefer extension methods over large inline blocks: `IServiceCollection` (Add...), `WebApplication` (Map...).
4. Each extension method must be idempotent and side-effect free beyond registrations.
5. Endpoint mapping grouped per feature (e.g., `MapOidcProtocolEndpoints`, `MapAdminApis`, `MapJwksEndpoints`, `MapBackchannelHealth`).
6. Maintain existing route patterns, auth policies, CORS policies, and rate limiting policy names (backward compatibility) unless explicitly versioned.
7. Add safety tests before refactor to freeze observable behavior (route list, policies, rate limiting names, and a few critical end-to-end flows).
8. No introduction of external frameworks for modular endpoints (avoid over-engineering). Simple internal interface-based plugin (optional) is acceptable.

## Current Responsibility Inventory (Snapshot)
1. Options & Configuration Binding (OidcOptions, AuthOptions, AdminAuthOptions, Backchannel, Audit, RateLimit, FederatedLogout, Alert Options)
2. Telemetry & Observability (App Insights, Metrics recorders, Audit sink, Alert publisher + sampler)
3. Persistence & DataProtection (AuthDbContext, migrations trigger, key store warm-up)
4. Authentication & Authorization (Cookie schemes, admin policy, custom handler)
5. Security Services (DPoP validator/replay/nonce stores, JAR replay cache)
6. External Identity Provider & Claim Mapping services
7. PAR services & cleanup hosted service
8. Backchannel Logout: dispatch worker, alert sampler, runtime state, admin + health endpoints
9. Rate Limiting (policies + optional Redis global limiter) & distributed middleware
10. CORS policy definition for OIDC endpoints
11. Antiforgery + DataProtection customization
12. Localization & culture pipeline
13. Endpoint mappings (Discovery/JWKS, Authorize, Logout flows, Token + grants, Revocation, UserInfo, Introspection, PAR, External OIDC chaining, Admin CRUD sets for Providers, Provider Keys, Claim Mappings, Client Provider mappings, Client Keys, BCL Outbox & Alerts, BCL health)
14. Migration / seeding command mode (`--seed`) and ApplicationStarted migration + key generation + default data
15. Inline helper & nested types: `SetConditionalEtag`, Admin DTO records, `AdminApiHelpers`, `AdminAuthOptions`, `AdminRequirement`, `AdminAuthorizationHandler`.

## High-Level Epics & Phases (Status)
| Phase | Epic | Status | Notes / Gaps |
|-------|------|--------|--------------|
| 0 | Safety Nets | PARTIAL | Snapshot test exists (`ProgramSurfaceSnapshotTests`) capturing full endpoint manifest, but: (a) rate limiting policy names not asserted, (b) admin policy explicit test missing, (c) negative/positive functional probes (discovery/token/userinfo) absent, (d) `/health/backchannel` shape not golden-checked. Line-count guard present. |
| 1 | Extract Nested Types | COMPLETE | `AdminAuthOptions`, `AdminRequirement`, `AdminAuthorizationHandler`, DTO records, `AdminApiHelpers`, `EtagHelpers` all extracted. No remaining nested types in `Program.cs` aside from temporary `ProgramEndpointMapping` partial (can be deleted later). |
| 2 | Service Registration Modularization | PARTIAL | Only narrow `AddMrWhoOidcAuthAndAdmin` extension implemented. Observability, security, persistence + seeding, rate limiting, CORS, antiforgery, PAR, DPoP, external OIDC, backchannel services still inline. |
| 3 | Endpoint Mapping Modularization | PARTIAL | Core OIDC + some infra moved to `MapMrWhoOidcEndpoints` (internal). Admin CRUD, backchannel health endpoint, and BCL admin endpoints remain inline in `Program.cs`. Migration/seeding logic currently (mis)located inside endpoint mapping extension. |
| 4 | Rate Limiting & CORS Consolidation | NOT STARTED | Policies still registered inline. No central defaults object or config override tests. CORS logic still inline. |
| 5 | Feature Module Interface (Optional) | DEFER (TBD) | Value not yet justified—will re‑evaluate after core extraction (Phases 2–4). |
| 6 | Slim Program.cs | NOT STARTED | Current line count ≈918 (baseline test uses 1036). Major blocks remain. |
| 7 | Cleanup & Docs | NOT STARTED | Docs not yet updated; ADR absent. |

### Current Program.cs Size Trend
- Original (estimated): ~1100 lines
- After Phases 1 + partial 2/3: 918 lines
- Target after next two PRs (finish Phases 2 & 3 extractions): ~550–600 lines
- Final target (Phase 6): <=150 (stretch 120)

### Key Technical Debts Introduced During Partial Extraction
- Migration & seeding logic lives inside `EndpointMappingExtensions` (violates separation; should move to dedicated infra extension or startup helper).
- Rate limiting metadata not reflected in snapshot test (reduces safety net value).
- Mixed responsibility for option binding (some in Program.cs, others implied for future extraction). Need a consistent rule: bind in Program OR inside feature extension. Recommendation: Keep binding in Program for top-level options, feature-specific (Backchannel, Audit, Alerts, RateLimiting) inside respective Add* methods.
- Endpoint mapping extension is `internal` and monolithic; should be decomposed into `MapOidcProtocolEndpoints`, `MapAdminApis`, `MapBackchannelHealthEndpoint`, and optional `MapStaticAndRazor` for clarity.

---

## Phase 0 – Safety Nets (Augmented Backlog)
Status: PARTIAL

Still Needed:
1. Add focused test capturing rate limiting policy names actually applied to endpoints (parse metadata implementing `IRateLimiterPolicy` or look for `EnableRateLimitingAttribute` & `DisableRateLimitingAttribute`).
2. Add functional smoke tests (using `WebApplicationFactory<Program>`):
    - `/.well-known/openid-configuration` returns 200 & JSON with required fields (issuer, authorization_endpoint).
    - `/jwks` returns 200 & keys array.
    - `/authorize` without params returns 400 or appropriate error (snapshot expected code now).
    - `/token` POST empty form returns 400 JSON error.
    - `/userinfo` without auth returns 401.
3. Add admin policy test verifying presence of policy name `admin` and that `AdminAuthorizationHandler` is registered as scoped.
4. Add test enumerating defined rate limiting policy names (e.g., `rl-authorize`, `rl-token`, `rl-token-exchange`, `rl-userinfo`, `rl-par`, `rl-introspect`, `rl-jwks`, `rl-admin`) and asserting exact set.
5. Add golden contract test for `/health/backchannel` (assert JSON has `enabled`, `backlog`, `openCircuits`).
6. Adjust route snapshot test to also record (a) CORS enabled flag via metadata, (b) rate limit policy names (not just first), (c) presence of authorization requirement.

Exit Criteria Update: All above tests green; snapshots committed; future diffs fail fast on accidental surface drift.

## Phase 1 – Extract Nested Types
Tasks:
1. Move `AdminAuthOptions` to `Security/Admin/AdminAuthOptions.cs`.
2. Move `AdminRequirement` & `AdminAuthorizationHandler` likewise.
3. Move Admin DTO records (`MappingInput`, `ClaimMappingInput`, `ProviderKeyInput`, `ClientKeysInput`) to `Admin/Dto/` directory.
4. Move `AdminApiHelpers` to `Admin/Helpers/AdminApiHelpers.cs` (internal static) – ensure namespace kept internal to WebAuth.
5. Move `SetConditionalEtag` helper to `Infrastructure/Http/EtagHelpers.cs`.
6. Update `Program.cs` references accordingly.

Acceptance Criteria: No behavior change; tests still green; Program.cs shrinks ~150 lines.

## Phase 2 – Modular Service Registration (Refined Plan)
Status: PARTIAL

Planned Extensions (new or expanded):
1. `AddLocalizationAndMvc()` – Razor Pages, antiforgery filter, localization resources path.
2. `AddPersistence(this IServiceCollection, IConfiguration)` – AuthDbContext + seeder registration.
3. `AddObservability(this IServiceCollection, IConfiguration)` – App Insights (conditional), metrics, audit sink, alert publisher & sampler, clock abstraction.
4. `AddSecurityCore(this IServiceCollection, IConfiguration)` – DPoP validator + replay/nonce stores (conditional Redis upgrade), JAR replay cache, antiforgery, DataProtection persistence, claim mapping, external OIDC handler, client assertion validator, identity provider validator, PAR handler + cleanup, federated logout options binding.
5. `AddOidcProtocolHandlers()` – discovery, authorize, token, logout, userinfo, revocation, introspection handlers; grant handlers; memory cache; JWKS public cache.
6. `AddBackchannel(this IServiceCollection, IConfiguration)` – feature + alert options binding, runtime state, dispatcher, dispatch options, alert diagnostics, expired token cleanup.
7. `AddRateLimitingPolicies(this IServiceCollection, IConfiguration)` – all RL policies (extracted unchanged). Provide optional config binding section `RateLimiting:*` with sensible defaults (fall back to existing constants if absent). Introduce `RateLimitPolicyDefaults` internal static for tests.
8. `AddCorsPolicies(this IServiceCollection, OidcOptions)` – remain strict; only expose `oidc` policy.
9. Aggregator method `AddMrWhoOidcWebAuthServices(this IServiceCollection, IConfiguration)` (optional convenience; deferrable until near Phase 6).

Implementation Guidelines:
- Avoid side effects (no migrations, no seeding) inside Add* methods.
- Keep Redis connection acquisition centralized (`AddSecurityCore`), return `IConnectionMultiplexer` singleton if configured.
- Keep all hosting / pipeline decisions out of service registration.

Acceptance Criteria Update:
- Program.cs loses at least: metrics/audit/alert blocks, antiforgery setup, DataProtection, DPoP/JAR wiring, PAR handler registrations, handler registrations, rate limiting policies, seeder registration, backchannel worker wiring.
- New tests: simple host build confirming all key service interfaces can be resolved (smoke DI test) without actual DB (using in-memory override flag already present).

Deferred Decision: Whether to split `AddSecurityCore` into narrower (`AddDpop`, `AddPar`) – keep combined for now to reduce call noise.

## Phase 3 – Endpoint Mapping Extraction (Refined Plan)
Status: PARTIAL

Current: `MapMrWhoOidcEndpoints()` mixes multiple concerns and embeds migration trigger logic; admin endpoints still inline in `Program.cs`.

Planned Decomposition:
1. `MapOidcProtocolEndpoints(this IEndpointRouteBuilder endpoints)` – all public OIDC endpoints + JWKS variants + external OIDC chaining + federated logout endpoints.
2. `MapAdminApis(this IEndpointRouteBuilder endpoints)` – full admin CRUD + BCL admin endpoints (providers, provider keys, claim mappings, client mappings, client keys, BCL outbox & alerts).
3. `MapBackchannelHealthEndpoint(this IEndpointRouteBuilder endpoints)` – health only.
4. `MapStaticAndRazor(this IEndpointRouteBuilder endpoints)` – Razor + static assets.
5. Remove migration/seeding logic from mapping; relocate to Phase 6 extension `RunMigrationsAndSeedAsync` (or `UseAuthMigrationsAndSeeding` hooking `ApplicationStarted`).

Safety Tasks:
- Add parity test enumerating endpoint -> rate limit policy names before and after extraction (should be identical sets per route).
- Update snapshot test after mapping extraction; re‑approve only if no semantic differences.

Acceptance Criteria Update:
- No inline endpoint lambdas remaining in `Program.cs`.
- `Program.cs` expresses mapping via 4 (or fewer) clearly named calls.
- Migration/seeding logic no longer lives in mapping extensions.

## Phase 4 – Rate Limiting & CORS Consolidation (Refined)
Status: NOT STARTED

Tasks:
1. Implement `AddRateLimitingPolicies` (see Phase 2) using current numeric constants as defaults.
2. Introduce `RateLimitPolicyDefaults` (internal static class) to hold numeric defaults; ensure tests reference only that (single source).
3. Support optional config keys, e.g.:
    - `RateLimiting:Authorize:PermitLimit`
    - `RateLimiting:Token:PermitLimit`
    - `RateLimiting:TokenExchange:PermitLimit`
    - etc.
4. Add tests verifying override behavior and fallback to defaults when missing or invalid.
5. Extract CORS policy into `AddCorsPolicies`; make policy name constant `OidcCorsPolicy = "oidc"`.
6. Add test that endpoints previously requiring CORS still do so and no additional endpoints gained it unintentionally.

Acceptance Criteria: Rate limiting & CORS definitions absent from `Program.cs`; tests guard both policy name stability and override behavior.

## Phase 5 – Optional Endpoint Module Pattern (Re‑Evaluation)
Status: DEFERRED

Decision Gate: Revisit only after Phases 2–4 complete. Success metric for adopting: net reduction in `Program.cs` + easier feature discovery without adding cognitive overhead. If extension method grouping already yields clarity, skip entirely.

Lightweight Alternative (preferred): Provide a single aggregator `AddMrWhoOidcWebAuth(this IServiceCollection, IConfiguration)` & `MapMrWhoOidcWebAuth(this WebApplication)` wrapping individual Add*/Map* calls (no reflection / scanning).

## Phase 6 – Slim Program.cs Finalization (Refined)
Status: NOT STARTED

Updated Task List:
1. Introduce `UseForwardedHeadersAndCertificates()` (optional) OR inline small helper.
2. Introduce `UseMrWhoOidcMiddlewarePipeline()` assembling: forwarded headers, HTTPS redirection (conditional), routing, localization, CORS, authentication, authorization, distributed limiter (if Redis), rate limiter.
3. Extract `--seed` logic to `SeedRunner.RunAsync(args, IServiceProvider root)` in a new `Infrastructure/Seeding` file.
4. Create `RunMigrationsAndSeedAsync(this WebApplication app, bool runKeyWarmup = true)` extension performing: migrate DB, warm signing keys, seed default data. Move ApplicationStarted registration pattern inside this extension (remove from endpoint mapping).
5. Remove remaining inline service registrations & endpoint lambdas from Program; switch to:
   ```csharp
   builder.Services
       .AddLocalizationAndMvc()
       .AddPersistence(builder.Configuration)
       .AddObservability(builder.Configuration)
       .AddSecurityCore(builder.Configuration)
       .AddOidcProtocolHandlers()
       .AddBackchannel(builder.Configuration)
       .AddRateLimitingPolicies(builder.Configuration)
       .AddCorsPolicies(oidcOptions);
   ```
6. After each major removal, update line-count test baseline downward (multi‑PR). Final threshold enforcement: `<=150` lines.
7. Delete transitional `ProgramEndpointMapping` partial.

Acceptance Criteria (Refined): Program.cs ≤150 lines, only orchestration + high-level comments.

## Phase 7 – Documentation & Cleanup (Expanded)
Status: NOT STARTED

Tasks:
1. Update `developer-guide.md`: add section "WebAuth Composition Root" showing before/after diff + extension call ordering rationale.
2. ADR: `ADR-XX-program-slimming.md` (include context, decision, alternatives rejected, consequences, future simplifications).
3. Add XML `<summary>` to each public Add*/Map* extension (internal ones may rely on file header comments).
4. Remove stale comments referencing earlier phases (e.g., "Phase 0 safety refactor step").
5. Update backlog (this file) with final SHAs, mark completion.
6. Optional: Add `ArchitectureTests.cs` enforcing: no direct `.MapGet/Post/...` invocations in `Program.cs` (regex or Roslyn) after Phase 6.

Acceptance Criteria: Docs & ADR merged in same PR or successive small PRs; build passes with XML doc warnings (if any) addressed.

## Cross-Cutting Tasks & Enhancements (Updated)
1. `InternalsVisibleTo` for UnitTests (consider enabling now to test internal rate limiting extension logic & JWKS cache helpers directly).
2. Architecture test (post Phase 6) verifying zero `app.Map` calls in `Program.cs` except allowed comment markers.
3. Optional Roslyn analyzer (future) to warn when `WebApplication` extension mapping is performed outside sanctioned extension classes.
4. Lightweight perf smoke: measure cold start (host build + first discovery request) before & after; ensure <5% regression.
5. Consider adding `ILogger` scopes around migration & seeding extension for observability; test ensures log category present.

## Risk Assessment & Mitigations
| Risk | Impact | Mitigation |
|------|--------|-----------|
| Route changes / typos | Breaking clients & tests | Route snapshot test (Phase 0) |
| Lost rate limiting / auth policy attributes | Security/perf regression | Endpoint attribute parity tests; manual code review checklist |
| Subtle DI lifetime changes | Runtime errors / memory leaks | Keep lifetimes identical; add smoke test starting host verifying service resolution |
| Migration ordering & seeding race | Startup failure | Extracted migration extension retains original logic & ordering; add integration test verifying a fresh DB seeds properly |
| Redis conditional wiring mistakes | Rate limiting / DPoP replay issues | Add test with in-memory vs simulated redis config toggles |
| Over-modularization complexity | Slower onboarding | Keep pattern minimal; avoid deep folder nesting |

## Aggregate Acceptance Criteria (Unchanged + Clarified)
- All existing unit/integration tests continue to pass throughout refactor PR series.
- Snapshot & safety tests enhanced (Phase 0 augmented) and stable.
- Program.cs eventually ≤150 lines (stretch 120) with only orchestration.
- Endpoint list (patterns + verbs) unchanged unless explicitly versioned and snapshot updated deliberately.
- CORS policy name `oidc` retained.
- Rate limiting policy names stable (`rl-*`).
- Admin policy name remains `admin`.
- Migration + seeding ordering unchanged (verified by integration test with blank DB + seeding flag).
- Cold start performance not degraded >5% (informational check).

## Updated Incremental PR Breakdown (Forward Looking)
| PR | Objective | Key Diff Impact | Expected Line Reduction |
|----|-----------|-----------------|-------------------------|
| 1 (done) | Initial safety net (partial) | Tests added (snapshot, line count) | N/A |
| 2 (done) | Extract admin types/helpers | New Security/Admin folders | ~ -120 |
| 3 (next) | Finish Service Registration (Phase 2) | Add 6–8 extensions, remove large blocks | -250 to -300 |
| 4 | Endpoint Mapping split (Phase 3) | Add Map* extensions, remove inline admin endpoints | -180 to -220 |
| 5 | Rate Limiting + CORS consolidation (Phase 4) | Add RL/CORS extensions + tests | -130 |
| 6 | Middleware + migrations/seeding extensions (Phase 6 early) | Add Use*/Run* extensions | -120 |
| 7 | Final slimming + remove transitional artifacts | Delete old partials; adjust baseline test | -80 (reach target) |
| 8 | Docs + ADR + architecture test | New docs/ADR/test | N/A |
| 9 (optional) | Module aggregator (if still desired) | Aggregator methods | Minimal |

Total projected reduction: ~770–820 lines (achieving ≤150).

Each PR should include: updated changelog (if maintained), summary of removed lines from Program.cs, confirmation of unchanged route snapshot diff.

## Checklists
Refactor PR Checklist (Per PR Gate):
- [ ] All tests green (including augmented safety tests) 
- [ ] Route snapshot diff empty (or approved & snapshot updated intentionally)
- [ ] No unauthorized direct `app.Map*` calls added to `Program.cs`
- [ ] Service lifetimes unchanged (scoped vs singleton parity) 
- [ ] Rate limit & CORS policy names unchanged
- [ ] Added XML summary comments for new public extensions
- [ ] Program.cs line count decreased or unchanged (never increased) 
- [ ] Migration & seeding behavior unchanged (verified if touched)

## Open Questions / Deferred Items (Reviewed)
1. Unified mega-options object? (Still defer – explicit per-feature binding clearer.)
2. Endpoint module reflection-based discovery? (Defer; may skip entirely.)
3. Source-generated endpoint manifest? (Future performance/compile-time safety optimization.)
4. Separate Admin API project (`MrWhoOidc.WebAdmin`)? (Out of scope; reconsider once code volume of admin endpoints grows again.)
5. Introduce a minimal `IStartupFilter` to enforce pipeline ordering? (Unnecessary now; extension method suffices.)

## Quick Preview of Desired Final Program.cs Skeleton (Still Target)
```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services
    .AddBoundOptions(builder.Configuration) // central optional binder
    .AddLocalizationAndMvc()
    .AddAuthenticationAndAuthorization()
    .AddPersistenceAndSeeding(builder.Configuration)
    .AddObservabilityServices(builder.Configuration)
    .AddSecurityServices(builder.Configuration)
    .AddBackchannelServices(builder.Configuration)
    .AddOidcCoreServices()
    .AddCorsPolicies(builder.Configuration)
    .AddRateLimitingPolicies(builder.Configuration);

var app = builder.Build();
app.UseStandardMiddlewarePipeline();
app.MapStaticAndRazor();
app.MapOidcProtocolEndpoints();
app.MapAdminApis();
app.MapBackchannelHealthEndpoint();
await app.RunMigrationsAndSeedAsync(args); // handles --seed and warm-up
app.Run();
```

---
Maintainer Notes:
- When a phase completes, append a bullet here with commit SHA(s), e.g.: `Phase 2 Complete: abc1234 (service registration extraction)`.
- If snapshot changes are intentional, include diff summary in PR description for reviewer clarity.

Progress Log (to fill):
- Phase 1 Complete: <commit-sha>
- Phase 2 (partial): <commit-sha(s)> – Added Authentication/Authorization extension only.

