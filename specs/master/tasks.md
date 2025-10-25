# Tasks: License Key System

**Input**: Design documents from `/specs/master/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Tests**: Include only where explicitly listed per user story.

**Organization**: Tasks grouped by user story so each story is independently implementable and testable.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Prepare filesystem scaffolding so later phases can add code without friction.

- [X] T001 Create Licensing folder structure (Entities/Models/Services/Repositories/Validators/Options) under MrWhoOidc.Auth/Licensing/
- [X] T002 Create admin Razor page folder skeleton under MrWhoOidc.WebAuth/Pages/Admin/License/
- [X] T003 Create licensing test folder scaffold under MrWhoOidc.UnitTests/Licensing/

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Shared configuration and DI hooks required before implementing any user story.

- [X] T004 Introduce licensing options record and binder in MrWhoOidc.Auth/Licensing/Options/LicensingOptions.cs and wire configuration in MrWhoOidc.WebAuth/Program.cs
- [X] T005 Add stubbed licensing DI extension in MrWhoOidc.Auth/Licensing/ServiceCollectionExtensions.cs returning IServiceCollection
- [X] T006 Seed licensing configuration placeholders (public key, grace period) in MrWhoOidc.WebAuth/appsettings.json and MrWhoOidc.WebAuth/appsettings.Development.json

---

## Phase 3: User Story 1 - Cryptographic License Installation (Priority: P1) 🎯 MVP

**Goal**: Persist licenses, validate ECDSA JWS keys, and expose install/revoke flows in the domain layer.

**Independent Test**: Install a signed license via domain service and verify cached lookup plus hourly validation run without touching WebAuth UI.

### Implementation Tasks (US1)

- [X] T007 [P] [US1] Add License aggregate entities (License, LicenseHistoryEntry, FeatureUsageMetric, LicenseLimit) per data model in MrWhoOidc.Auth/Licensing/Entities/
- [X] T008 [US1] Extend AuthDbContext and OnModelCreating to register licensing DbSets and ConfigureLicenseEntities in MrWhoOidc.Auth/Persistence/AuthDbContext.cs
- [X] T009 [P] [US1] Author fluent configurations for licensing entities in MrWhoOidc.Auth/Persistence/Configurations/
- [X] T010 [US1] Generate AddLicenseSystem migration and snapshot under MrWhoOidc.Auth/Persistence/Migrations/
- [X] T011 [US1] Implement ECDSA JWS LicenseValidator with signature + claim validation in MrWhoOidc.Auth/Licensing/Validators/LicenseValidator.cs
- [X] T012 [P] [US1] Create LicenseInfo, LicenseValidationResult, and LicenseTier models in MrWhoOidc.Auth/Licensing/Models/
- [X] T013 [US1] Implement EF-backed LicenseRepository with history persistence in MrWhoOidc.Auth/Licensing/Repositories/LicenseRepository.cs
- [X] T014 [US1] Implement LicenseService (install, validate, revoke, cache refresh) in MrWhoOidc.Auth/Licensing/Services/LicenseService.cs
- [X] T015 [US1] Register licensing services and hosted validator in MrWhoOidc.Auth/Licensing/ServiceCollectionExtensions.cs and add background worker class MrWhoOidc.WebAuth/Background/LicenseValidationWorker.cs
- [X] T016 [P] [US1] Add unit coverage for validator/service flows in MrWhoOidc.UnitTests/Licensing/LicenseServiceTests.cs

---

## Phase 4: User Story 2 - Feature Gating & Limit Enforcement (Priority: P1)

**Goal**: Enforce tier-specific features and tenant/user limits across core flows.

**Independent Test**: With a Professional license, attempt to provision a 6th tenant and verify the request fails while basic OIDC endpoints still succeed.

### Implementation Tasks (US2)

- [X] T017 [P] [US2] Add FeatureFlags and UsageLimitInfo models per spec in MrWhoOidc.Auth/Licensing/Models/
- [X] T018 [US2] Implement FeatureService to resolve enabled features via LicenseInfo in MrWhoOidc.Auth/Licensing/Services/FeatureService.cs
- [X] T019 [US2] Implement LimitService enforcing numeric limits in MrWhoOidc.Auth/Licensing/Services/LimitService.cs
- [X] T020 [US2] Add FeatureGatingMiddleware enforcing disabled features in MrWhoOidc.WebAuth/Middleware/FeatureGatingMiddleware.cs
- [X] T021 [US2] Inject license limit checks into tenant and authorization flows in MrWhoOidc.Auth/Services/TenantService.cs and MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs
- [X] T022 [US2] Update pipeline wiring and advanced endpoint guards in MrWhoOidc.WebAuth/Program.cs to use feature gating middleware and services
- [X] T023 [P] [US2] Add unit tests for feature gating and limit enforcement scenarios in MrWhoOidc.UnitTests/Licensing/FeatureGatingTests.cs

---

## Phase 5: User Story 3 - Admin License Management Surface (Priority: P2)

**Goal**: Provide admin APIs and UI for installing licenses, viewing status, and reviewing history.

**Independent Test**: Through admin API, upload a license, view it in the UI, and confirm history audit entry appears without enabling analytics endpoints.

### Implementation Tasks (US3)

- [X] T024 [P] [US3] Implement license management minimal APIs per contract in MrWhoOidc.WebAuth/Admin/Api/LicenseEndpoints.cs
- [X] T025 [US3] Add admin DTOs and mapping helpers in MrWhoOidc.WebAuth/Admin/Api/LicenseDtos.cs
- [X] T026 [US3] Register license API endpoints and policies in MrWhoOidc.WebAuth/Program.cs
- [X] T027 [US3] Build license admin Razor pages (Index, Install, History) under MrWhoOidc.WebAuth/Pages/Admin/License/
- [X] T028 [US3] Surface license navigation links within admin sidebar in MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml
- [X] T029 [US3] Implement Razor page models orchestrating services in MrWhoOidc.WebAuth/Pages/Admin/License/Index.cshtml.cs and companions
- [X] T030 [P] [US3] Add admin API integration tests covering install/validate/history in MrWhoOidc.UnitTests/Licensing/LicenseAdminApiTests.cs

---

## Phase 6: User Story 4 - License Analytics & Tier Insights (Priority: P3)

**Goal**: Deliver license usage analytics, limits dashboards, and tier reference endpoints.

**Independent Test**: Query analytics endpoints to fetch usage metrics after recording feature hits and confirm UI cards render aggregated data.

### Implementation Tasks (US4)

- [X] T031 [P] [US4] Implement FeatureUsageRepository persisting metrics in MrWhoOidc.Auth/Licensing/Repositories/FeatureUsageRepository.cs
- [X] T032 [US4] Add LicenseAnalyticsService aggregating usage/limits in MrWhoOidc.Auth/Licensing/Services/LicenseAnalyticsService.cs
- [X] T033 [US4] Extend license endpoints with usage, limits, and tiers routes in MrWhoOidc.WebAuth/Admin/Api/LicenseEndpoints.cs
- [X] T034 [US4] Feed analytics data into admin dashboard widgets in MrWhoOidc.WebAuth/Pages/Admin/License/Index.cshtml
- [X] T035 [US4] Record feature usage at critical entry points (e.g., DPoP/JAR handlers) in MrWhoOidc.WebAuth/Handlers/TokenHandler.cs and related services
- [X] T036 [P] [US4] Add analytics service unit tests validating aggregation logic in MrWhoOidc.UnitTests/Licensing/LicenseAnalyticsServiceTests.cs

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Harden observability, documentation, and ops readiness across stories.

- [X] T037 Add structured logging and OTel metrics for licensing flows in MrWhoOidc.Auth/Licensing/Services/LicenseService.cs and MrWhoOidc.ServiceDefaults/Observability/
- [ ] T038 Document license installation and feature gating workflows in docs/admin-guide.md and docs/developer-guide.md
- [ ] T039 Provide sample license configuration guidance in MrWhoOidc.WebAuth/appsettings.Sample.json (create if missing)

---

## Dependencies & Execution Order

- Setup (Phase 1) → Foundational (Phase 2) → User Stories (Phases 3-6) → Polish (Phase 7)
- User Story priority order: US1 (P1) → US2 (P1) → US3 (P2) → US4 (P3)
- US2 depends on US1 completion; US3 depends on US1; US4 depends on US1 and US2 data collection hooks

---

## Parallel Execution Examples

- **US1**: Run T007, T009, T012 in parallel directories before tackling T014
- **US2**: Execute T017 and T018 concurrently, then proceed to T021
- **US3**: Implement API layer (T024-T026) while UI team handles Razor pages (T027-T029)
- **US4**: Build repository (T031) while tests (T036) scaffold against mocked data

---

## Implementation Strategy

1. Ship MVP by completing US1 end-to-end, enabling license installation and background validation.
2. Layer in US2 to enforce feature gating so unsupported tiers cannot access premium capabilities.
3. Deliver admin surface (US3) for operational control, then analytics (US4) for insights.
4. Finish with polish tasks to document, monitor, and provide operator guidance.
