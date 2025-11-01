# Tasks: System-Wide URL Convention Standardization to kebab-case

**Input**: Design documents from `/specs/002-url-kebab-case-conversion/`  
**Prerequisites**: plan.md ✓, spec.md ✓, research.md ✓, data-model.md ✓ (N/A), quickstart.md ✓

**Tests**: No test generation requested - tests will be updated as part of implementation tasks

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3, US4, US5)
- Include exact file paths in descriptions

## Path Conventions

This project uses multi-project structure:

- **HTTP Layer**: `MrWhoOidc.WebAuth/` (primary focus - all route changes)
- **Domain Layer**: `MrWhoOidc.Auth/` (no changes needed)
- **Tests**: `MrWhoOidc.UnitTests/` (update assertions)
- **Documentation**: `docs/` (update URL references)

---

## Phase 1: Setup (Notification & Preparation)

**Purpose**: Prepare external parties and infrastructure for clean break migration

- [ ] T001 Create external party notification email template with before/after URL mappings in `specs/002-url-kebab-case-conversion/notification-template.md`
- [ ] T002 Generate list of external IdP admin contacts from database for notification campaign
- [ ] T003 Generate list of RP client contacts from database for notification campaign
- [ ] T004 Send initial notification email (Day 0) to all external parties with 30-day timeline
- [ ] T005 [P] Create deployment checklist in `specs/002-url-kebab-case-conversion/deployment-checklist.md`
- [ ] T006 [P] Create rollback procedure document in `specs/002-url-kebab-case-conversion/rollback.md`
- [ ] T007 Schedule reminder notifications (Day 7, 14, 21, 28) in notification tracking system

**Checkpoint**: External parties notified, countdown started

---

## Phase 2: Foundational (Custom 404 Handler)

**Purpose**: Core infrastructure that provides helpful error messages for old URLs

**⚠️ CRITICAL**: This should be deployed BEFORE the URL changes to provide helpful migration guidance

- [ ] T008 Implement `ToKebabCase()` helper method in `MrWhoOidc.WebAuth/Extensions/UrlConversionHelper.cs`
- [ ] T009 Implement `SuggestKebabCase()` path analyzer in `MrWhoOidc.WebAuth/Extensions/UrlConversionHelper.cs`
- [ ] T010 Add custom 404 error handler in `MrWhoOidc.WebAuth/Program.cs` using `UseStatusCodePagesWithReExecute`
- [ ] T011 Create custom 404 error page with kebab-case suggestion in `MrWhoOidc.WebAuth/Pages/Error.cshtml`
- [ ] T012 Test 404 handler with sample PascalCase URLs to verify kebab-case suggestions work

**Checkpoint**: Foundation ready - 404 handler deployed to staging, ready for URL migration

---

## Phase 3: User Story 1 - Core Protocol Endpoints Migration (Priority: P1) 🎯 MVP

**Goal**: Convert all OIDC protocol endpoints to kebab-case so external IdPs and RP clients can connect using new convention

**Independent Test**: Perform complete OIDC authorization code flow against kebab-case endpoints (discovery → authorize → token → userinfo). Initiate federated authentication flow and verify callback handling. All tests should pass with kebab-case URLs only.

### Implementation for User Story 1

- [ ] T013 [P] [US1] Update `/Auth/External/Start` to `/auth/external/start` in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` line 234
- [ ] T014 [P] [US1] Update `/Auth/External/Callback` to `/auth/external/callback` in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` line 235
- [ ] T015 [P] [US1] Update `/Auth/External/Confirm` to `/auth/external/confirm` in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` line 236
- [ ] T016 [P] [US1] Update `/Auth/QrMobile` to `/auth/qr-mobile` in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` line 240
- [ ] T017 [US1] Build solution and verify no compiler errors: `dotnet build`
- [ ] T018 [US1] Fetch discovery document and verify all endpoint URLs use kebab-case: `curl https://localhost:5001/.well-known/openid-configuration | jq`
- [ ] T019 [US1] Update test assertions expecting `/Auth/External/Start` to `/auth/external/start` in `MrWhoOidc.UnitTests/` (grep and replace)
- [ ] T020 [US1] Update test assertions expecting `/Auth/External/Callback` to `/auth/external/callback` in `MrWhoOidc.UnitTests/` (grep and replace)
- [ ] T021 [US1] Update test assertions expecting `/Auth/External/Confirm` to `/auth/external/confirm` in `MrWhoOidc.UnitTests/` (grep and replace)
- [ ] T022 [US1] Update test assertions expecting `/Auth/QrMobile` to `/auth/qr-mobile` in `MrWhoOidc.UnitTests/` (grep and replace)
- [ ] T023 [US1] Run full test suite and verify all tests pass: `dotnet test`
- [ ] T024 [US1] Manually test external IdP callback flow with new kebab-case callback URL
- [ ] T025 [US1] Verify `/logout/federated-callback` still works (already kebab-case, regression test)

