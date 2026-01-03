---
description: "Task list for implementing Pairwise Subject Identifiers"
---

# Tasks: Pairwise Subject Identifiers

**Input**: Design documents from `/specs/016-pairwise-subject-ids/`
**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: REQUIRED by spec.md (FR-013). Include unit tests (sector resolution + mapping) and integration tests verifying `sub` behavior in ID tokens and UserInfo.

## Format: `[ID] [P?] [Story] Description`
- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Each task includes an exact file path

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create missing feature artifacts referenced by plan.md so implementation work has a single source of truth.

- [ ] T001 [P] [Shared] Create research notes in specs/016-pairwise-subject-ids/research.md
- [ ] T002 [P] [Shared] Document entity shape and indexes in specs/016-pairwise-subject-ids/data-model.md
- [ ] T003 [P] [Shared] Create service contract notes in specs/016-pairwise-subject-ids/contracts/service-contracts.md
- [ ] T004 [P] [Shared] Create admin surface notes in specs/016-pairwise-subject-ids/contracts/admin-api.md
- [ ] T005 [P] [Shared] Write verification guide in specs/016-pairwise-subject-ids/quickstart.md

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Persistence + domain services required before any user story can work.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T006 [Shared] Update client configuration fields (subject type + sector identifier URI) in MrWhoOidc.Auth/Persistence/AuthDbContext.cs
- [ ] T007 [Shared] Add PairwiseSubjectIdentifier entity + DbSet in MrWhoOidc.Auth/Persistence/AuthDbContext.cs
- [ ] T008 [Shared] Add model configuration + indexes for pairwise mappings in MrWhoOidc.Auth/Persistence/AuthDbContext.cs
- [ ] T009 [P] [Shared] Add sector resolution interface in MrWhoOidc.Auth/Services/SubjectIdentifiers/ISectorIdentifierResolver.cs
- [ ] T010 [P] [Shared] Add pairwise subject interface in MrWhoOidc.Auth/Services/SubjectIdentifiers/IPairwiseSubjectService.cs
- [ ] T011 [P] [Shared] Add resolver implementation (fallback uses AllowedLoginRedirectUrisJson host) in MrWhoOidc.Auth/Services/SubjectIdentifiers/SectorIdentifierResolver.cs
- [ ] T012 [P] [Shared] Add pairwise mapping implementation (get-or-create + secure random sub) in MrWhoOidc.Auth/Services/SubjectIdentifiers/PairwiseSubjectService.cs
- [ ] T012 [P] [Shared] Add pairwise mapping implementation (get-or-create + CSPRNG base64url `sub`) in MrWhoOidc.Auth/Services/SubjectIdentifiers/PairwiseSubjectService.cs
- [ ] T013 [Shared] Wire DI registrations for subject identifier services in MrWhoOidc.Auth/Persistence/ServiceCollectionExtensions.cs
- [ ] T014 [Shared] Generate EF Core migration for schema changes in MrWhoOidc.Auth/Persistence/Migrations/ (run `dotnet ef migrations add AddPairwiseSubjectIdentifiers --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations`)
- [ ] T015 [P] [Shared] Include new client fields in config export/import in MrWhoOidc.WebAuth/Services/ConfigurationExportService.cs
- [ ] T016 [P] [Shared] Include new client fields in config import/merge in MrWhoOidc.WebAuth/Services/ConfigurationImportService.cs

**Checkpoint**: Foundation ready — user story implementation can now begin.

---

## Phase 3: User Story 1 - Issue pairwise `sub` per client (Priority: P1) 🎯 MVP

**Goal**: Administrators can opt a client into pairwise subject identifiers and the system issues a stable, non-public `sub` for that client.

**Independent Test**: Configure one client as pairwise and validate that repeated logins produce the same `sub` for that client, and that a public client still receives the public `sub`.

### Tests for User Story 1 (REQUIRED)

- [ ] T017 [P] [US1] Unit test: sector fallback derives host from redirect URIs in MrWhoOidc.UnitTests/Services/SubjectIdentifiers/SectorIdentifierResolverTests.cs
- [ ] T018 [P] [US1] Unit test: get-or-create mapping returns stable value for same user+sector in MrWhoOidc.UnitTests/Services/SubjectIdentifiers/PairwiseSubjectServiceTests.cs
- [ ] T019 [P] [US1] Integration test: pairwise client receives stable `sub` in ID token in MrWhoOidc.UnitTests/Integration/PairwiseSubjectIdentifiersTests.cs
- [ ] T020 [P] [US1] Integration test: public client still receives public `sub` in ID token in MrWhoOidc.UnitTests/Integration/PairwiseSubjectIdentifiersTests.cs

### Implementation for User Story 1

- [ ] T021 [P] [US1] Add subject type + sector identifier fields to admin UI in MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml
- [ ] T022 [US1] Update admin page model binding + persistence for new fields in MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs
- [ ] T023 [US1] Add server-side validation for subject type values in MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs
- [ ] T024 [US1] Update access token `sub` claim selection for pairwise clients in MrWhoOidc.Auth/Services/Token/AccessTokenClaimBuilder.cs
- [ ] T025 [US1] Update ID token `sub` claim selection for pairwise clients in MrWhoOidc.Auth/Services/Token/AuthorizationCodeExchanger.cs
- [ ] T026 [US1] Add audit logging for pairwise mapping creation (no raw JWT logging) in MrWhoOidc.Auth/Services/SubjectIdentifiers/PairwiseSubjectService.cs


