# Tasks: OIDC Configuration Export/Import

**Input**: Design documents from `/specs/012-oidc-export-import/`  
**Prerequisites**: plan.md ✓, spec.md ✓, research.md ✓, data-model.md ✓, contracts/api.md ✓

**Tests**: Unit and integration tests are included per existing project patterns.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`
- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2)
- Exact file paths included in descriptions

---

## Phase 1: Setup

**Purpose**: Project structure and schema extensions

- [x] T001 [P] Create `ExportMode` and `ConflictResolution` enums in `MrWhoOidc.Auth/Seeding/ExportEnums.cs`
- [x] T002 [P] Create `ExportOptions` DTO in `MrWhoOidc.Auth/Seeding/ExportOptions.cs`
- [x] T003 [P] Create `ImportOptions` DTO in `MrWhoOidc.Auth/Seeding/ImportOptions.cs`
- [x] T004 [P] Create `ExportMetadata` record in `MrWhoOidc.Auth/Seeding/ExportMetadata.cs`
- [x] T005 [P] Create `ExportManifest` root container in `MrWhoOidc.Auth/Seeding/ExportManifest.cs`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Schema Extensions

- [x] T006 Create `IdentityProviderSeedDefinition` in `MrWhoOidc.Auth/Seeding/IdentityProviderSeedDefinition.cs`
- [x] T007 [P] Create `ClaimMappingSeedDefinition` in `MrWhoOidc.Auth/Seeding/ClaimMappingSeedDefinition.cs`
- [x] T008 [P] Create `ProviderKeySeedDefinition` in `MrWhoOidc.Auth/Seeding/ProviderKeySeedDefinition.cs`
- [x] T009 [P] Create `ClientIdpAssignmentSeedDefinition` in `MrWhoOidc.Auth/Seeding/ClientIdpAssignmentSeedDefinition.cs`
- [x] T010 [P] Create `RoleSeedDefinition` in `MrWhoOidc.Auth/Seeding/RoleSeedDefinition.cs`
- [x] T011 Extend `TenantSeedDefinition` with `identityProviders`, `roles`, new branding fields in `MrWhoOidc.Auth/Seeding/SeedManifest.cs`
- [x] T012 Extend `ClientSeedDefinition` with `clientSecretHash`, `publicJwksJson`, `identityProviderAssignments`, logout URIs, M2M settings in `MrWhoOidc.Auth/Seeding/SeedManifest.cs` (depends on T009)

### Audit Entity & Migration

- [x] T013 Create `ConfigurationAuditLog` entity in `MrWhoOidc.Auth/Entities/ConfigurationAuditLog.cs`
- [x] T014 Add `ConfigurationAuditLogs` DbSet and configuration to `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (depends on T013)
- [x] T015 Generate EF Core migration for `ConfigurationAuditLog` using `dotnet ef migrations add AddConfigurationAuditLog` (depends on T014)

### Service Interfaces

- [x] T016 [P] Create `IConfigurationExportService` interface in `MrWhoOidc.Auth/Services/IConfigurationExportService.cs`
- [x] T017 [P] Create `IConfigurationImportService` interface with preview/execute methods in `MrWhoOidc.Auth/Services/IConfigurationImportService.cs`
- [x] T018 [P] Create import DTOs (`ImportPreview`, `ImportConflict`, `ImportResult`, `ValidationError`) in `MrWhoOidc.Auth/Seeding/ImportDtos.cs`

### Service Registration

- [x] T019 Register export/import services in `MrWhoOidc.WebAuth/Program.cs` DI container (depends on T016, T017)

**Checkpoint**: Foundation ready - user story implementation can now begin

---

## Phase 3: User Story 1 - Export Entire Tenant Configuration (Priority: P1) 🎯 MVP

**Goal**: Platform admins can export a tenant's complete configuration (realms, clients, IdPs) as JSON

**Independent Test**: Select tenant → Export → Download JSON → Verify complete structure and obfuscated secrets

### Tests for User Story 1

- [ ] T020 [P] [US1] Unit test for tenant export serialization in `MrWhoOidc.UnitTests/Export/TenantExportSerializationTests.cs`
- [ ] T021 [P] [US1] Unit test for secret obfuscation in `MrWhoOidc.UnitTests/Export/SecretObfuscationTests.cs`

### Implementation for User Story 1