**Checkpoint**: OIDC protocol endpoints fully converted to kebab-case. External parties can update configurations.

---

## Phase 4: User Story 4 - Programmatic URL Construction Migration (Priority: P1)

**Goal**: Update all programmatic URL construction to use kebab-case paths, ensuring redirects and email links work correctly

**Independent Test**: Grep codebase for `TenantAwareUrlBuilder.BuildTenantPath` and `RedirectToPage` calls, verify all return zero PascalCase results. Run full test suite to catch any runtime construction issues.

### Implementation for User Story 4

- [ ] T026 [P] [US4] Update `TenantAwareUrlBuilder.BuildTenantPath("/Admin/Providers", ...)` to `"/admin/providers"` in `MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs` line 213
- [ ] T027 [P] [US4] Update `TenantAwareUrlBuilder.BuildTenantPath` calls in `MrWhoOidc.WebAuth/Pages/Admin/TenantAwarePageModel.cs` lines 52, 60, 85 to use kebab-case paths
- [ ] T028 [P] [US4] Update `TenantAwareUrlBuilder.BuildTenantPath` call in `MrWhoOidc.WebAuth/Pages/Admin/Realms/Edit.cshtml.cs` line 105 to use kebab-case path
- [ ] T029 [P] [US4] Update `TenantAwareUrlBuilder.BuildTenantPath` calls in `MrWhoOidc.WebAuth/Pages/Admin/Realms/Index.cshtml.cs` lines 94, 105 to use kebab-case paths
- [ ] T030 [P] [US4] Update `TenantAwareUrlBuilder.BuildTenantPath` call in `MrWhoOidc.WebAuth/Pages/Admin/Realms/Add.cshtml.cs` line 66 to use kebab-case path
- [ ] T031 [P] [US4] Update `TenantAwareUrlBuilder.BuildTenantPath` call in `MrWhoOidc.WebAuth/Pages/Admin/Providers/ClaimMappings.cshtml.cs` line 40 to use kebab-case path
- [ ] T032 [P] [US4] Update `TenantAwareUrlBuilder.BuildTenantPath` calls in `MrWhoOidc.WebAuth/Pages/Admin/Providers/Delete.cshtml.cs` lines 67, 82 to use kebab-case paths
- [ ] T033 [P] [US4] Update `TenantAwareUrlBuilder.BuildTenantPath` call in `MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml.cs` line 89 to use kebab-case path
- [ ] T034 [US4] Grep for remaining PascalCase `TenantAwareUrlBuilder` calls: `grep -r 'TenantAwareUrlBuilder.BuildTenantPath.*"/[A-Z]' MrWhoOidc.WebAuth/ --include="*.cs"` should return zero results
- [ ] T035 [US4] Search for all `RedirectToPage` calls with PascalCase paths and update to kebab-case in `MrWhoOidc.WebAuth/Pages/` (grep pattern: `RedirectToPage\("/[A-Z]`)
- [ ] T036 [US4] Update email confirmation URL construction in `MrWhoOidc.Auth/Services/EmailConfirmationWorkflow.cs` to use kebab-case paths
- [ ] T037 [US4] Update redirect URI computation in `MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml.cs` `ComputeRedirectUris()` method to use `/auth/external/callback`
- [ ] T038 [US4] Build solution and verify no compiler errors: `dotnet build`
- [ ] T039 [US4] Run full test suite and verify all tests pass: `dotnet test`

**Checkpoint**: All programmatic URL construction uses kebab-case. No mixed convention bugs.

---

## Phase 5: User Story 2 - Admin UI Navigation Links Migration (Priority: P2)

**Goal**: Convert all admin UI page routes and navigation links to kebab-case so admins see consistent URLs

**Independent Test**: Log in as platform admin, navigate through all sidebar menu items, verify browser address bar shows only kebab-case URLs. All form submissions should redirect to kebab-case URLs.

