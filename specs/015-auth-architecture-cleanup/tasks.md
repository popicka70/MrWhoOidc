# Tasks: Auth Architecture Cleanup

**Input**: Design documents from `/specs/015-auth-architecture-cleanup/`
**Prerequisites**: plan.md ✓, spec.md ✓, research.md ✓, data-model.md ✓, contracts/ ✓

**Tests**: Tests are REQUIRED per FR-024 - all new services MUST have corresponding unit tests with at least 80% coverage.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`
- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions
- **Auth domain**: `MrWhoOidc.Auth/`
- **HTTP surface**: `MrWhoOidc.WebAuth/`
- **Unit tests**: `MrWhoOidc.UnitTests/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create directory structure and ensure baseline is stable

- [x] T001 Run `dotnet build` to confirm baseline compiles with zero warnings
- [x] T002 Run `dotnet test` to confirm all existing tests pass
- [x] T003 [P] Create directory `MrWhoOidc.Auth/Options/` for moved configuration classes
- [x] T004 [P] Create directory `MrWhoOidc.Auth/Services/Authentication/` for client auth abstraction
- [x] T005 [P] Create directory `MrWhoOidc.Auth/Services/Token/` for decomposed token services
- [x] T006 [P] Create directory `MrWhoOidc.Auth/Services/Authorization/` for decomposed authorize services
- [x] T007 [P] Create directory `MrWhoOidc.Auth/Services/KeyManagement/` for cached key provider

**Checkpoint**: Directory structure ready, baseline green

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Move shared types that multiple user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T008 Create `MrWhoOidc.Auth/Options/OidcOptions.cs` with same content as WebAuth version
- [x] T009 Update all Auth usages to reference `MrWhoOidc.Auth.Options.OidcOptions`
- [x] T010 Update all WebAuth usages to reference `MrWhoOidc.Auth.Options.OidcOptions`
- [x] T011 Delete `MrWhoOidc.WebAuth/Handlers/OidcOptions.cs` after migration complete
- [x] T012 Rename `MrWhoOidc.Auth/Telemetry/OidcMetrics.cs` to `GlobalAuthMetrics.cs`
- [x] T013 Update all Auth references from `OidcMetrics` to `GlobalAuthMetrics`
- [x] T014 Rename `MrWhoOidc.WebAuth/Telemetry/OidcMetrics.cs` to `OidcEndpointMetrics.cs`
- [x] T015 Update all WebAuth references from `OidcMetrics` to `OidcEndpointMetrics`
- [x] T016 Run `dotnet build` to confirm all renames compile
- [x] T017 Run `dotnet test` to confirm no regressions from foundational changes

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel

---

## Phase 3: User Story 1 - Security Fixes for Token Operations (Priority: P1) 🎯 MVP

**Goal**: Fix critical security vulnerabilities (blocking async, race condition, audience validation)

**Independent Test**: Load test token endpoint at 1000 concurrent requests; verify audience validation for both JWT and opaque token exchange paths

### Tests for User Story 1

- [ ] T018 [P] [US1] Create test `CachedKeyProviderTests.cs` in `MrWhoOidc.UnitTests/KeyManagement/`
- [ ] T019 [P] [US1] Create test `ConsentServiceTransactionTests.cs` in `MrWhoOidc.UnitTests/Services/`
- [ ] T020 [P] [US1] Create test `TokenExchangeAudienceValidationTests.cs` in `MrWhoOidc.UnitTests/Services/`

### Implementation for User Story 1

- [ ] T021 [P] [US1] Create `ICachedKeyProvider.cs` interface in `MrWhoOidc.Auth/Services/KeyManagement/`
- [ ] T022 [US1] Create `CachedKeyProvider.cs` implementation in `MrWhoOidc.Auth/Services/KeyManagement/`
- [ ] T023 [US1] Register `ICachedKeyProvider` as singleton in `MrWhoOidc.Auth/Extensions/ServiceCollectionExtensions.cs`
- [ ] T024 [US1] Update `JwtService.cs` constructor to inject `ICachedKeyProvider` instead of direct `IKeyStore`
- [ ] T025 [US1] Add key cache warmup in startup via `IHostedService` in `MrWhoOidc.WebAuth/`
- [ ] T026 [US1] Add transaction wrapper to `ConsentService.GrantConsentAsync` using `CreateExecutionStrategy` pattern
- [ ] T027 [US1] Add audience validation for opaque tokens in `TokenExchangeService.cs` after entity load
- [ ] T028 [US1] Add legacy secret authentication metric emission in `ClientStore.cs` when legacy hash is used
- [ ] T029 [US1] Run all tests to verify US1 implementation
- [ ] T030 [US1] Update XML documentation for all new US1 types

