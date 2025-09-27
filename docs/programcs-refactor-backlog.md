# Program.cs Refactor Backlog

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

## High-Level Epics & Phases
| Phase | Epic | Goal | Output |
|-------|------|------|--------|
| 0 | Safety Nets | Freeze observable behavior | Tests + route manifest snapshot |
| 1 | Extract Nested Types | Move inline admin types to dedicated files | New files under `Security/` or `Admin/` namespace |
| 2 | Service Registration Modularization | Break out Add* extension methods by concern | `ServiceCollectionExtensions/*` |
| 3 | Endpoint Mapping Modularization | Group endpoints into `EndpointRouteBuilder` extensions | `EndpointMappings/*` |
| 4 | Rate Limiting & CORS Consolidation | Single method to register policies, optional config section | `AddOidcRateLimiting()`, `AddOidcCors()` |
| 5 | Feature Module Interface (Optional) | Introduce `IEndpointModule` pattern for auto-discovery | Lightweight interface + scan registration |
| 6 | Slim Program.cs | Replace large blocks with extension calls | Final orchestrated file |
| 7 | Cleanup & Docs | Update developer & admin guide + remove obsolete helpers | Updated docs + ADR |

## Phase 0 – Safety Nets (Highest Priority)
Tasks:
1. Add unit test enumerating all current route templates + HTTP verbs + applied rate limit policy names & auth requirements (use reflection over endpoint data sources) – store snapshot JSON under `MrWhoOidc.UnitTests/Baselines/routes.json`.
2. Add test verifying key OIDC endpoints respond (discovery, jwks, authorize (302), token (400 on empty), userinfo (401)).
3. Add test asserting admin policy name is `admin` and rate limiting policy names list unchanged.
4. Add test that `Program.cs` line count > 500 initially (guards that we truly reduce it; will adjust threshold in Phase 6).
5. (Optional) Introduce simple golden file for `/health/backchannel` response shape keys.

Acceptance Criteria: All safety tests green pre-refactor; CI baseline created.

## Phase 1 – Extract Nested Types
Tasks:
1. Move `AdminAuthOptions` to `Security/Admin/AdminAuthOptions.cs`.
2. Move `AdminRequirement` & `AdminAuthorizationHandler` likewise.
3. Move Admin DTO records (`MappingInput`, `ClaimMappingInput`, `ProviderKeyInput`, `ClientKeysInput`) to `Admin/Dto/` directory.
4. Move `AdminApiHelpers` to `Admin/Helpers/AdminApiHelpers.cs` (internal static) – ensure namespace kept internal to WebAuth.
5. Move `SetConditionalEtag` helper to `Infrastructure/Http/EtagHelpers.cs`.
6. Update `Program.cs` references accordingly.

Acceptance Criteria: No behavior change; tests still green; Program.cs shrinks ~150 lines.

## Phase 2 – Modular Service Registration
Create extension classes under `MrWhoOidc.WebAuth/Extensions` (or `DependencyInjection` for symmetry with Auth project) with focused responsibilities:
1. `AddObservabilityServices` (metrics, audit sink, alert publisher, alert sampler hosted service, metrics recorder safety fallback).
2. `AddSecurityServices` (DPoP + JAR replay + antiforgery + DataProtection + claim mapping + external OIDC + PAR + validators + identity provider validator).
3. `AddBackchannelServices` (BCL dispatcher, runtime state, feature options, alert options binding if not already in observability).
4. `AddOidcCoreServices` (authorize/token/logout handlers, grant handlers, discovery, userinfo, introspection, revocation, jwks cache, memory cache, federated logout options).
5. `AddPersistenceAndSeeding` (Auth persistence, seeder registration; migration/seeding will still run in Program until Phase 6 redesign).
6. `AddAuthenticationAndAuthorization` (cookie schemes, admin policy + handler registration).
7. `AddLocalizationAndMvc` (RazorPages, antiforgery filter, localization config).
8. `AddCorsPolicies` (CORS policy named `oidc`).
9. `AddRateLimitingPolicies` (all RL policies + redis global limiter wiring on supplied connection multiplexer).

Implementation Notes:
- Each method returns `IServiceCollection` for chaining.
- Keep option binding at the top-level or move into relevant method (decide consistency: prefer binding in Program + passing via config? -> choose: binding remains Program for clarity except where logically part of a feature e.g., Backchannel options).

Acceptance Criteria: Program.cs reduced by another ~250–300 lines; tests green; new extension methods covered by minimal service registration smoke tests.

## Phase 3 – Endpoint Mapping Extraction
Create static classes with `IEndpointRouteBuilder` extension methods (OR `WebApplication` extensions):
1. `MapOidcProtocolEndpoints` – discovery, jwks (and conditional variants), authorize, token (+ OPTIONS), revoke, userinfo (+ OPTIONS), introspect, par (+ OPTIONS), external OIDC chaining, logout endpoints.
2. `MapAdminApis` – all admin groups & CRUD operations (providers, provider keys, claim mappings, client-provider mappings, client keys, BCL outbox, alerts).
3. `MapBackchannelHealthEndpoint` – `/health/backchannel`.
4. (Optional) `MapStaticAndRazor` – razor pages + static assets.

Design: Accept required services through DI inside endpoint lambdas as done currently; avoid capturing external state. Preserve rate limiting & authorization decorations.

Acceptance Criteria: Program.cs endpoint section replaced by 3–4 concise calls; route snapshot test unchanged.

## Phase 4 – Rate Limiting & CORS Consolidation
Tasks:
1. Ensure `AddRateLimitingPolicies` reads optional configuration (e.g., `RateLimiting:TokenPermitLimit`) with sane defaults.
2. Introduce object `RateLimitPolicyDefaults` for central tuning & tests.
3. Add test verifying custom config overrides default.
4. Confirm RL names remain identical.