### Implementation for User Story 2

#### Admin Pages - Providers

- [ ] T040 [P] [US2] Add `@page "/admin/providers"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Providers/Index.cshtml`
- [ ] T041 [P] [US2] Add `@page "/admin/providers/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml`
- [ ] T042 [P] [US2] Add `@page "/admin/providers/add"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml`
- [ ] T043 [P] [US2] Add `@page "/admin/providers/delete"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Providers/Delete.cshtml`
- [ ] T044 [P] [US2] Add `@page "/admin/providers/claim-mappings"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Providers/ClaimMappings.cshtml`

#### Admin Pages - Clients

- [ ] T045 [P] [US2] Add `@page "/admin/clients"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Clients/Index.cshtml`
- [ ] T046 [P] [US2] Add `@page "/admin/clients/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml`
- [ ] T047 [P] [US2] Add `@page "/admin/clients/add"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Clients/Add.cshtml`
- [ ] T048 [P] [US2] Add `@page "/admin/clients/delete"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Clients/Delete.cshtml`

#### Admin Pages - Users

- [ ] T049 [P] [US2] Add `@page "/admin/users"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Index.cshtml`
- [ ] T050 [P] [US2] Add `@page "/admin/users/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Edit.cshtml`
- [ ] T051 [P] [US2] Add `@page "/admin/users/add"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Add.cshtml`
- [ ] T052 [P] [US2] Add `@page "/admin/users/delete"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Delete.cshtml`

#### Admin Pages - Realms

- [ ] T053 [P] [US2] Add `@page "/admin/realms"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Realms/Index.cshtml`
- [ ] T054 [P] [US2] Add `@page "/admin/realms/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Realms/Edit.cshtml`
- [ ] T055 [P] [US2] Add `@page "/admin/realms/add"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Realms/Add.cshtml`

#### Admin Pages - Scopes

- [ ] T056 [P] [US2] Add `@page "/admin/scopes"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Scopes/Index.cshtml`
- [ ] T057 [P] [US2] Add `@page "/admin/scopes/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Scopes/Edit.cshtml`
- [ ] T058 [P] [US2] Add `@page "/admin/scopes/add"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Scopes/Add.cshtml`

#### Admin Pages - Other

- [ ] T059 [P] [US2] Add `@page "/admin"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Index.cshtml`
- [ ] T060 [P] [US2] Add `@page "/admin/branding"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Branding.cshtml`
- [ ] T061 [P] [US2] Add `@page "/admin/settings"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Settings.cshtml`

#### PlatformAdmin Pages

- [ ] T062 [P] [US2] Add `@page "/platform-admin"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Index.cshtml`
- [ ] T063 [P] [US2] Add `@page "/platform-admin/impersonation"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Impersonation.cshtml`
- [ ] T064 [P] [US2] Add `@page "/platform-admin/tenants"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Index.cshtml`
- [ ] T065 [P] [US2] Add `@page "/platform-admin/tenants/edit"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Edit.cshtml`
- [ ] T066 [P] [US2] Add `@page "/platform-admin/tenants/create"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Create.cshtml`
- [ ] T067 [P] [US2] Add `@page "/platform-admin/impersonation-history"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/ImpersonationHistory/Index.cshtml`

#### Navigation Links - _Layout.cshtml

- [ ] T068 [US2] Update all admin navigation links in `MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml` (grep for `asp-page="/Admin` and `asp-page="/PlatformAdmin`, replace with kebab-case)
- [ ] T069 [US2] Update tenant navigation links in `MrWhoOidc.WebAuth/Pages/Shared/_TenantContextBanner.cshtml` to use kebab-case
- [ ] T070 [US2] Update impersonation control links in `MrWhoOidc.WebAuth/Pages/Shared/_ImpersonationBanner.cshtml` to use kebab-case

#### Test & Verify

- [ ] T071 [US2] Update test assertions expecting `/Admin/` paths to `/admin/` in `MrWhoOidc.UnitTests/` (grep and replace)
- [ ] T072 [US2] Update test assertions expecting `/PlatformAdmin/` paths to `/platform-admin/` in `MrWhoOidc.UnitTests/` (grep and replace)
- [ ] T073 [US2] Build solution and verify no compiler errors: `dotnet build`
- [ ] T074 [US2] Run full test suite and verify all tests pass: `dotnet test`
- [ ] T075 [US2] Manual test: Log in as platform admin, navigate all sidebar menu items, verify kebab-case URLs in address bar