**Checkpoint**: Security fixes complete - token operations are thread-safe and validate consistently

---

## Phase 4: User Story 2 - Layer Violation Corrections (Priority: P2)

**Goal**: Separate domain logic (Auth) from HTTP handling (WebAuth) for maintainability

**Independent Test**: Verify Auth project has no HTTP-specific dependencies; WebAuth handlers delegate all business logic

### Tests for User Story 2

- [ ] T031 [P] [US2] Create test `ClientAuthenticationServiceTests.cs` in `MrWhoOidc.UnitTests/Services/Authentication/`
- [ ] T032 [P] [US2] Create test `RegistrationServiceTests.cs` in `MrWhoOidc.UnitTests/Services/Users/`

### Implementation for User Story 2

- [ ] T033 [P] [US2] Create `IClientAuthenticationService.cs` interface in `MrWhoOidc.Auth/Services/Authentication/`
- [ ] T034 [P] [US2] Create `ClientCredentialInput.cs` record in `MrWhoOidc.Auth/Services/Authentication/`
- [ ] T035 [P] [US2] Create `ClientAuthResult.cs` record in `MrWhoOidc.Auth/Services/Authentication/`
- [ ] T036 [US2] Create `ClientAuthenticationService.cs` implementation in `MrWhoOidc.Auth/Services/Authentication/`
- [ ] T037 [US2] Register `IClientAuthenticationService` in `MrWhoOidc.Auth/Extensions/ServiceCollectionExtensions.cs`
- [ ] T038 [US2] Refactor `ClientAuthenticator.cs` in WebAuth to extract HTTP params and delegate to Auth service
- [ ] T039 [P] [US2] Create `IRegistrationService.cs` interface in `MrWhoOidc.Auth/Services/Users/`
- [ ] T040 [US2] Create `RegistrationService.cs` implementation in `MrWhoOidc.Auth/Services/Users/`
- [ ] T041 [US2] Refactor WebAuth `RegistrationService` to delegate domain logic to Auth's `IRegistrationService`
- [ ] T042 [US2] Move logout token JWT creation from `LogoutHandler.cs` to Auth's token services
- [ ] T043 [US2] Run all tests to verify US2 implementation
- [ ] T044 [US2] Update XML documentation for all new US2 types

**Checkpoint**: Layer violations fixed - Auth has no HTTP dependencies

---

## Phase 5: User Story 3 - TokenService God Class Decomposition (Priority: P3)

**Goal**: Break TokenService (723 lines) into focused single-responsibility services

**Independent Test**: Each extracted service is under 150 lines and has dedicated unit tests; TokenService orchestrator under 150 lines

### Tests for User Story 3

- [ ] T045 [P] [US3] Create test `AuthorizationCodeExchangerTests.cs` in `MrWhoOidc.UnitTests/Services/Token/`
- [ ] T046 [P] [US3] Create test `RefreshTokenExchangerTests.cs` in `MrWhoOidc.UnitTests/Services/Token/`
- [ ] T047 [P] [US3] Create test `ClientCredentialsTokenFactoryTests.cs` in `MrWhoOidc.UnitTests/Services/Token/`
- [ ] T048 [P] [US3] Create test `DeviceCodeTokenFactoryTests.cs` in `MrWhoOidc.UnitTests/Services/Token/`
- [ ] T049 [P] [US3] Create test `AccessTokenClaimBuilderTests.cs` in `MrWhoOidc.UnitTests/Services/Token/`

### Implementation for User Story 3