Acceptance Criteria: Single location to change limits; no behavior changes without config modifications.

## Phase 5 – Optional Endpoint Module Pattern
If desired for future features / plugin style.
Interface: `public interface IEndpointModule { void AddServices(IServiceCollection services, IConfiguration config); void MapEndpoints(IEndpointRouteBuilder endpoints); }`
Implementation Plan:
1. Create modules for Admin, Core OIDC, Backchannel, External OIDC.
2. Add scanning extension `AddEndpointModules(this IServiceCollection, Assembly assembly)` and `MapEndpointModules(this WebApplication app)`.
3. Migrate previous extension mapping methods into modules (or keep mapping methods as wrappers delegating to modules to minimize churn).
4. Evaluate complexity vs value; if it adds noise, skip or defer.

Acceptance Criteria: (If adopted) Program.cs loops through discovered modules, no manual Map calls for feature sets.

## Phase 6 – Slim Program.cs Finalization
Tasks:
1. Remove residual helper code & inline logic now moved.
2. Introduce `app.RunMigrationsAndSeedAsync()` extension encapsulating ApplicationStarted logic.
3. Keep `--seed` branch but delegate to `SeedRunner.RunAsync(args)` static helper to avoid duplication.
4. Introduce environment pipeline extension `UseStandardMiddlewarePipeline(this WebApplication app)` bundling forwarded headers, localization, CORS, auth, rate limiting middleware ordering.
5. Replace long sequence with method calls; add comment block referencing docs for deeper detail.
6. Adjust line-count test threshold to enforce <= 150 lines.

Acceptance Criteria: Program.cs readability improvement validated in PR review; tests green; diff shows removal not addition of complexity.

## Phase 7 – Documentation & Cleanup
Tasks:
1. Update `docs/developer-guide.md` with new extension method / module pattern.
2. Add ADR (e.g., `docs/adr/ADR-XX-program-slimming.md`) summarizing rationale & trade-offs (why not adopt full vertical slice framework, why minimal custom pattern).
3. Remove any obsolete comments in extracted files.
4. Ensure XML docs or summaries on public extension methods.

Acceptance Criteria: Docs reflect new structure; onboarding instructions reference extension names.

## Cross-Cutting Tasks & Enhancements
1. Introduce `InternalsVisibleTo` for unit tests if needed to test internal helpers (ETag, JWKS status) directly.
2. Add analyzer / Roslyn code-fix (optional future) to prevent adding endpoints directly in Program.
3. Add architecture test ensuring `Program.cs` has no `Map` method invocations outside a curated allow list (post Phase 6).

## Risk Assessment & Mitigations
| Risk | Impact | Mitigation |
|------|--------|-----------|
| Route changes / typos | Breaking clients & tests | Route snapshot test (Phase 0) |
| Lost rate limiting / auth policy attributes | Security/perf regression | Endpoint attribute parity tests; manual code review checklist |
| Subtle DI lifetime changes | Runtime errors / memory leaks | Keep lifetimes identical; add smoke test starting host verifying service resolution |
| Migration ordering & seeding race | Startup failure | Extracted migration extension retains original logic & ordering; add integration test verifying a fresh DB seeds properly |
| Redis conditional wiring mistakes | Rate limiting / DPoP replay issues | Add test with in-memory vs simulated redis config toggles |
| Over-modularization complexity | Slower onboarding | Keep pattern minimal; avoid deep folder nesting |

## Acceptance Criteria (Aggregate)
- All existing unit/integration tests pass unchanged.
- Newly added route snapshot & safety tests pass.
- Program.cs line count threshold enforced (<= 150 lines) after Phase 6.
- No change in externally observable endpoint list, HTTP verbs, CORS policy name, rate limiting policy names, or authorization policy names.
- Startup (cold) latency not measurably worse (>5% baseline) – optional perf check.

## Suggested Incremental PR Breakdown
1. PR #1: Safety tests only.
2. PR #2: Extract nested admin/security types.
3. PR #3: Service registration extensions (no endpoints yet).
4. PR #4: Endpoint mapping extraction.
5. PR #5: Rate limiting & CORS consolidation.
6. PR #6: Optional module interface (or skip if decided unnecessary).
7. PR #7: Final slimming & migration/seed extensions + adjust line-count test.
8. PR #8: Documentation & ADR.

Each PR should include: updated changelog (if maintained), summary of removed lines from Program.cs, confirmation of unchanged route snapshot diff.

## Checklists
Refactor PR Checklist:
- [ ] Safety tests untouched & green
- [ ] Route snapshot diff = empty
- [ ] No new top-level `Map` calls added directly to Program.cs (after Phase 4)
- [ ] Lifetimes of moved services preserved
- [ ] Public route names & rate limit policy names unchanged
- [ ] Added xml summary where introducing new public extensions
- [ ] Program.cs line count trending downward

## Open Questions / Deferred Items
1. Do we want to unify option binding into a single `Configure<CompositeOptions>` pattern? (Defer – clarity may suffer.)
2. Should endpoint modules be discovered via assembly scanning or explicit registration? (Prototype explicit first.)
3. Introduce source-generated endpoint manifest for compile-time validation? (Future optimization.)
4. Consider splitting Admin API into its own project (`MrWhoOidc.WebAdmin`) later – out of scope now.

## Quick Preview of Desired Final Program.cs Skeleton
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
Maintainer Notes: Keep this backlog updated; mark phases complete with commit SHAs. Adjust tasks if emergent complexity appears during extraction.
