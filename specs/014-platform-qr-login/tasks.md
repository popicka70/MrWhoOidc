# Tasks: Platform QR Login at DiscoverTenant

**Input**: Design documents from `/specs/014-platform-qr-login/`  
**Prerequisites**: plan.md ✓, spec.md ✓, research.md ✓, data-model.md ✓, contracts/ ✓

**Tests**: Not explicitly requested in feature specification. Integration tests added for critical paths only.

**Organization**: Tasks grouped by user story to enable independent implementation and testing.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Auth domain**: `MrWhoOidc.Auth/` (entities, services, persistence)
- **WebAuth HTTP/UI**: `MrWhoOidc.WebAuth/` (pages, handlers)
- **Tests**: `MrWhoOidc.UnitTests/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create foundational entity and service that all user stories depend on

- [ ] T001 [P] Create PlatformSettings entity in `MrWhoOidc.Auth/Persistence/PlatformSettings.cs` with Id (UUIDv7), QrLoginAtDiscoveryEnabled (bool, default false), CreatedAt, UpdatedAt, UpdatedBy properties
- [ ] T002 [P] Add DbSet<PlatformSettings> to AuthDbContext in `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`
- [ ] T003 Generate EF Core migration using `dotnet ef migrations add AddPlatformSettings --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations`
- [ ] T004 [P] Create IPlatformSettingsService interface in `MrWhoOidc.Auth/Services/IPlatformSettingsService.cs` with GetSettingsAsync, UpdateSettingsAsync, IsQrLoginAtDiscoveryEnabledAsync methods
- [ ] T005 Create PlatformSettingsService implementation in `MrWhoOidc.Auth/Services/PlatformSettingsService.cs` with HybridCache caching (same pattern as TenantSettingsService)
- [ ] T006 Register IPlatformSettingsService in DI container in `MrWhoOidc.WebAuth/Program.cs`

**Checkpoint**: Entity and service ready - user story implementation can begin

---

## Phase 2: User Story 1 - Platform Administrator Enables QR Login (Priority: P1) 🎯 MVP

**Goal**: Platform admin can toggle QR login at discovery page via a new Platform Settings page

**Independent Test**: Navigate to `/platform-admin/settings`, toggle setting, verify persistence and cache invalidation

### Implementation for User Story 1

- [ ] T007 [P] [US1] Create Platform Settings page model in `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Settings.cshtml.cs` with [Authorize(Policy = "platform-admin")], [RequireDefaultTenantContext], QrLoginAtDiscoveryEnabled property, OnGetAsync loading settings, OnPostAsync saving settings
- [ ] T008 [P] [US1] Create Platform Settings Razor page in `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Settings.cshtml` with @page "/platform-admin/settings", card layout, toggle switch for QR login, save button, success message display
- [ ] T009 [US1] Add Platform Settings navigation link to admin sidebar in `MrWhoOidc.WebAuth/Pages/Shared/_AdminLayout.cshtml` under PlatformAdmin section (visible to platform-admin only)
- [ ] T010 [US1] Verify page is accessible only to platform admins by testing authorization policy

**Checkpoint**: User Story 1 complete - admin can enable/disable QR login at platform level

---

## Phase 3: User Story 2 - User Initiates QR Login at DiscoverTenant (Priority: P2)

**Goal**: When QR login enabled, users see QR button on DiscoverTenant and can authenticate via QR code

**Independent Test**: Enable QR login via platform settings, visit /DiscoverTenant, click QR button, verify QR code displays and flow works

### Implementation for User Story 2

- [ ] T011 [US2] Add IPlatformSettingsService dependency to DiscoverTenantModel constructor in `MrWhoOidc.WebAuth/Pages/DiscoverTenant.cshtml.cs`
- [ ] T012 [US2] Add ShowQrLogin boolean property to DiscoverTenantModel in `MrWhoOidc.WebAuth/Pages/DiscoverTenant.cshtml.cs`
- [ ] T013 [US2] Call IsQrLoginAtDiscoveryEnabledAsync() in OnGetAsync and set ShowQrLogin property in `MrWhoOidc.WebAuth/Pages/DiscoverTenant.cshtml.cs`
- [ ] T014 [US2] Add conditional QR login button section to DiscoverTenant page in `MrWhoOidc.WebAuth/Pages/DiscoverTenant.cshtml` with Bootstrap icon, proper styling, link to /auth/qr with returnUrl preserved
- [ ] T015 [US2] Add visual separator ("or" divider) between QR login and other auth options in `MrWhoOidc.WebAuth/Pages/DiscoverTenant.cshtml`
- [ ] T016 [US2] Verify QR button hidden when platform setting is disabled (default state)
- [ ] T017 [US2] Verify returnUrl parameter is preserved through QR login flow

**Checkpoint**: User Story 2 complete - users can initiate QR login from DiscoverTenant page

---

## Phase 4: User Story 3 - Platform Settings Page Polish (Priority: P3)

**Goal**: Platform Settings page properly integrated into admin UI with clear labeling and future extensibility

**Independent Test**: Navigate admin area, find Platform Settings in nav, verify page is clearly labeled as platform-wide

### Implementation for User Story 3

- [ ] T018 [US3] Add page header with icon and description explaining platform-wide scope in `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Settings.cshtml`
- [ ] T019 [US3] Add help text below toggle explaining what QR Login at Discovery does in `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Settings.cshtml`
- [ ] T020 [US3] Ensure consistent styling with other PlatformAdmin pages (card layout, button styles) in `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Settings.cshtml`
- [ ] T021 [US3] Add breadcrumb navigation showing Platform Admin > Settings path in `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Settings.cshtml`

**Checkpoint**: User Story 3 complete - Platform Settings page fully polished and integrated

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Tests, documentation, and validation

- [ ] T022 [P] Create PlatformSettingsServiceTests unit tests in `MrWhoOidc.UnitTests/PlatformSettingsServiceTests.cs` covering GetSettingsAsync creates default, UpdateSettingsAsync invalidates cache, IsQrLoginAtDiscoveryEnabledAsync returns correct value
- [ ] T023 [P] Create DiscoverTenantQrLoginTests integration tests in `MrWhoOidc.UnitTests/DiscoverTenantQrLoginTests.cs` covering QR button visibility based on platform setting
- [ ] T024 [P] Update admin-guide.md documentation in `docs/admin-guide.md` to include Platform Settings section
- [ ] T025 Verify zero compiler warnings in Debug and Release configurations
- [ ] T026 Run quickstart.md validation checklist from `specs/014-platform-qr-login/quickstart.md`
- [ ] T027 Verify all constitution check gates pass (domain separation, security, build quality)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies - start immediately
- **Phase 2 (US1)**: Depends on Phase 1 (T001-T006)
- **Phase 3 (US2)**: Depends on Phase 1 (T001-T006), can run parallel to US1 after Phase 1
- **Phase 4 (US3)**: Depends on Phase 2 (US1 creates the page)
- **Phase 5 (Polish)**: Depends on all user stories complete

### User Story Dependencies

| Story | Depends On | Can Start After |
|-------|------------|-----------------|
| US1 (Admin enables QR) | Phase 1 | T006 complete |
| US2 (User initiates QR) | Phase 1 | T006 complete (parallel with US1) |
| US3 (Page polish) | US1 | T010 complete |

### Parallel Opportunities

**Phase 1 parallelization**:

```text
Parallel: T001, T002, T004 (different files)
Sequential: T003 (after T001, T002), T005 (after T004), T006 (after T005)
```

**User Story parallelization**:

```text
After Phase 1:
- US1 (T007-T010) and US2 (T011-T017) can proceed in parallel
- US3 (T018-T021) must wait for US1 completion
```

**Phase 5 parallelization**:

```text
Parallel: T022, T023, T024 (different files/scopes)
```

---

## Parallel Example: Phase 1 Setup

```text
# Start together (different files):
Task T001: Create PlatformSettings entity
Task T002: Add DbSet to AuthDbContext  
Task T004: Create IPlatformSettingsService interface