- [ ] T050 [P] [US3] Create `IAuthorizationCodeExchanger.cs` interface in `MrWhoOidc.Auth/Services/Token/`
- [ ] T051 [P] [US3] Create `AuthorizationCodeExchangeRequest.cs` record in `MrWhoOidc.Auth/Services/Token/`
- [ ] T052 [US3] Create `AuthorizationCodeExchanger.cs` implementation (extract from TokenService)
- [ ] T053 [P] [US3] Create `IRefreshTokenExchanger.cs` interface in `MrWhoOidc.Auth/Services/Token/`
- [ ] T054 [P] [US3] Create `RefreshTokenExchangeRequest.cs` record in `MrWhoOidc.Auth/Services/Token/`
- [ ] T055 [US3] Create `RefreshTokenExchanger.cs` implementation (extract from TokenService)
- [ ] T056 [P] [US3] Create `IClientCredentialsTokenFactory.cs` interface in `MrWhoOidc.Auth/Services/Token/`
- [ ] T057 [P] [US3] Create `ClientCredentialsRequest.cs` record in `MrWhoOidc.Auth/Services/Token/`
- [ ] T058 [US3] Create `ClientCredentialsTokenFactory.cs` implementation (extract from TokenService)
- [ ] T059 [P] [US3] Create `IDeviceCodeTokenFactory.cs` interface in `MrWhoOidc.Auth/Services/Token/`
- [ ] T060 [P] [US3] Create `DeviceCodePollRequest.cs` record in `MrWhoOidc.Auth/Services/Token/`
- [ ] T061 [US3] Create `DeviceCodeTokenFactory.cs` implementation (extract from TokenService)
- [ ] T062 [P] [US3] Create `IAccessTokenClaimBuilder.cs` interface in `MrWhoOidc.Auth/Services/Token/`
- [ ] T063 [US3] Create `AccessTokenClaimBuilder.cs` implementation (extract claim building logic)
- [ ] T064 [US3] Register all new token services in `MrWhoOidc.Auth/Extensions/ServiceCollectionExtensions.cs`
- [ ] T065 [US3] Refactor `TokenService.cs` to delegate to extracted services (orchestrator pattern)
- [ ] T066 [US3] Verify `TokenService.cs` is under 150 lines
- [ ] T067 [US3] Run all tests to verify US3 implementation
- [ ] T068 [US3] Update XML documentation for all new US3 types

**Checkpoint**: TokenService decomposed - each service has single responsibility

---

## Phase 6: User Story 4 - AuthorizeHandler Decomposition (Priority: P3)

**Goal**: Break AuthorizeHandler (708 lines) into focused components for independent testing

**Independent Test**: Main handler under 200 lines; each component has dedicated tests

### Tests for User Story 4

- [ ] T069 [P] [US4] Create test `AuthorizeRequestValidatorTests.cs` in `MrWhoOidc.UnitTests/Services/Authorization/`
- [ ] T070 [P] [US4] Create test `ConsentProcessorTests.cs` in `MrWhoOidc.UnitTests/Services/Authorization/`
- [ ] T071 [P] [US4] Create test `ProviderSelectionServiceTests.cs` in `MrWhoOidc.UnitTests/Services/Authorization/`

### Implementation for User Story 4

- [ ] T072 [P] [US4] Create `IAuthorizeRequestValidator.cs` interface in `MrWhoOidc.Auth/Services/Authorization/`
- [ ] T073 [P] [US4] Create `AuthorizeRequest.cs` record in `MrWhoOidc.Auth/Services/Authorization/`
- [ ] T074 [P] [US4] Create `AuthorizeValidationResult.cs` record in `MrWhoOidc.Auth/Services/Authorization/`
- [ ] T075 [US4] Create `AuthorizeRequestValidator.cs` implementation (extract validation logic)
- [ ] T076 [P] [US4] Create `IConsentProcessor.cs` interface in `MrWhoOidc.Auth/Services/Authorization/`
- [ ] T077 [P] [US4] Create `ConsentDecision.cs` record in `MrWhoOidc.Auth/Services/Authorization/`
- [ ] T078 [US4] Create `ConsentProcessor.cs` implementation (extract consent logic)
- [ ] T079 [P] [US4] Create `IProviderSelectionService.cs` interface in `MrWhoOidc.Auth/Services/Authorization/`
- [ ] T080 [P] [US4] Create `ProviderSelectionResult.cs` and `ProviderOption.cs` records in `MrWhoOidc.Auth/Services/Authorization/`
- [ ] T081 [US4] Create `ProviderSelectionService.cs` implementation (extract provider selection logic)
- [ ] T082 [US4] Register all new authorization services in `MrWhoOidc.Auth/Extensions/ServiceCollectionExtensions.cs`
- [ ] T083 [US4] Refactor `AuthorizeHandler.cs` in WebAuth to delegate to Auth services (orchestrator pattern)
- [ ] T084 [US4] Verify `AuthorizeHandler.cs` is under 200 lines
- [ ] T085 [US4] Run all tests to verify US4 implementation
- [ ] T086 [US4] Update XML documentation for all new US4 types