- [ ] T022 [US1] Implement `ConfigurationExportService.ExportTenantAsync()` with full entity loading in `MrWhoOidc.Auth/Services/ConfigurationExportService.cs`
- [ ] T023 [US1] Add secret obfuscation logic (replace with `***OBFUSCATED***`) in `ConfigurationExportService` (depends on T022)
- [ ] T024 [US1] Add full export mode with hashed secrets in `ConfigurationExportService` (depends on T023)
- [ ] T025 [US1] Add checksum generation (SHA-256 of data section) in `ConfigurationExportService` (depends on T022)
- [ ] T026 [US1] Add audit logging for export operations in `ConfigurationExportService` (depends on T015)
- [ ] T027 [US1] Create tenant export API endpoint `GET /admin/api/platform/tenants/{slug}/export` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T028 [US1] Create tenant export Razor Page in `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Export.cshtml(.cs)` with export mode selector

### Integration Test

- [ ] T029 [US1] Integration test for tenant export endpoint with TestServer in `MrWhoOidc.UnitTests/Export/TenantExportIntegrationTests.cs`

**Checkpoint**: User Story 1 complete - tenant export fully functional

---

## Phase 4: User Story 2 - Import Tenant Configuration (Priority: P1) 🎯 MVP

**Goal**: Platform admins can import tenant configurations from exported JSON files with conflict resolution

**Independent Test**: Upload JSON → Preview → Resolve conflicts → Import → Verify entities created

### Tests for User Story 2

- [x] T030 [P] [US2] Unit test for manifest validation in `MrWhoOidc.UnitTests/Import/ManifestValidationTests.cs`
- [x] T031 [P] [US2] Unit test for conflict detection in `MrWhoOidc.UnitTests/Import/ConflictDetectionTests.cs`
- [x] T032 [P] [US2] Unit test for transactional rollback in `MrWhoOidc.UnitTests/Import/TransactionalRollbackTests.cs`

### Implementation for User Story 2

- [x] T033 [US2] Implement `ConfigurationImportService.PreviewImportAsync()` for validation and conflict detection in `MrWhoOidc.WebAuth/Services/ConfigurationImportService.cs`
- [x] T034 [US2] Implement conflict detection for tenant slug, realm name, client ID, provider name collisions (depends on T033)
- [x] T035 [US2] Implement conflict resolution strategies (skip, rename, merge, overwrite) (depends on T034)
- [x] T036 [US2] Implement `ConfigurationImportService.ExecuteImportAsync()` with transaction wrapper using EF Core execution strategy (depends on T035)
- [x] T037 [US2] Handle obfuscated secret prompting during import (depends on T036)
- [x] T038 [US2] Add audit logging for import operations (depends on T015)
- [x] T039 [US2] Create import preview API endpoint `POST /admin/api/platform/tenants/import/preview` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [x] T040 [US2] Create import execute API endpoint `POST /admin/api/platform/tenants/import` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [x] T041 [US2] Create tenant import Razor Page in `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Import.cshtml(.cs)` with file upload, preview, and conflict resolution UI

### Integration Tests

- [x] T042 [P] [US2] Integration test for import preview endpoint in `MrWhoOidc.UnitTests/Import/TenantImportIntegrationTests.cs`
- [x] T043 [P] [US2] Integration test for import execute with rollback in `MrWhoOidc.UnitTests/Import/TenantImportIntegrationTests.cs`

**Checkpoint**: User Stories 1 AND 2 complete - full tenant backup/restore cycle functional ✅

---

## Phase 5: User Story 3 - Export Individual Realm (Priority: P2)

**Goal**: Tenant admins can export a single realm with its clients

**Independent Test**: Select realm → Export → Download JSON → Verify realm and clients included

### Implementation for User Story 3

- [ ] T044 [US3] Implement `ConfigurationExportService.ExportRealmAsync()` in `MrWhoOidc.Auth/Services/ConfigurationExportService.cs`
- [ ] T045 [US3] Create realm export API endpoint `GET /admin/api/realms/{id}/export` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T046 [US3] Create realm export Razor Page in `MrWhoOidc.WebAuth/Pages/Admin/Realms/Export.cshtml(.cs)`

### Tests for User Story 3

- [ ] T047 [US3] Unit test for realm export in `MrWhoOidc.UnitTests/Export/RealmExportTests.cs`

**Checkpoint**: User Story 3 complete - realm export functional

---

## Phase 6: User Story 4 - Export Individual Client (Priority: P2)

**Goal**: Tenant admins can export a single client's configuration

**Independent Test**: Select client → Export → Download JSON → Verify complete client config

### Implementation for User Story 4

