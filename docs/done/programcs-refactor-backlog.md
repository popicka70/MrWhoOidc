# Program.cs Refactor Backlog

> Progress Assessment Date: 2025-09-27

This document reflects the repository state (branch `UserPage`) as of the assessment date and refines the remaining plan toward a slim `Program.cs` (<=150 lines). A phased approach is retained, but statuses and next actions are continuously pruned for velocity and safety.

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
| 2 | Service Registration Modularization | PARTIAL | Extracted: `AddMrWhoOidcAuthAndAdmin`, `AddMrWhoOidcObservability`, `AddMrWhoOidcPersistenceAndCore` (covers persistence, seeder, protocol handlers, validators, JWKS caches). Still inline: security core (DPoP/JAR/Redis), DataProtection + antiforgery, localization/Razor bundling, background hosted services group, rate limiting, CORS, backchannel feature wiring. Duplicate inline persistence/core registrations removed from Program.cs. |
| 3 | Endpoint Mapping Modularization | PARTIAL | Core OIDC + some infra moved to `MapMrWhoOidcEndpoints` (internal). Admin CRUD, backchannel health endpoint, and BCL admin endpoints remain inline in `Program.cs`. Migration/seeding logic currently (mis)located inside endpoint mapping extension. |
| 4 | Rate Limiting & CORS Consolidation | NOT STARTED | Policies still registered inline. No central defaults object or config override tests. CORS logic still inline. |
| 5 | Feature Module Interface (Optional) | DEFER (TBD) | Value not yet justified—will re‑evaluate after core extraction (Phases 2–4). |
| 6 | Slim Program.cs | NOT STARTED | Current line count ≈918 (baseline test uses 1036). Major blocks remain. |
| 7 | Cleanup & Docs | NOT STARTED | Docs not yet updated; ADR absent. |

### Current Program.cs Size Trend
- Original (estimated): ~1100 lines
- After Phase 1 + partial Phases 2/3 (previous snapshot): 918 lines
- Current (post minor cleanups & extractions reviewed 2025-09-27): 855 lines (measured total file lines)
- Target after completing remaining Phase 2 (service modularization) + Phase 3 (endpoint split): ~520–560 lines
- Target after Phase 4 (RL + CORS) + early Phase 6 (migrations/seeding + pipeline): ~300–340 lines
- Final target (Phase 6 completion): <=150 (stretch 120)

Action: Update any existing line-count snapshot test baseline to 855 before next reduction PR (then ratchet downward each PR; never increase).

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

## Phase 2 – Modular Service Registration (Refined Plan – Updated Status)
Status: PARTIAL (progress improved)

Already Extracted (present in code):
- `AddMrWhoOidcObservability` (telemetry, audit, alerts)
- `AddMrWhoOidcAuthAndAdmin` (auth schemes + admin policy)
- `AddMrWhoOidcPersistenceAndCore` (DbContext + core protocol handlers + JWKS caches)
- `AddMrWhoOidcSecurityCore` (DPoP, JAR, DataProtection, antiforgery, localization hookup pieces, external OIDC wiring, PAR, federated logout options binding)
- `AddMrWhoOidcBackgroundAndBackchannel` (dispatcher + background workers + runtime state)

Still Inline / Outstanding for Phase 2 Completion:
1. Razor Pages + MVC + antiforgery + localization bundling not unified (AddRazorPages/AddMvc inline). Create `AddLocalizationAndMvc()` (or `AddPresentationLayer()`).
2. CORS policy defined inline – move to `AddCorsPolicies()` (strict; maintain name `oidc`).
3. Rate limiting policies inline – move to `AddRateLimitingPolicies()` (may slide to Phase 4 if needed, but earlier extraction simplifies endpoint split).
4. Redis connection creation currently inline in Program; consider relocating into `AddMrWhoOidcSecurityCore` (with safe early connect + logging) OR a thin `AddRedisIfConfigured` helper returning `IConnectionMultiplexer?`.
5. Seed/migration logic remains inline (belongs to Phase 6 but keep noted).