**Checkpoint**: US1 complete — pairwise `sub` works for a single client using sector fallback.

---

## Phase 4: User Story 2 - Control pairwise scope via sector identifier (Priority: P2)

**Goal**: Administrators can control whether multiple clients share the same pairwise `sub` by using a sector identifier.

**Independent Test**: Two pairwise clients with the same sector identifier yield the same `sub` for the same user; different sector identifiers yield different `sub`.

### Tests for User Story 2 (REQUIRED)

- [ ] T027 [P] [US2] Unit test: validates sector_identifier_uri requires HTTPS and redirect URI inclusion in MrWhoOidc.UnitTests/Services/SubjectIdentifiers/SectorIdentifierUriValidatorTests.cs
- [ ] T028 [P] [US2] Integration test: same sector_identifier_uri yields same `sub` across two clients in MrWhoOidc.UnitTests/Integration/PairwiseSubjectIdentifiersTests.cs
- [ ] T029 [P] [US2] Integration test: different sectors yield different `sub` across clients in MrWhoOidc.UnitTests/Integration/PairwiseSubjectIdentifiersTests.cs
- [ ] T040 [P] [US2] Integration test: unreachable/invalid sector_identifier_uri fails issuance (no fallback) in MrWhoOidc.UnitTests/Integration/PairwiseSubjectIdentifiersTests.cs

### Implementation for User Story 2

- [ ] T030 [P] [US2] Add sector identifier URI validation helper (HTTPS + redirect URI set match) in MrWhoOidc.Auth/Services/SubjectIdentifiers/SectorIdentifierUriValidator.cs
- [ ] T031 [US2] Add sector identifier resolution support for configured sector_identifier_uri in MrWhoOidc.Auth/Services/SubjectIdentifiers/SectorIdentifierResolver.cs
- [ ] T032 [US2] Normalize and persist sector identifiers consistently (host normalization + tenant scoping) in MrWhoOidc.Auth/Services/SubjectIdentifiers/SectorIdentifierResolver.cs
- [ ] T033 [US2] Surface validation errors for sector identifier URI in admin UI in MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs
- [ ] T034 [US2] Ensure pairwise mapping uniqueness constraints are tenant-scoped and align with sector normalization in MrWhoOidc.Auth/Persistence/AuthDbContext.cs

**Checkpoint**: US2 complete — sector-based grouping works and invalid configuration is rejected.

---

## Phase 5: User Story 3 - Discover and validate support for subject identifier types (Priority: P3)

**Goal**: Integrators can see that the provider supports both `public` and `pairwise` subject identifiers.

**Independent Test**: Fetch the discovery document and verify it lists both supported subject types.

### Tests for User Story 3 (REQUIRED)

- [ ] T035 [P] [US3] Integration test: discovery advertises both subject types in MrWhoOidc.UnitTests/Integration/DiscoveryMetadataTests.cs

### Implementation for User Story 3

- [ ] T036 [US3] Update discovery metadata to include both subject types in MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs

**Checkpoint**: US3 complete — discovery advertises `subject_types_supported` including both values.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Documentation alignment and build-quality verification.

- [ ] T037 [P] [Shared] Update feature documentation to reflect implemented behavior in docs/future-plans/pairwise-subject-identifiers.md
- [ ] T038 [Shared] Run build gate check for solution in MrWhoOidc.slnx (`dotnet build` and ensure zero warnings)
- [ ] T039 [Shared] Run test gate check in MrWhoOidc.slnx (`dotnet test` and ensure passing)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: Can start immediately.
- **Foundational (Phase 2)**: Depends on Setup completion; blocks all user stories.
- **User Stories (Phase 3+)**: Depend on Foundational.
- **Polish (Phase 6)**: Depends on all desired user stories being complete.

### User Story Dependencies

- **US1 (P1)**: Depends on Foundational; independent of US2/US3.
- **US2 (P2)**: Depends on US1 domain primitives (pairwise service + resolver), but can be developed after Foundational if you implement resolver support incrementally.
- **US3 (P3)**: Depends only on Foundational-level discovery plumbing; can be done anytime after Phase 2.

---

## Parallel Opportunities

- Phase 1 tasks T001–T005 can run in parallel.
- Phase 2 tasks T009–T012 and T015–T016 can run in parallel.
- After Phase 2, UI tasks (T017–T019) can proceed in parallel with token changes (T020–T021).
- US3 discovery update (T028) can be done in parallel with US1/US2 once Foundational is complete.

---

## Parallel Example: User Story 1

```text
Task: "Add subject type + sector identifier fields to admin UI in MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml" (T021)
Task: "Update access token sub claim selection for pairwise clients in MrWhoOidc.Auth/Services/Token/AccessTokenClaimBuilder.cs" (T024)
Task: "Update ID token sub claim selection for pairwise clients in MrWhoOidc.Auth/Services/Token/AuthorizationCodeExchanger.cs" (T025)
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 and Phase 2
2. Complete Phase 3 (US1)
3. Stop and validate US1 independently

### Incremental Delivery

1. US1 → validate
2. US2 → validate
3. US3 → validate

---

## Notes

- [P] tasks are parallelizable (different files, no dependencies).
- [US1]/[US2]/[US3] labels map tasks to user stories.
- Each user story is independently testable via its “Independent Test” criteria.