**Checkpoint**: AuthorizeHandler decomposed - each component independently testable

---

## Phase 7: User Story 5 - Code Duplication Removal (Priority: P4)

**Goal**: Consolidate duplicated code into single implementations

**Independent Test**: Grep confirms each utility exists in exactly one location; no duplicate patterns

### Tests for User Story 5

- [ ] T087 [P] [US5] Create test `TokenLifetimeResolverTests.cs` in `MrWhoOidc.UnitTests/Services/Token/`
- [ ] T088 [P] [US5] Create test `RoleClaimBuilderTests.cs` in `MrWhoOidc.UnitTests/Services/Token/`
- [ ] T089 [P] [US5] Create test `OpaqueTokenPolicyTests.cs` in `MrWhoOidc.UnitTests/Services/Token/`
- [ ] T090 [P] [US5] Create test `MtlsThumbprintResolverTests.cs` in `MrWhoOidc.UnitTests/Services/`

### Implementation for User Story 5

- [ ] T091 [P] [US5] Create `ITokenLifetimeResolver.cs` interface in `MrWhoOidc.Auth/Services/Token/`
- [ ] T092 [US5] Create `TokenLifetimeResolver.cs` implementation (consolidate lifetime calculation logic)
- [ ] T093 [P] [US5] Create `IRoleClaimBuilder.cs` interface in `MrWhoOidc.Auth/Services/Token/`
- [ ] T094 [US5] Create `RoleClaimBuilder.cs` implementation (consolidate role claim building)
- [ ] T095 [P] [US5] Create `IOpaqueTokenPolicy.cs` interface in `MrWhoOidc.Auth/Services/Token/`
- [ ] T096 [US5] Create `OpaqueTokenPolicy.cs` implementation (consolidate opaque token decision logic)
- [ ] T097 [P] [US5] Create `IMtlsThumbprintResolver.cs` interface in `MrWhoOidc.Auth/Services/`
- [ ] T098 [US5] Create `MtlsThumbprintResolver.cs` implementation (consolidate mTLS thumbprint lookup)
- [ ] T099 [US5] Remove legacy `ComputeHash` wrapper methods, use `CryptoHelper` directly in callers
- [ ] T100 [US5] Update all callers to use new consolidated services
- [ ] T101 [US5] Register all new services in `MrWhoOidc.Auth/Extensions/ServiceCollectionExtensions.cs`
- [ ] T102 [US5] Run grep to verify no duplicate implementations remain
- [ ] T103 [US5] Run all tests to verify US5 implementation
- [ ] T104 [US5] Update XML documentation for all new US5 types

**Checkpoint**: Duplication removed - each utility exists in exactly one place

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Final verification, documentation, and code quality