- [ ] T048 [P] [US4] Implement `ConfigurationExportService.ExportClientAsync()` in `MrWhoOidc.Auth/Services/ConfigurationExportService.cs`
- [ ] T049 [US4] Create client export API endpoint `GET /admin/api/clients/{id}/export` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T050 [US4] Create client export Razor Page in `MrWhoOidc.WebAuth/Pages/Admin/Clients/Export.cshtml(.cs)`

### Tests for User Story 4

- [ ] T051 [US4] Unit test for client export with IdP assignments in `MrWhoOidc.UnitTests/Export/ClientExportTests.cs`

**Checkpoint**: User Story 4 complete - client export functional

---

## Phase 7: User Story 5 - Export Individual Identity Provider (Priority: P2)

**Goal**: Tenant admins can export a single IdP's configuration with claim mappings

**Independent Test**: Select IdP → Export → Download JSON → Verify IdP config and claim mappings, no private keys

### Implementation for User Story 5

- [ ] T052 [P] [US5] Implement `ConfigurationExportService.ExportIdentityProviderAsync()` in `MrWhoOidc.Auth/Services/ConfigurationExportService.cs`
- [ ] T053 [US5] Ensure private keys excluded from IdP export (public keys only) (depends on T052)
- [ ] T054 [US5] Create IdP export API endpoint `GET /admin/api/providers/{id}/export` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T055 [US5] Create IdP export Razor Page in `MrWhoOidc.WebAuth/Pages/Admin/Providers/Export.cshtml(.cs)`

### Tests for User Story 5

- [ ] T056 [US5] Unit test for IdP export with claim mappings in `MrWhoOidc.UnitTests/Export/IdentityProviderExportTests.cs`

**Checkpoint**: User Stories 3, 4, 5 complete - all granular export functionality available

---

## Phase 8: User Story 6 - Import Realm Configuration (Priority: P3)

**Goal**: Tenant admins can import realm configurations into their tenant

**Independent Test**: Upload realm JSON → Select target tenant → Preview → Import → Verify realm created

### Implementation for User Story 6

- [ ] T057 [US6] Implement `ConfigurationImportService.ImportRealmAsync()` in `MrWhoOidc.Auth/Services/ConfigurationImportService.cs`
- [ ] T058 [US6] Create realm import preview endpoint `POST /admin/api/realms/import/preview` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T059 [US6] Create realm import execute endpoint `POST /admin/api/realms/import` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T060 [US6] Create realm import Razor Page in `MrWhoOidc.WebAuth/Pages/Admin/Realms/Import.cshtml(.cs)`

### Tests for User Story 6

- [ ] T061 [US6] Unit test for realm import with conflict resolution in `MrWhoOidc.UnitTests/Import/RealmImportTests.cs`

**Checkpoint**: User Story 6 complete - realm import functional

---

## Phase 9: User Story 7 - Import Individual Client (Priority: P3)

**Goal**: Tenant admins can import client configurations into a specified realm

**Independent Test**: Upload client JSON → Select target realm → Preview → Import → Verify client created

### Implementation for User Story 7

- [ ] T062 [US7] Implement `ConfigurationImportService.ImportClientAsync()` with target realm parameter in `MrWhoOidc.Auth/Services/ConfigurationImportService.cs`
- [ ] T063 [US7] Create client import preview endpoint `POST /admin/api/clients/import/preview` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T064 [US7] Create client import execute endpoint `POST /admin/api/clients/import` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T065 [US7] Create client import Razor Page in `MrWhoOidc.WebAuth/Pages/Admin/Clients/Import.cshtml(.cs)` with realm selector

### Tests for User Story 7

- [ ] T066 [US7] Unit test for client import with secret handling in `MrWhoOidc.UnitTests/Import/ClientImportTests.cs`

**Checkpoint**: User Story 7 complete - client import functional

---

## Phase 10: User Story 8 - Import Individual Identity Provider (Priority: P3)

**Goal**: Tenant admins can import IdP configurations

**Independent Test**: Upload IdP JSON → Preview → Import → Verify IdP and claim mappings created

### Implementation for User Story 8

- [ ] T067 [US8] Implement `ConfigurationImportService.ImportIdentityProviderAsync()` in `MrWhoOidc.Auth/Services/ConfigurationImportService.cs`
- [ ] T068 [US8] Create IdP import preview endpoint `POST /admin/api/providers/import/preview` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T069 [US8] Create IdP import execute endpoint `POST /admin/api/providers/import` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T070 [US8] Create IdP import Razor Page in `MrWhoOidc.WebAuth/Pages/Admin/Providers/Import.cshtml(.cs)`