Adjustments vs Original Plan:
- Combined extensions already cover several originally distinct conceptual Add* methods (OK – avoid churn).
- Ensure each existing extension gains XML `<summary>` doc comments before closing Phase 2.

Acceptance Criteria (Revised for Phase 2 Close):
- Program.cs no longer contains: AddRazorPages/AddMvc blocks, CORS block, rate limiter definition, Redis connection logic.
- All extracted extensions idempotent; no side-effects beyond registrations.
- DI smoke test added validating resolution of core interfaces (KeyStore, TokenService, TokenValidator, Backchannel dispatcher) under minimal config.
- Line count reduction >= ~140 lines upon completion of above.

Deferred Decision: Keep combined security extension; revisit split only if testability suffers.

## Phase 3 – Endpoint Mapping Extraction (Refined Plan – Updated Status)
Status: PARTIAL

Current State:
- `MapMrWhoOidcEndpoints()` exists (protocol/public endpoints) – admin CRUD + BCL admin + health remain inline.
- Inline admin endpoints represent the majority of remaining lambda bulk.
- Migration/`--seed` logic is still inline (not inside mapping extension – good; earlier doc assumed it migrated into mapping; corrected).

Planned Decomposition (unchanged with clarifications):
1. `MapOidcProtocolEndpoints()` (rename / carve from existing `MapMrWhoOidcEndpoints` retaining route patterns & metadata)
2. `MapAdminApis()` – all admin+CRUD+BCL admin endpoints
3. `MapBackchannelHealthEndpoint()` – just `/health/backchannel`
4. `MapStaticAndRazor()` – static + Razor pages (called conditionally based on Testing flag)
5. Delete transitional `ProgramEndpointMapping` partial once tests target new methods

Safety / Test Tasks (augment Phase 0 items):
- Endpoint → rate limiting policy parity test (capture before extraction while still inline)
- Snapshot diff review after extraction; only expected movement: handler method names in metadata (ignore) – patterns/verbs unchanged
- Add test asserting `/health/backchannel` contract (moved earlier into Phase 0 list) BEFORE extraction for golden baseline

Acceptance Criteria:
- Zero inline `admin.Map*` or `app.MapGet/Post/...` (except initial creation of groups if any minimal remainder; prefer none) in `Program.cs`
- Program uses only: `app.MapStaticAndRazor(); app.MapOidcProtocolEndpoints(); app.MapAdminApis(); app.MapBackchannelHealthEndpoint();`
- Line count reduction expected: ~230–260 lines

## Phase 4 – Rate Limiting & CORS Consolidation (Refined – Slight Reorder Possible)
Status: NOT STARTED (but may partially shift earlier to finish Phase 2 closure)

Tasks (ordered if executed as separate PR, else batch with late Phase 2):
1. Implement `AddRateLimitingPolicies` (current numeric constants become `RateLimitPolicyDefaults`).
2. Config override binding (`RateLimiting:*:PermitLimit`, optional `WindowSeconds`). Validate & fall back to defaults on invalid input.
3. Introduce unit test: enumerates defined policy names EXACT match set; override test changes one limit via config and asserts effective permit limit.
4. Extract CORS policy to `AddCorsPolicies` with internal constant `OidcCorsPolicy = "oidc"` (public exposure only if needed by tests).
5. Add test enumerating endpoints with CORS metadata before & after extraction (no delta).

Acceptance Criteria: No rate limiter or CORS configuration inline in `Program.cs`; tests enforce policy name stability & override semantics.

## Phase 5 – Optional Endpoint Module Pattern (Re‑Evaluation)
Status: DEFERRED

Decision Gate: Revisit only after Phases 2–4 complete. Success metric for adopting: net reduction in `Program.cs` + easier feature discovery without adding cognitive overhead. If extension method grouping already yields clarity, skip entirely.

Lightweight Alternative (preferred): Provide a single aggregator `AddMrWhoOidcWebAuth(this IServiceCollection, IConfiguration)` & `MapMrWhoOidcWebAuth(this WebApplication)` wrapping individual Add*/Map* calls (no reflection / scanning).

