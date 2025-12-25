# Tasks: External IdP Registration

**Input**: Design documents from `/specs/013-external-idp-registration/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅

**Tests**: No tests explicitly requested in specification. Tests marked as optional.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3, US4)
- Include exact file paths in descriptions

## Path Conventions

- **MrWhoOidc.Auth/**: Domain logic, persistence, entities
- **MrWhoOidc.WebAuth/**: HTTP surface, Razor Pages, handlers
- **MrWhoOidc.UnitTests/**: Test files

---

## Phase 1: Setup (Schema & Entity)

**Purpose**: Add `AllowRegistration` property to IdentityProvider entity and create database migration

- [x] T001 Add `AllowRegistration` boolean property to `IdentityProvider` entity in `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`
- [x] T002 Run EF Core migration: `dotnet ef migrations add AddAllowRegistrationToIdentityProvider --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations`
- [x] T003 Verify migration file created in `MrWhoOidc.Auth/Persistence/Migrations/` and contains correct column definition

---

## Phase 2: Foundational (Export/Import & Seeding Support)

**Purpose**: Update supporting infrastructure to handle the new property across all code paths

**⚠️ CRITICAL**: These tasks ensure the new property works correctly in import/export and seeding scenarios

- [x] T004 [P] Update `IdentityProviderSeedDefinition` to include `AllowRegistration` in `MrWhoOidc.Auth/Seeding/IdentityProviderSeedDefinition.cs`
- [x] T005 [P] Update `ConfigurationExportService.ExportIdentityProviderAsync()` to export `AllowRegistration` in `MrWhoOidc.WebAuth/Services/ConfigurationExportService.cs`
- [x] T006 [P] Update `ConfigurationImportService.ImportIdentityProviderAsync()` to import `AllowRegistration` in `MrWhoOidc.WebAuth/Services/ConfigurationImportService.cs`
- [x] T007 Build solution and verify zero warnings: `dotnet build`

**Checkpoint**: Foundation ready - entity extended, migration created, import/export updated

---

## Phase 3: User Story 1 - Register via External IdP (Priority: P1) 🎯 MVP

**Goal**: Users can register by clicking an external IdP button on the registration page, authenticating with that provider, and having their account automatically created.

**Independent Test**: Configure an external IdP with `AllowRegistration=true`, visit `/Registrations`, click the IdP button, complete external authentication, verify new user account is created.

### Implementation for User Story 1

- [x] T008 [P] [US1] Create `RegistrationIdpOption` record in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [x] T009 [P] [US1] Add `RegistrationIdps` property to `IndexModel` in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [x] T010 [US1] Inject `AuthDbContext` into `IndexModel` constructor in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [x] T011 [US1] Implement `OnGetAsync()` method to query registration-enabled IdPs from default tenant in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [x] T012 [US1] Add IdP buttons section to registration page UI in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml`
- [x] T013 [US1] Style IdP buttons to match provider picker page design in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml`
- [x] T014 [US1] Construct external start URL with registration-specific returnUrl in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml`
- [x] T015 [US1] Add `mode` query parameter handling to detect IdP callback in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [x] T016 [US1] Display success message when `mode=idp_callback` indicates successful registration in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`

**Checkpoint**: At this point, User Story 1 should be fully functional - users can register via external IdP

---

## Phase 4: User Story 2 - Admin Enables IdP for Registration (Priority: P2)

**Goal**: Administrators can toggle the "Allow Registration" setting on the IdP edit page to control which IdPs appear on the registration page.

**Independent Test**: Edit an IdP in admin UI, toggle "Allow Registration" on/off, verify the IdP appears/disappears from the registration page accordingly.

### Implementation for User Story 2

- [x] T017 [P] [US2] Add `AllowRegistration` property to `InputModel` in `MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs`
- [x] T018 [US2] Map `AllowRegistration` from entity to `InputModel` in `OnGetAsync()` in `MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs`
- [x] T019 [US2] Map `AllowRegistration` from `InputModel` to entity in `OnPostAsync()` in `MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs`
- [x] T020 [US2] Add "Allow Registration" checkbox to admin provider edit form in `MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml`
- [x] T021 [US2] Add help text explaining the setting's purpose in `MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml`

**Checkpoint**: At this point, User Story 2 should be fully functional - admins can enable/disable IdP registration

---

## Phase 5: User Story 3 - Registration Form Fallback (Priority: P3)

**Goal**: When no IdPs are enabled for registration, the registration page shows only the manual form without errors or empty sections.

**Independent Test**: Disable all IdPs for registration (set `AllowRegistration=false` on all), visit `/Registrations`, verify only the manual form is displayed with no IdP section visible.

### Implementation for User Story 3

- [x] T022 [US3] Add conditional rendering to hide IdP section when `RegistrationIdps` is empty in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml`
- [x] T023 [US3] Ensure no visual artifacts (empty dividers, sections) when IdPs list is empty in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml`
- [x] T024 [US3] Verify manual registration form remains fully functional regardless of IdP configuration in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`