### Tests for User Story 8

- [ ] T071 [US8] Unit test for IdP import with claim mapping preservation in `MrWhoOidc.UnitTests/Import/IdentityProviderImportTests.cs`

**Checkpoint**: All user stories complete - full export/import functionality available

---

## Phase 11: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

### Audit & Observability

- [ ] T072 [P] Create audit log list API endpoint `GET /admin/api/configuration-audit` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T073 [P] Create audit log detail API endpoint `GET /admin/api/configuration-audit/{id}` in `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`
- [ ] T074 [P] Create audit log Razor Page in `MrWhoOidc.WebAuth/Pages/Admin/ConfigurationAudit/Index.cshtml(.cs)`

### Documentation & Cleanup

- [ ] T075 [P] Update `docs/admin-guide.md` with export/import instructions
- [ ] T076 [P] Add export/import section to `docs/developer-guide.md`
- [ ] T077 Code cleanup: ensure consistent error messages across all export/import operations
- [ ] T078 Run `quickstart.md` validation to verify implementation matches specification

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup - BLOCKS all user stories
- **User Stories (Phases 3-10)**: All depend on Foundational phase completion
  - US1-US2 (P1): Core MVP, sequential
  - US3-US5 (P2): Can run in parallel after US1-US2
  - US6-US8 (P3): Can run in parallel after US2 patterns established
- **Polish (Phase 11)**: Depends on all user stories being complete

### User Story Dependencies

| User Story | Phase | Depends On | Can Parallel With |
|------------|-------|------------|-------------------|
| US1 (Export Tenant) | 3 | Foundational | - |
| US2 (Import Tenant) | 4 | US1 (shares DTOs) | - |
| US3 (Export Realm) | 5 | US1 | US4, US5 |
| US4 (Export Client) | 6 | US1 | US3, US5 |
| US5 (Export IdP) | 7 | US1 | US3, US4 |
| US6 (Import Realm) | 8 | US2 | US7, US8 |
| US7 (Import Client) | 9 | US2 | US6, US8 |
| US8 (Import IdP) | 10 | US2 | US6, US7 |

### Within Each User Story

- Tests SHOULD be written first (TDD pattern)
- Service implementation before API endpoints
- API endpoints before Razor Pages
- Audit logging after core functionality

### Parallel Opportunities

```
Phase 1: T001 || T002 || T003 || T004 || T005

Phase 2: T007 || T008 || T009 || T010 (after T006)
         T016 || T017 || T018

Phase 3: T020 || T021 (tests parallel)

Phases 5-7: US3 || US4 || US5 (all three in parallel)

Phases 8-10: US6 || US7 || US8 (all three in parallel)

Phase 11: T072 || T073 || T074 || T075 || T076
```

---

## Implementation Strategy

### MVP First (US1 + US2)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL - blocks all stories)
3. Complete Phase 3: User Story 1 (Export Tenant)
4. **STOP and VALIDATE**: Export a tenant, validate JSON structure
5. Complete Phase 4: User Story 2 (Import Tenant)
6. **STOP and VALIDATE**: Round-trip export → import → verify
7. Deploy/demo MVP - full backup/restore capability

### Incremental Delivery

1. MVP Ready → Export/Import tenant configurations
2. Add US3-US5 → Granular export for realms, clients, IdPs
3. Add US6-US8 → Granular import capabilities
4. Add Polish → Audit UI, documentation

### Task Count by Phase

| Phase | Tasks | Cumulative |
|-------|-------|------------|
| Phase 1: Setup | 5 | 5 |
| Phase 2: Foundational | 14 | 19 |
| Phase 3: US1 (P1) | 10 | 29 |
| Phase 4: US2 (P1) | 14 | 43 |
| Phase 5: US3 (P2) | 4 | 47 |
| Phase 6: US4 (P2) | 4 | 51 |
| Phase 7: US5 (P2) | 5 | 56 |
| Phase 8: US6 (P3) | 5 | 61 |
| Phase 9: US7 (P3) | 5 | 66 |
| Phase 10: US8 (P3) | 5 | 71 |
| Phase 11: Polish | 7 | 78 |

**Total**: 78 tasks

---

## Notes

- [P] tasks = different files, no dependencies - safe to run in parallel
- [US#] label maps task to specific user story for traceability
- Path conventions follow existing MrWhoOidc project structure
- All migrations via `dotnet ef migrations add` (not hand-written)
- Entity primary keys use `GuidHelper.NewId()` per constitution
- Verify tests fail before implementing (TDD)
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
