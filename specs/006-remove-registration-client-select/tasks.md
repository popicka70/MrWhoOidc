# Tasks: Remove Client Selection from User Registration

**Input**: Design documents from `/specs/006-remove-registration-client-select/`  
**Prerequisites**: plan.md ✓, spec.md ✓, research.md ✓, data-model.md ✓, contracts/ ✓

**Tests**: Included - research.md explicitly calls for tests to validate behavior changes.

**Organization**: Tasks grouped by user story. This is a **simplification feature** that removes code rather than adding it.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **WebAuth project**: `MrWhoOidc.WebAuth/`
- **Unit tests**: `MrWhoOidc.UnitTests/`

---

## Phase 1: Setup

**Purpose**: Verify current state and create backup reference

- [ ] T001 Run existing test suite to establish baseline in `MrWhoOidc.UnitTests/`
- [ ] T002 [P] Review current implementation in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`

---

## Phase 2: Foundational (No blockers for this feature)

**Purpose**: This feature has no blocking prerequisites - it's removing existing code.

**✓ No foundational tasks required** - proceed directly to user stories.

---

## Phase 3: User Story 1 - Register Without Client Selection (Priority: P1) 🎯 MVP

**Goal**: Remove client selection dropdown from registration page to eliminate exposure of database records to unauthenticated users.

**Independent Test**: Navigate to `/Registrations`, verify no client dropdown visible, complete registration successfully without selecting a client.

**Acceptance Criteria**:
- FR-001: No client selection dropdown displayed
- FR-002: No client database records exposed
- FR-008: Registration succeeds without client association

### Tests for User Story 1

- [ ] T003 [P] [US1] Add test for registration without ClientId succeeds in `MrWhoOidc.UnitTests/RegistrationServiceTests.cs`
- [ ] T004 [P] [US1] Add test verifying no client list query during registration in `MrWhoOidc.UnitTests/RegistrationPageTests.cs` (if exists, or create)

### Implementation for User Story 1

- [ ] T005 [US1] Remove `ClientId` property from `RegistrationInput` class in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [ ] T006 [US1] Remove `ClientOptions` property from `IndexModel` class in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [ ] T007 [US1] Remove `LoadClientsAsync()` method from `IndexModel` in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [ ] T008 [US1] Remove calls to `LoadClientsAsync()` from `OnGetAsync()` in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [ ] T009 [US1] Remove calls to `LoadClientsAsync()` from `OnPostCreateAsync()` in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [ ] T010 [US1] Update service call to pass `null` for clientId parameter in `OnPostCreateAsync()` in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`
- [ ] T011 [US1] Remove client dropdown div (lines ~47-55) from `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml`

**Checkpoint**: Registration page loads without client dropdown. Registration completes successfully with null ClientId.

---

## Phase 4: User Story 2 - Registration Uses Tenant from URL Path (Priority: P1)

**Goal**: Verify tenant context from URL path is correctly used (existing behavior - no code changes needed, just validation).

**Independent Test**: Navigate to `/t/default/Registrations`, complete registration, verify registration is associated with "default" tenant.

**Acceptance Criteria**:
- FR-003: Tenant context determined from URL path
- FR-004: Default tenant used when no path specified

### Tests for User Story 2

- [ ] T012 [P] [US2] Add test verifying tenant context preserved from URL path during registration in `MrWhoOidc.UnitTests/RegistrationServiceTests.cs`
- [ ] T013 [P] [US2] Add test verifying default tenant used when no path specified in `MrWhoOidc.UnitTests/RegistrationServiceTests.cs`

### Implementation for User Story 2

**No implementation changes required** - existing `ITenantAccessor` pattern already handles tenant resolution correctly. Tests validate existing behavior.

**Checkpoint**: Tenant resolution works correctly from URL path. Default tenant used when path not specified.

---

## Phase 5: User Story 3 - Self-Service Tenant Creation Remains Functional (Priority: P2)