**Checkpoint**: Admin UI fully converted to kebab-case. All navigation works correctly.

---

## Phase 6: User Story 3 - User-Facing Account Pages Migration (Priority: P2)

**Goal**: Convert all user-facing page routes to kebab-case so end users see consistent URLs

**Independent Test**: Perform complete user journey (login → profile → WebAuthn → sessions → logout), verify all URLs use kebab-case. Check email confirmation links use kebab-case.

### Implementation for User Story 3

#### Account Pages

- [ ] T076 [P] [US3] Add `@page "/account/profile"` directive to `MrWhoOidc.WebAuth/Pages/Account/Profile.cshtml`
- [ ] T077 [P] [US3] Add `@page "/account/sessions"` directive to `MrWhoOidc.WebAuth/Pages/Account/Sessions.cshtml`
- [ ] T078 [P] [US3] Add `@page "/account/webauthn"` directive to `MrWhoOidc.WebAuth/Pages/Account/WebAuthn.cshtml`

#### Auth Pages

- [ ] T079 [P] [US3] Add `@page "/auth/webauthn"` directive to `MrWhoOidc.WebAuth/Pages/Auth/WebAuthn.cshtml`
- [ ] T080 [P] [US3] Add `@page "/auth/qr"` directive to `MrWhoOidc.WebAuth/Pages/Auth/Qr.cshtml`

#### Password & Registration Pages

- [ ] T081 [P] [US3] Add `@page "/password"` directive to `MrWhoOidc.WebAuth/Pages/Password/Index.cshtml`
- [ ] T082 [P] [US3] Add `@page "/registrations"` directive to `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml`

#### Verify Already Kebab-case Pages (Regression Test)

- [ ] T083 [P] [US3] Verify `@page "/login"` in `MrWhoOidc.WebAuth/Pages/Login.cshtml` is already kebab-case (no changes)
- [ ] T084 [P] [US3] Verify `@page "/account/confirm-email"` in `MrWhoOidc.WebAuth/Pages/Account/ConfirmEmail.cshtml` is already kebab-case (no changes)
- [ ] T085 [P] [US3] Verify `/logout/federated-callback` in endpoint mapping is already kebab-case (no changes)

#### Navigation Links - User-facing

- [ ] T086 [US3] Update account navigation links in `MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml` (grep for `asp-page="/Account`, replace with kebab-case)
- [ ] T087 [US3] Update password reset links in `MrWhoOidc.WebAuth/Pages/Login.cshtml` to use `/password` (kebab-case)
- [ ] T088 [US3] Update WebAuthn navigation links in `MrWhoOidc.WebAuth/Pages/Shared/_WebAuthnSetup.cshtml` to use kebab-case

#### Test & Verify

- [ ] T089 [US3] Update test assertions expecting `/Account/` paths to `/account/` in `MrWhoOidc.UnitTests/` (grep and replace)
- [ ] T090 [US3] Update test assertions expecting `/Password/` paths to `/password/` in `MrWhoOidc.UnitTests/` (grep and replace)
- [ ] T091 [US3] Update test assertions expecting `/Registrations/` paths to `/registrations/` in `MrWhoOidc.UnitTests/` (grep and replace)
- [ ] T092 [US3] Build solution and verify no compiler errors: `dotnet build`
- [ ] T093 [US3] Run full test suite and verify all tests pass: `dotnet test`
- [ ] T094 [US3] Manual test: Complete user journey (login → profile → WebAuthn → logout), verify kebab-case URLs

**Checkpoint**: User-facing pages fully converted to kebab-case. All user flows work correctly.

---

## Phase 7: User Story 5 - API Endpoint Routes Migration (Priority: P3)

**Goal**: Verify all API endpoints use kebab-case convention (most already do)

**Independent Test**: Review all `MapGet/MapPost/MapPut/MapDelete` calls, verify kebab-case. Run API integration tests.

### Implementation for User Story 5