## Phase 6 – Slim Program.cs Finalization (Refined – Updated)
Status: NOT STARTED

Updated Task List:
1. Introduce `UseMrWhoOidcMiddlewarePipeline()` assembling: forwarded headers, optional HTTPS redirect, routing, localization, CORS, authentication, authorization, distributed limiter (if Redis), rate limiter (single call replaces current inline block).
2. Extract forwarded headers + certificate forwarding into either above pipeline or a dedicated `UseForwarding()` helper (decide based on clarity).
3. Extract `--seed` logic into `SeedRunner.RunAsync(args, IServiceProvider root)` (invoked prior to host run) and `RunMigrationsAndSeedAsync()` (ApplicationStarted hook for standard startup path – includes key warm-up + seed if configured).
4. After Phases 2–4, collapse service registration into visually ordered fluent chain (line-break style audited to stay concise): localization/mvc, persistence, security, observability, protocol, backchannel, CORS, rate limiting.
5. Remove any lingering diagnostic test flags (InlineAuthCoreSafety/DiagnoseAuthCore) once stability proven & tests cover DI resolution; if retained, wrap behind DEBUG conditional.
6. Delete `ProgramEndpointMapping` partial.
7. Ensure final file ≤150 lines (target 120) – add guard test enforcing upper bound.

Acceptance Criteria: Program.cs expresses only: builder creation, chained Add* calls, pipeline `Use*` call, Map* calls, migration/seed extension invocation, `app.Run()`. Nothing else.

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

## Updated Incremental PR Breakdown (Forward Looking – Revised After 855 Baseline)
| PR | Objective | Key Diff Impact | Estimated Line Reduction (from 855) |
|----|-----------|-----------------|------------------------------------|
| 1 (done) | Initial safety net (partial) | Snapshot + line count tests | N/A |
| 2 (done) | Extract admin types/helpers | Admin DTOs/helpers moved | - ~150 (historical) |
| 3 (next) | Phase 0 test augmentation | Add missing safety tests (no major refactor) | ~0 (guardrail only) |
| 4 | Finish outstanding Phase 2 (AddLocalizationAndMvc, move Redis/CORS/RL if included) | New Add* extensions; remove inline blocks | -120 to -160 |
| 5 | Endpoint Mapping split (Phase 3) | Map* extensions; remove admin/health lambdas inline | -230 to -260 |
| 6 | Rate Limiting + CORS (if not fully in PR4) | RL + CORS extraction + tests | -90 to -110 |
| 7 | Middleware pipeline + seeding extensions (Phase 6 partial) | Use* + SeedRunner | -110 to -130 |
| 8 | Final slimming & cleanup (remove diagnostics, delete transitional partial) | Minimal structural changes | -40 to -60 (reach ≤150) |
| 9 | Docs + ADR + architecture test | New docs + analyzer/test | N/A |
| 10 (optional) | Aggregator Add/Map wrappers | Convenience only | ~0 |

Projected remaining reduction needed: ~705 lines → feasible within PRs 4–8.

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

Progress Log (to fill / update with real SHAs during PRs):
- Phase 1 Complete: <commit-sha>
- Phase 2 (partial): <commit-sha(s)> – Added Auth/Admin, Observability, Persistence/Core, SecurityCore, Background/Backchannel service extensions; removed duplicate inline registrations.
- Current Baseline Line Count: 855 (Program.cs) – snapshot updated pending next PR.

Next Immediate Actions (Recommended Order):
1. Augment safety tests (Phase 0 gaps 1–6) BEFORE further extraction.
2. Introduce `AddLocalizationAndMvc` and move Razor/MVC/antiforgery + optional localization there.
3. Decide whether to move Redis connect logic into security extension (simplifies Program) – add logging & resilience.
4. Extract CORS + (optionally) Rate Limiting in same PR or leave RL for dedicated PR (balance between churn vs test clarity).
5. Commit snapshot updates only when no semantic change; otherwise accompany with explicit diff rationale.

Decision Note: If PR size risk emerges, split (2) and (3/4) into two PRs.