# Then sequential:
Task T003: Generate migration (needs T001, T002)
Task T005: Create PlatformSettingsService (needs T004)
Task T006: Register in DI (needs T005)
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (T001-T006)
2. Complete Phase 2: User Story 1 (T007-T010)
3. **STOP and VALIDATE**: Platform admin can toggle setting ✓
4. Deploy/demo as MVP

### Incremental Delivery

1. **MVP**: Phase 1 + US1 → Admin can configure feature
2. **+US2**: Add T011-T017 → Users can use QR login
3. **+US3**: Add T018-T021 → Page is polished
4. **+Polish**: Add T022-T027 → Full quality assurance

### Task Summary

| Phase | Tasks | Parallelizable |
|-------|-------|----------------|
| Phase 1: Setup | T001-T006 (6) | 3 parallel |
| Phase 2: US1 | T007-T010 (4) | 2 parallel |
| Phase 3: US2 | T011-T017 (7) | 0 (sequential) |
| Phase 4: US3 | T018-T021 (4) | 0 (sequential) |
| Phase 5: Polish | T022-T027 (6) | 3 parallel |
| **Total** | **27 tasks** | |

---

## Notes

- [P] tasks = different files, no dependencies on incomplete tasks
- [Story] label maps task to specific user story for traceability
- Entity uses `GuidHelper.NewId()` per constitution (UUIDv7)
- Migration generated via `dotnet ef migrations add` (not hand-written)
- Platform Settings page uses existing `platform-admin` policy
- QR login reuses existing infrastructure (no changes to QrLoginHandler)
- Cache invalidation on save ensures immediate effect (SC-004)