**Goal**: Verify self-service tenant creation continues to work after client dropdown removal.

**Independent Test**: Navigate to `/Registrations`, check "Create new tenant and become admin", fill tenant details, submit, verify tenant created and user becomes admin.

**Acceptance Criteria**:
- FR-005: Optional tenant creation preserved

### Tests for User Story 3

- [ ] T014 [P] [US3] Add regression test for tenant creation during registration in `MrWhoOidc.UnitTests/RegistrationServiceTests.cs`

### Implementation for User Story 3

**No implementation changes required** - tenant creation flow is independent of client selection. Test validates no regression.

**Checkpoint**: Self-service tenant creation works as before.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final validation and cleanup

- [ ] T015 Run full test suite `dotnet test` to verify no regressions
- [ ] T016 [P] Build in Release configuration `dotnet build -c Release` to verify zero warnings
- [ ] T017 [P] Update `specs/006-remove-registration-client-select/quickstart.md` with verification results
- [ ] T018 Manual verification: Navigate to registration page and complete registration flow

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: N/A - no blockers
- **User Story 1 (Phase 3)**: Can start immediately after Setup
- **User Story 2 (Phase 4)**: Can run in parallel with US1 (tests only, no code changes)
- **User Story 3 (Phase 5)**: Can run in parallel with US1 (tests only, no code changes)
- **Polish (Phase 6)**: Depends on all user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: No dependencies - core implementation
- **User Story 2 (P1)**: Can run in parallel with US1 (test-only validation)
- **User Story 3 (P2)**: Can run in parallel with US1 (test-only validation)

### Within User Story 1

Execution order within Phase 3:

1. T003, T004: Tests (parallel) - establish expected behavior
2. T005: Remove `ClientId` property
3. T006: Remove `ClientOptions` property
4. T007: Remove `LoadClientsAsync()` method
5. T008, T009: Remove method calls (can be done together)
6. T010: Update service call
7. T011: Remove UI dropdown

### Parallel Opportunities

```
Phase 1 (Setup):
  T001 ─┬─► T002 (parallel)
        │
Phase 3 (US1):      Phase 4 (US2):      Phase 5 (US3):
  T003 ─┬─► T004      T012 ─┬─► T013      T014
        │                   │
  T005 ─┴─► T006 ─► T007 ─► T008/T009 ─► T010 ─► T011
                                                  │
Phase 6 (Polish):                                 │
  ◄───────────────────────────────────────────────┘
  T015 ─► T016 ─► T017 ─► T018
```

---

## Parallel Example: User Story 1

```bash
# Tests can run in parallel:
Task T003: "Add test for registration without ClientId succeeds"
Task T004: "Add test verifying no client list query during registration"

# User Story tests (US2, US3) can also run in parallel with US1 implementation:
Task T012: "Add test verifying tenant context preserved"
Task T013: "Add test verifying default tenant used"
Task T014: "Add regression test for tenant creation"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (T001-T002)
2. Skip Phase 2: No blockers
3. Complete Phase 3: User Story 1 (T003-T011)
4. **STOP and VALIDATE**: Test registration without client dropdown
5. ✓ MVP Complete - security vulnerability fixed

### Full Delivery

1. MVP (above)
2. Add US2 tests (T012-T013) - validates existing behavior
3. Add US3 test (T014) - validates no regression
4. Polish phase (T015-T018) - final validation

### Single Developer Strategy

Since this is a small feature:
1. Run T001 (baseline)
2. Write tests T003, T004, T012-T014 (all test files)
3. Implement T005-T011 (all in two files)
4. Run T015-T018 (validation)

---

## Notes

- This is a **code removal** feature - most tasks involve deleting code
- No database migrations required
- No new entities or APIs
- Tests validate that removed code doesn't break existing functionality
- `ClientId` field retained on Registration entity for backward compatibility
- All changes confined to `MrWhoOidc.WebAuth/Pages/Registrations/` directory
