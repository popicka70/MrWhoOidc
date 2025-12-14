---
description: "Task list for Identity Provider Configuration Form"
---

# Tasks: Identity Provider Configuration Form

**Input**: Design documents from `/specs/009-provider-form-ui/`
**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Tests are OPTIONAL and are not included as implementation tasks because the feature specification does not explicitly request a TDD approach.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`
- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Descriptions include exact file paths

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Confirm scope and mapping against existing code before changes

- [ ] T001 Review current provider add/edit UI in MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml and MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml
- [ ] T001 [P] Review current provider add/edit UI in MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml and MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml
- [ ] T002 [P] Confirm standard OIDC field list matches current schema in MrWhoOidc.Auth/IdentityProviders/OidcProviderConfig.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Shared building blocks used by all user stories

**⚠️ CRITICAL**: No user story work should start until this phase is complete

- [ ] T003 [P] Create JSON merge utility to preserve unknown keys in MrWhoOidc.Auth/IdentityProviders/OidcProviderConfigJsonMerger.cs
- [ ] T004 [P] Extract shared OIDC form model into MrWhoOidc.WebAuth/Pages/Admin/Providers/OidcConfigForm.cs (used by both Add and Edit)
- [ ] T005 [P] Create Razor partial for standard OIDC inputs in MrWhoOidc.WebAuth/Pages/Admin/Providers/_OidcStandardConfig.cshtml
- [ ] T006 [P] Create Razor partial for advanced config JSON input in MrWhoOidc.WebAuth/Pages/Admin/Providers/_OidcAdvancedConfig.cshtml

**Checkpoint**: Foundation ready — user story implementation can now begin

---

## Phase 3: User Story 1 - Add OIDC provider using guided form (Priority: P1) 🎯 MVP

**Goal**: Admin can create an OIDC provider using standard inputs (no hand-written JSON required).

**Independent Test**: Use the Add Provider page to create a provider with only Authority + Client ID and verify field-level validation blocks bad inputs.

### Implementation for User Story 1

- [ ] T007 [US1] Add OIDC form binding to MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml.cs (bind OidcConfigForm + optional advanced JSON)
- [ ] T008 [US1] Replace raw config textarea-first UX with standard form partials in MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml
- [ ] T009 [US1] Build stored config JSON from standard inputs (and optional advanced) in MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml.cs
- [ ] T010 [US1] Implement field-level validation feedback on Add save in MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml.cs

**Checkpoint**: US1 complete — new OIDC provider can be created without JSON

---

## Phase 4: User Story 2 - Edit provider without breaking existing configurations (Priority: P2)

**Goal**: Admin can edit standard settings without losing unknown/extended config and without exposing or accidentally clearing secrets.

**Independent Test**: Edit a provider that contains an extra/unknown key in config and verify the key remains after saving standard field changes; verify leaving secret blank does not change it.

### Implementation for User Story 2

- [ ] T011 [P] [US2] Refactor OIDC edit model to use shared OidcConfigForm in MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs
- [ ] T012 [P] [US2] Render standard + advanced partials in MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml
- [ ] T013 [US2] Implement merge-on-save to preserve unknown keys using MrWhoOidc.Auth/IdentityProviders/OidcProviderConfigJsonMerger.cs in MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs
- [ ] T014 [US2] Implement safe secret update semantics (blank = unchanged; do not display existing secret) in MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml and MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs
- [ ] T015 [US2] Handle invalid stored config by avoiding auto-rewrite and allowing explicit overwrite via advanced JSON in MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs
- [ ] T016 [US2] Prevent secret disclosure in provider details view by redacting sensitive fields from display in MrWhoOidc.WebAuth/Pages/Admin/Providers/Details.cshtml.cs and MrWhoOidc.WebAuth/Pages/Admin/Providers/Details.cshtml

**Checkpoint**: US2 complete — edits preserve unknown keys and keep secrets safe

---

## Phase 5: User Story 3 - Use advanced configuration only when needed (Priority: P3)

**Goal**: Admin can optionally provide extended parameters via advanced JSON with clear validation and deterministic conflict handling.

**Independent Test**: Add/edit provider with advanced JSON containing an extra key and verify it is saved; try an invalid JSON payload and confirm save is blocked with a clear error.

### Implementation for User Story 3

- [ ] T017 [P] [US3] Define advanced JSON semantics as an “extended properties” JSON object and document help text in MrWhoOidc.WebAuth/Pages/Admin/Providers/_OidcAdvancedConfig.cshtml
- [ ] T018 [P] [US3] Validate advanced JSON syntax and require JSON object shape in MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml.cs and MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs
- [ ] T019 [US3] Detect conflicts where advanced JSON sets a standard field and block save with clear message in MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml.cs and MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs

**Checkpoint**: US3 complete — advanced config is optional, validated, and conflict-safe

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Quality, docs, and end-to-end verification

- [ ] T020 [P] Update admin documentation for the new provider form and advanced JSON guidance in docs/admin-guide.md
- [ ] T021 Update quickstart verification steps if needed in specs/009-provider-form-ui/quickstart.md
- [ ] T022 Run end-to-end validation by executing dotnet test for MrWhoOidc.slnx and fix any warnings/regressions in modified files

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **User Stories (Phase 3+)**: Depend on Foundational completion; implement in priority order for MVP (US1 → US2 → US3)
- **Polish (Phase 6)**: Depends on completed user stories targeted for the release

### User Story Dependencies

- **US1 (P1)**: Depends on Phase 2 only
- **US2 (P2)**: Depends on Phase 2; may build on shared components from US1
- **US3 (P3)**: Depends on Phase 2; integrates with Add/Edit flows

---

## Parallel Execution Examples

### Setup

- T001 and T002 can be done in parallel.

### Foundational

- T003, T004, T005, T006 can be done in parallel (different files).

### US1

- After T007, T008 can proceed in parallel with T009 (UI vs server mapping), then complete with T010.

### US2

- T011 and T012 can proceed in parallel, then T013–T016 in sequence (merge + secret handling + invalid config + details redaction).

### US3

- T017 can run in parallel with T018, then complete with T019.

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 and Phase 2
2. Complete Phase 3 (US1)
3. Validate Add flow manually (see specs/009-provider-form-ui/quickstart.md)

### Incremental Delivery

1. US1 (Add form)
2. US2 (Edit preservation + secret safety)
3. US3 (Advanced JSON optional + conflict handling)
4. Phase 6 polish