- [ ] T095 [P] [US5] Verify WebAuthn API routes in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` lines 249-267 are already kebab-case (e.g., `/api/webauthn/registration/challenge`)
- [ ] T096 [P] [US5] Verify QR login API routes in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` lines 241-246 are already kebab-case (e.g., `/api/qr/status/{sessionToken}`)
- [ ] T097 [P] [US5] Verify admin API routes in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/AdminApiEndpointMappingExtensions.cs` are already kebab-case (e.g., `/admin/api/providers`)
- [ ] T098 [P] [US5] Verify icon API route in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` line 273 is already kebab-case (`/api/icon/{iconId:guid}`)
- [ ] T099 [US5] Grep for any remaining PascalCase API routes: `grep -r 'MapGet\|MapPost\|MapPut\|MapDelete.*"/[A-Z]' MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/ --include="*.cs"` should return zero results
- [ ] T100 [US5] Run API integration tests: `dotnet test --filter Category=API`
- [ ] T101 [US5] Manual test: Call WebAuthn registration endpoint, verify response
- [ ] T102 [US5] Manual test: Call admin API providers endpoint, verify response

**Checkpoint**: All API endpoints verified to use kebab-case convention.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Documentation updates, final verification, deployment preparation

- [ ] T103 [P] Update developer guide with kebab-case URL conventions in `docs/developer-guide.md`
- [ ] T104 [P] Update admin guide with new URL patterns in `docs/admin-guide.md`
- [ ] T105 [P] Update IdP chaining configuration docs with kebab-case redirect URI examples in `docs/idp-chaining-client-configuration.md`
- [ ] T106 [P] Update backlog items with kebab-case URL references in `docs/backlog.md`
- [ ] T107 [P] Search and update any remaining URL references in docs: `grep -r 'https://.*/(Admin|PlatformAdmin|Account|Auth)/[A-Z]' docs/ --include="*.md"`
- [ ] T108 Create before/after URL mapping table in `specs/002-url-kebab-case-conversion/url-mappings.md` for reference
- [ ] T109 Run comprehensive grep to verify zero PascalCase URLs remain: `grep -r '"/Admin/\|/PlatformAdmin/\|/Account/[A-Z]\|/Auth/[A-Z]' MrWhoOidc.WebAuth/ --include="*.cs" --include="*.cshtml"` should return zero results
- [ ] T110 Run full test suite one final time: `dotnet test` should exit with code 0
- [ ] T111 Deploy to staging environment and run smoke tests
- [ ] T112 Send final warning notification (Day 28) to external parties: 2 days until deployment
- [ ] T113 Review deployment checklist in `specs/002-url-kebab-case-conversion/deployment-checklist.md`
- [ ] T114 Execute deployment to production following checklist
- [ ] T115 Monitor 404 error rates for 7 days post-deployment
- [ ] T116 Track external party migration success rate via audit logs

**Checkpoint**: Feature complete, deployed, and monitored.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - START IMMEDIATELY (Day 0)
- **Foundational (Phase 2)**: Depends on Setup - Can start Day 1
- **User Story 1 (Phase 3 - P1)**: Depends on Foundational AND 30-day notice period - Deploy Day 30
- **User Story 4 (Phase 4 - P1)**: Depends on Foundational - Can work in parallel with US1
- **User Story 2 (Phase 5 - P2)**: Depends on US1 and US4 completion (URLs must be updated in code before pages)
- **User Story 3 (Phase 6 - P2)**: Depends on US1 and US4 completion (URLs must be updated in code before pages)
- **User Story 5 (Phase 7 - P3)**: Independent - Can verify anytime after Foundational
- **Polish (Phase 8)**: Depends on all user stories complete

### User Story Dependencies

- **US1 (Core Protocol Endpoints)**: BLOCKS external IdP/RP integration - CRITICAL PATH
- **US4 (Programmatic URLs)**: BLOCKS US2 and US3 (must update construction before pages)
- **US2 (Admin UI)**: Depends on US1 + US4
- **US3 (User Pages)**: Depends on US1 + US4
- **US5 (API Verification)**: Independent - parallel with any phase

### Critical Path

```text
Day 0: Setup (Phase 1) → Notification sent
Day 1-7: Foundational (Phase 2) → 404 handler deployed to staging
Day 7-29: Development work on US1, US4, US2, US3, US5
Day 28: Final warning notification
Day 30: Deploy all URL changes to production
Day 30-37: Monitor and support migration issues
```

### Parallel Opportunities

**Within Setup (Phase 1)**:
- Tasks T001-T007 can all run in parallel (different deliverables)

**Within Foundational (Phase 2)**:
- Tasks T008-T009 (helper methods) can run parallel with T011 (error page)