- [ ] T105 Run `dotnet build` in Debug and Release to confirm zero warnings
- [ ] T106 Run `dotnet test` to confirm all tests pass (existing + new)
- [ ] T107 [P] Verify TokenService.cs is under 150 lines (FR-014)
- [ ] T108 [P] Verify AuthorizeHandler.cs is under 200 lines (FR-015)
- [ ] T109 [P] Verify no Auth files import from MrWhoOidc.WebAuth namespace (SC-005)
- [ ] T110 [P] Verify all public interfaces have XML documentation (FR-022)
- [ ] T111 [P] Verify all services use nullable reference type annotations (FR-023)
- [ ] T112 Run quickstart.md validation steps
- [ ] T113 Update docs/architecture-refactoring-plan.md to mark items as complete
- [ ] T114 Final code review and cleanup

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion - BLOCKS all user stories
- **User Stories (Phase 3-7)**: All depend on Foundational phase completion
  - US1 (Security) → Highest priority, can start immediately after Phase 2
  - US2 (Layer Violations) → Can start after Phase 2, parallel with US1
  - US3 (TokenService) → Can start after Phase 2, parallel with US1/US2
  - US4 (AuthorizeHandler) → Can start after Phase 2, parallel with US1/US2/US3
  - US5 (Duplication) → Recommended after US3/US4 complete (may reference extracted services)
- **Polish (Phase 8)**: Depends on all user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: No dependencies on other stories - independent security fixes
- **User Story 2 (P2)**: No dependencies on other stories - independent layer corrections
- **User Story 3 (P3)**: No dependencies on other stories - TokenService decomposition
- **User Story 4 (P3)**: No dependencies on other stories - AuthorizeHandler decomposition
- **User Story 5 (P4)**: Soft dependency on US3/US4 (may consolidate code they extract)

### Within Each User Story

- Tests written and verified to work with existing behavior
- Interfaces before implementations
- Core implementation before integration
- All tests pass before story marked complete

### Parallel Opportunities

**Phase 1 (Setup)**: T003-T007 all parallel

**Phase 2 (Foundational)**: T012-T015 can run in parallel (different files)

**Phase 3 (US1)**: T018-T020 (tests), T021 (interface) all parallel

**Phase 4 (US2)**: T031-T032 (tests), T033-T035, T039 all parallel

**Phase 5 (US3)**: T045-T049 (tests), T050-T051, T053-T054, T056-T057, T059-T060, T062 all parallel

**Phase 6 (US4)**: T069-T071 (tests), T072-T074, T076-T077, T079-T080 all parallel

**Phase 7 (US5)**: T087-T090 (tests), T091, T093, T095, T097 all parallel

**Phase 8 (Polish)**: T107-T111 all parallel

---

## Parallel Example: User Story 3 - TokenService Decomposition

```bash
# Launch all tests in parallel:
Task T045: "AuthorizationCodeExchangerTests.cs"
Task T046: "RefreshTokenExchangerTests.cs"
Task T047: "ClientCredentialsTokenFactoryTests.cs"
Task T048: "DeviceCodeTokenFactoryTests.cs"
Task T049: "AccessTokenClaimBuilderTests.cs"

# Launch all interfaces in parallel:
Task T050: "IAuthorizationCodeExchanger.cs"
Task T053: "IRefreshTokenExchanger.cs"
Task T056: "IClientCredentialsTokenFactory.cs"
Task T059: "IDeviceCodeTokenFactory.cs"
Task T062: "IAccessTokenClaimBuilder.cs"

# Launch all request records in parallel:
Task T051: "AuthorizationCodeExchangeRequest.cs"
Task T054: "RefreshTokenExchangeRequest.cs"
Task T057: "ClientCredentialsRequest.cs"
Task T060: "DeviceCodePollRequest.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational
3. Complete Phase 3: User Story 1 (Security Fixes)
4. **STOP and VALIDATE**: Run load tests, verify security fixes work
5. Deploy/demo if ready - system is safer even with god classes

### Incremental Delivery

1. Complete Setup + Foundational → Foundation ready
2. Add User Story 1 → Test → Deploy (Security MVP!)
3. Add User Story 2 → Test → Deploy (Clean layers)
4. Add User Stories 3+4 → Test → Deploy (Clean code)
5. Add User Story 5 → Test → Deploy (No duplication)

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: User Story 1 (Security - highest priority)
   - Developer B: User Story 2 (Layer violations)
   - Developer C: User Story 3 (TokenService)
   - Developer D: User Story 4 (AuthorizeHandler)
3. Once US3+US4 complete:
   - Any developer: User Story 5 (Duplication)

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Tests should verify existing behavior before refactoring
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- FR-024 requires 80%+ test coverage for new services
- SC-003 target: TokenService under 150 lines
- SC-004 target: AuthorizeHandler under 200 lines