**Checkpoint**: At this point, User Story 3 should be fully functional - graceful degradation works

---

## Phase 6: User Story 4 - Prevent Duplicate Registration (Priority: P3)

**Goal**: When a user attempts IdP registration with an email that already exists, they receive a clear message and can navigate to sign in instead.

**Independent Test**: Create a user manually, attempt IdP registration with same email, verify error message appears with link to sign in.

### Implementation for User Story 4

- [x] T025 [US4] Add `ErrorMessage` property to `IndexModel` if not present in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [x] T026 [US4] Add `error` query parameter handling in `OnGetAsync()` to display duplicate email message in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [x] T027 [US4] Add error alert UI with "sign in instead" link in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml`
- [x] T028 [US4] Verify `ExternalOidcUserProvisioner` returns appropriate error for duplicate accounts that redirects to registration page with error parameter in `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs`

**Checkpoint**: At this point, User Story 4 should be fully functional - duplicate prevention works

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Final improvements, documentation, and validation

- [ ] T029 [P] Update quickstart documentation with actual screenshots/examples in `specs/013-external-idp-registration/quickstart.md`
- [x] T030 [P] Add logging for IdP registration flow in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [x] T031 Build and run all existing tests to verify no regressions: `dotnet test`
- [ ] T032 Apply database migration to development environment: `dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth`
- [ ] T033 Manual E2E validation following `specs/013-external-idp-registration/quickstart.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 completion (entity must exist before export/import updates)
- **User Stories (Phases 3-6)**: All depend on Phase 2 completion
  - User stories can proceed in priority order (P1 → P2 → P3)
  - US3 and US4 can run in parallel after US1/US2 (they don't depend on each other)
- **Polish (Phase 7)**: Depends on all user stories being complete

### User Story Dependencies

```text
Phase 1 (Setup)
    │
    ▼
Phase 2 (Foundational)
    │
    ├──────────────────────┐
    ▼                      ▼
Phase 3 (US1: P1)    Phase 4 (US2: P2)
    │                      │
    ├──────────────────────┤
    │                      │
    ▼                      ▼
Phase 5 (US3: P3)    Phase 6 (US4: P3)
    │                      │
    └──────────┬───────────┘
               ▼
       Phase 7 (Polish)
```

### Within Each User Story

- Models/DTOs before page logic
- Page model logic before UI changes
- Core implementation before integration

### Parallel Opportunities

**Phase 2 (Foundational)**:

```text
T004, T005, T006 can all run in parallel (different files)
```

**Phase 3 (US1)**:

```text
T008, T009 can run in parallel (both in same file but different sections)
```

**Phase 7 (Polish)**:

```text
T029, T030 can run in parallel (different files)
```

---

## Parallel Example: Phase 2 (Foundational)

```bash
# Launch all foundational tasks together:
Task T004: "Update IdentityProviderSeedDefinition in MrWhoOidc.Auth/Seeding/SeedManifest.cs"
Task T005: "Update ConfigurationExportService in MrWhoOidc.WebAuth/Services/ConfigurationExportService.cs"
Task T006: "Update ConfigurationImportService in MrWhoOidc.WebAuth/Services/ConfigurationImportService.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (T001-T003)
2. Complete Phase 2: Foundational (T004-T007)
3. Complete Phase 3: User Story 1 (T008-T016)
4. **STOP and VALIDATE**: Test external IdP registration E2E
5. Deploy/demo if ready - users can now register via IdP!

### Incremental Delivery

1. Setup + Foundational → Schema ready
2. Add User Story 1 → MVP: Users can register via external IdP
3. Add User Story 2 → Admins can control which IdPs appear
4. Add User Story 3 → Graceful degradation when no IdPs configured
5. Add User Story 4 → Duplicate email prevention
6. Each story adds value without breaking previous stories

### Suggested MVP Scope

**For fastest time-to-value**: Complete Phases 1-3 only (User Story 1)

This delivers:

- ✅ External IdP registration working
- ✅ IdP buttons on registration page
- ✅ User accounts created from IdP claims

Deferred to post-MVP:

- Admin UI for AllowRegistration (can set via DB initially)
- Graceful degradation (minor UX polish)
- Duplicate email handling (existing flow already handles this case)

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story is independently completable and testable
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- Verify build succeeds after each phase completion