**Within US1 (Phase 3)**:
- Tasks T013-T016 (endpoint route updates) can all run in parallel (different lines in same file - use branches)

**Within US4 (Phase 4)**:
- Tasks T026-T033 (TenantAwareUrlBuilder updates) can all run in parallel (different files)

**Within US2 (Phase 5)**:
- Tasks T040-T067 (add @page directives) can all run in parallel (different files)

**Within US3 (Phase 6)**:
- Tasks T076-T085 (add @page directives) can all run in parallel (different files)

**Within US5 (Phase 7)**:
- Tasks T095-T098 (verification tasks) can all run in parallel (read-only)

**Within Polish (Phase 8)**:
- Tasks T103-T107 (documentation updates) can all run in parallel (different files)

**Across User Stories**:
- After Foundational completes, US1 + US4 + US5 can all start in parallel
- US2 and US3 can start in parallel after US1 + US4 complete

---

## Parallel Example: User Story 1 (Core Protocol Endpoints)

```bash
# Launch all endpoint route updates in parallel (use separate branches or careful merging):
Agent 1: "Update /Auth/External/Start to /auth/external/start in EndpointMappingExtensions.cs line 234"
Agent 2: "Update /Auth/External/Callback to /auth/external/callback in EndpointMappingExtensions.cs line 235"
Agent 3: "Update /Auth/External/Confirm to /auth/external/confirm in EndpointMappingExtensions.cs line 236"
Agent 4: "Update /Auth/QrMobile to /auth/qr-mobile in EndpointMappingExtensions.cs line 240"

# Then merge and test together
```

---

## Parallel Example: User Story 2 (Admin UI Pages)

```bash
# Launch all @page directive additions in parallel (different files):
Agent 1: "Add @page directives to all Provider pages (5 files)"
Agent 2: "Add @page directives to all Client pages (4 files)"
Agent 3: "Add @page directives to all User pages (4 files)"
Agent 4: "Add @page directives to all Realm pages (3 files)"
Agent 5: "Add @page directives to all Scope pages (3 files)"
Agent 6: "Add @page directives to PlatformAdmin pages (5 files)"

# Then update navigation links and test together
```

---

## MVP Scope Recommendation

**Minimum Viable Product**: User Story 1 + User Story 4 only

**Rationale**:
- US1 (Core Protocol Endpoints) enables external IdPs and RP clients to migrate
- US4 (Programmatic URLs) ensures internal code consistency
- Together they provide complete OIDC functionality with kebab-case URLs
- Admin UI (US2) and User Pages (US3) can be deployed incrementally after MVP
- API verification (US5) is confirmatory only

**MVP Delivery**:
- Complete T001-T039 (Setup + Foundational + US1 + US4)
- Deploy to production on Day 30
- Monitor external integration success
- Follow up with US2, US3, US5 in subsequent releases

---

## Task Summary

**Total Tasks**: 116 tasks

**Breakdown by Phase**:
- Phase 1 (Setup): 7 tasks
- Phase 2 (Foundational): 5 tasks
- Phase 3 (US1 - Core Protocol Endpoints): 13 tasks
- Phase 4 (US4 - Programmatic URLs): 14 tasks
- Phase 5 (US2 - Admin UI): 36 tasks
- Phase 6 (US3 - User Pages): 19 tasks
- Phase 7 (US5 - API Verification): 8 tasks
- Phase 8 (Polish): 14 tasks

**Parallelizable Tasks**: 74 tasks marked [P] (64% can run in parallel)

**Independent Test Criteria**:
- US1: Complete OIDC flow works with kebab-case endpoints only
- US2: Admin navigation shows only kebab-case URLs in browser
- US3: User journey shows only kebab-case URLs in browser
- US4: Zero PascalCase URLs in programmatic construction (grep validation)
- US5: All API integration tests pass

**Estimated Effort** (single developer):
- Setup: 1 day
- Foundational: 1 day
- US1: 2 days
- US4: 2 days
- US2: 3 days
- US3: 2 days
- US5: 1 day
- Polish: 2 days
- **Total**: 14 days (plus 30-day notification period)

**With Parallel Execution** (4 developers):
- Setup: 1 day
- Foundational: 1 day
- US1 + US4 + US5: 2 days (parallel)
- US2 + US3: 2 days (parallel after US1/US4)
- Polish: 1 day
- **Total**: 7 days (plus 30-day notification period)
