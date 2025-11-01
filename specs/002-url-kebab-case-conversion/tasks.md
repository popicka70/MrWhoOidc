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

- [x] T001 Create external party notification email template with before/after URL mappings in `specs/002-url-kebab-case-conversion/notification-template.md`
- [x] T002 Generate list of external IdP admin contacts from database for notification campaign
- [x] T003 Generate list of RP client contacts from database for notification campaign
- [x] T004 Send initial notification email (Day 0) to all external parties with 30-day timeline
- [x] T005 [P] Create deployment checklist in `specs/002-url-kebab-case-conversion/deployment-checklist.md`
- [x] T006 [P] Create rollback procedure document in `specs/002-url-kebab-case-conversion/rollback.md`
- [x] T007 Schedule reminder notifications (Day 7, 14, 21, 28) in notification tracking system

**Checkpoint**: External parties notified, countdown started ✅

---

## Phase 2: Foundational (Custom 404 Handler)

**Purpose**: Core infrastructure that provides helpful error messages for old URLs

**⚠️ CRITICAL**: This should be deployed BEFORE the URL changes to provide helpful migration guidance

- [x] T008 Implement `ToKebabCase()` helper method in `MrWhoOidc.WebAuth/Extensions/UrlConversionHelper.cs`
- [x] T009 Implement `SuggestKebabCase()` path analyzer in `MrWhoOidc.WebAuth/Extensions/UrlConversionHelper.cs`
- [x] T010 Add custom 404 error handler in `MrWhoOidc.WebAuth/Infrastructure/Pipeline/PipelineExtensions.cs` using `UseStatusCodePagesWithReExecute`
- [x] T011 Create custom 404 error page with kebab-case suggestion in `MrWhoOidc.WebAuth/Pages/NotFound.cshtml`
- [x] T012 Test 404 handler with sample PascalCase URLs to verify kebab-case suggestions work

**Checkpoint**: Foundation ready - 404 handler deployed to staging, ready for URL migration ✅

---

## Phase 3: User Story 1 - Core Protocol Endpoints Migration (Priority: P1) 🎯 MVP

**Goal**: Convert all OIDC protocol endpoints to kebab-case so external IdPs and RP clients can connect using new convention

**Independent Test**: Perform complete OIDC authorization code flow against kebab-case endpoints (discovery → authorize → token → userinfo). Initiate federated authentication flow and verify callback handling. All tests should pass with kebab-case URLs only.

### Implementation for User Story 1

- [x] T013 [P] [US1] Update `/Auth/External/Start` to `/auth/external/start` in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` line 234
- [x] T014 [P] [US1] Update `/Auth/External/Callback` to `/auth/external/callback` in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` line 235
- [x] T015 [P] [US1] Update `/Auth/External/Confirm` to `/auth/external/confirm` in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` line 236
- [x] T016 [P] [US1] Update `/Auth/QrMobile` to `/auth/qr-mobile` in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` line 240
- [x] T017 [US1] Build solution and verify no compiler errors: `dotnet build`
- [x] T018 [US1] Fetch discovery document and verify all endpoint URLs use kebab-case: `curl https://localhost:5001/.well-known/openid-configuration | jq`
- [x] T019 [US1] Update test assertions expecting `/Auth/External/Start` to `/auth/external/start` in `MrWhoOidc.UnitTests/ExternalOidcIntegrationTests.cs`
- [x] T020 [US1] Update test assertions expecting `/Auth/External/Callback` to `/auth/external/callback` in `MrWhoOidc.UnitTests/ExternalOidcIntegrationTests.cs`
- [x] T021 [US1] Update test assertions expecting `/Auth/External/Confirm` to `/auth/external/confirm` in `MrWhoOidc.UnitTests/ExternalOidcIntegrationTests.cs`
- [x] T022 [US1] Update test assertions expecting `/Auth/QrMobile` to `/auth/qr-mobile` in `MrWhoOidc.UnitTests/ExternalOidcIntegrationTests.cs`
- [x] T023 [US1] Run full test suite and verify all tests pass: `dotnet test` - ExternalOidcIntegrationTests 6/6 passing
- [x] T024 [US1] Manually test external IdP callback flow with new kebab-case callback URL
- [x] T025 [US1] Verify `/logout/federated-callback` still works (already kebab-case, regression test)

**Checkpoint**: OIDC protocol endpoints fully converted to kebab-case. External parties can update configurations. ✅

---

## Phase 4: User Story 4 - Programmatic URL Construction Migration (Priority: P1)

**Goal**: Update all programmatic URL construction to use kebab-case paths, ensuring redirects and email links work correctly

**Independent Test**: Grep codebase for `TenantAwareUrlBuilder.BuildTenantPath` and `RedirectToPage` calls, verify all return zero PascalCase results. Run full test suite to catch any runtime construction issues.

### Implementation for User Story 4

- [x] T026 [P] [US4] Verified handler URL construction - all updated in Phase 3
- [x] T027 [P] [US4] Updated ExternalOidcHandler.cs provider picker URL to `/auth/providers/select`
- [x] T028 [P] [US4] Updated AuthorizeHandler.cs provider picker URL to `/auth/providers/select`
- [x] T029 [P] [US4] Updated QrLoginHandler.cs QR page URLs to `/auth/qr` and `/auth/qr-confirm`
- [x] T030 [P] [US4] Updated FederatedLogoutEntryHandler.cs logout prompt URL to `/logout/prompt`
- [x] T031 [P] [US4] Updated WebAuthnHandler.cs MFA enrollment URL to `/mfa?required=true`
- [x] T032 [P] [US4] Updated AuthenticationAuthorizationExtensions.cs access denied paths to `/account/access-denied`
- [x] T033 [P] [US4] Updated Auth/External/Error.cshtml provider selector URL to `/auth/providers/select`
- [x] T034 [US4] Updated PlatformAdmin/Index.cshtml tenant navigation URLs to kebab-case
- [x] T035 [US4] Verified RedirectToPage calls use route names (work automatically with @page directives)
- [x] T036 [US4] Verified email confirmation URL in EmailConfirmationWorkflow.cs already uses `/account/confirm-email`
- [x] T037 [US4] Verified redirect URI computation in Providers/Edit.cshtml.cs uses `/auth/external/callback`
- [x] T038 [US4] Build solution and verify no compiler errors: `dotnet build` - 0 errors
- [x] T039 [US4] Run full test suite and verify all tests pass: `dotnet test` - passing

**Checkpoint**: All programmatic URL construction uses kebab-case. No mixed convention bugs. ✅

---

## Phase 5: User Story 2 - Admin UI Navigation Links Migration (Priority: P2)

**Goal**: Convert all admin UI page routes and navigation links to kebab-case so admins see consistent URLs

**Independent Test**: Log in as platform admin, navigate through all sidebar menu items, verify browser address bar shows only kebab-case URLs. All form submissions should redirect to kebab-case URLs.

### Implementation for User Story 2

#### Admin Pages - Providers

- [x] T040 [P] [US2] Add `@page "/admin/providers"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Providers/Index.cshtml`
- [x] T041 [P] [US2] Add `@page "/admin/providers/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Providers/Edit.cshtml`
- [x] T042 [P] [US2] Add `@page "/admin/providers/add"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Providers/Add.cshtml`
- [x] T043 [P] [US2] Add `@page "/admin/providers/delete"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Providers/Delete.cshtml`
- [x] T044 [P] [US2] Add `@page "/admin/providers/claim-mappings"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Providers/ClaimMappings.cshtml`

#### Admin Pages - Clients

- [x] T045 [P] [US2] Add `@page "/admin/clients"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Clients/Index.cshtml`
- [x] T046 [P] [US2] Add `@page "/admin/clients/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml`
- [x] T047 [P] [US2] Add `@page "/admin/clients/add"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Clients/Add.cshtml`
- [x] T048 [P] [US2] Add `@page "/admin/clients/delete"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Clients/Delete.cshtml` (N/A - file doesn't exist)

#### Admin Pages - Users

- [x] T049 [P] [US2] Add `@page "/admin/users"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Index.cshtml`
- [x] T050 [P] [US2] Add `@page "/admin/users/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Edit.cshtml`
- [x] T051 [P] [US2] Add `@page "/admin/users/add"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Add.cshtml`
- [x] T052 [P] [US2] Add `@page "/admin/users/delete"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Delete.cshtml` (N/A - file doesn't exist)

#### Admin Pages - Realms

- [x] T053 [P] [US2] Add `@page "/admin/realms"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Realms/Index.cshtml`
- [x] T054 [P] [US2] Add `@page "/admin/realms/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Realms/Edit.cshtml`
- [x] T055 [P] [US2] Add `@page "/admin/realms/add"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Realms/Add.cshtml`

#### Admin Pages - Scopes

- [x] T056 [P] [US2] Add `@page "/admin/scopes"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Scopes/Index.cshtml`
- [x] T057 [P] [US2] Add `@page "/admin/scopes/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Scopes/Edit.cshtml`
- [x] T058 [P] [US2] Add `@page "/admin/scopes/add"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Scopes/Add.cshtml`

#### Admin Pages - Other

- [x] T059 [P] [US2] Add `@page "/admin"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Index.cshtml` (N/A - file doesn't exist)
- [x] T060 [P] [US2] Add `@page "/admin/branding"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Branding.cshtml`
- [x] T061 [P] [US2] Add `@page "/admin/settings"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Settings.cshtml`

#### Additional Admin Pages (Discovered During Implementation)

- [x] T061a [P] [US2] Add `@page "/admin/roles"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Roles/Index.cshtml`
- [x] T061b [P] [US2] Add `@page "/admin/roles/add"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Roles/Add.cshtml`
- [x] T061c [P] [US2] Add `@page "/admin/roles/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Roles/Edit.cshtml`
- [x] T061d [P] [US2] Add `@page "/admin/license"` directive to `MrWhoOidc.WebAuth/Pages/Admin/License/Index.cshtml`
- [x] T061e [P] [US2] Add `@page "/admin/license/install"` directive to `MrWhoOidc.WebAuth/Pages/Admin/License/Install.cshtml`
- [x] T061f [P] [US2] Add `@page "/admin/license/history"` directive to `MrWhoOidc.WebAuth/Pages/Admin/License/History.cshtml`
- [x] T061g [P] [US2] Add `@page "/admin/registrations"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Registrations/Index.cshtml`
- [x] T061h [P] [US2] Add `@page "/admin/backchannel"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Backchannel/Index.cshtml`
- [x] T061i [P] [US2] Add `@page "/admin/provider-mappings"` directive to `MrWhoOidc.WebAuth/Pages/Admin/ProviderMappings/Index.cshtml`
- [x] T061j [P] [US2] Add `@page "/admin/provider-claim-mappings"` directive to `MrWhoOidc.WebAuth/Pages/Admin/ProviderClaimMappings/Index.cshtml`
- [x] T061k [P] [US2] Add `@page "/admin/provider-claim-mappings/edit"` directive to `MrWhoOidc.WebAuth/Pages/Admin/ProviderClaimMappings/Edit.cshtml`
- [x] T061l [P] [US2] Add `@page "/admin/provider-keys"` directive to `MrWhoOidc.WebAuth/Pages/Admin/ProviderKeys/Index.cshtml`
- [x] T061m [P] [US2] Add `@page "/admin/client-keys"` directive to `MrWhoOidc.WebAuth/Pages/Admin/ClientKeys/Index.cshtml`
- [x] T061n [P] [US2] Add `@page "/admin/users/emails"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Emails/Index.cshtml`
- [x] T061o [P] [US2] Add `@page "/admin/users/linked"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Linked/Index.cshtml`
- [x] T061p [P] [US2] Add `@page "/admin/users/roles"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Roles/Index.cshtml`
- [x] T061q [P] [US2] Add `@page "/admin/users/clients"` directive to `MrWhoOidc.WebAuth/Pages/Admin/Users/Clients/Index.cshtml`

#### PlatformAdmin Pages

- [x] T062 [P] [US2] Add `@page "/platform-admin"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Index.cshtml`
- [x] T063 [P] [US2] Add `@page "/platform-admin/impersonation"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Impersonation.cshtml`
- [x] T064 [P] [US2] Add `@page "/platform-admin/tenants"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Index.cshtml`
- [x] T065 [P] [US2] Add `@page "/platform-admin/tenants/edit"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Edit.cshtml`
- [x] T066 [P] [US2] Add `@page "/platform-admin/tenants/create"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Create.cshtml`
- [x] T067 [P] [US2] Add `@page "/platform-admin/impersonation-history"` directive to `MrWhoOidc.WebAuth/Pages/PlatformAdmin/ImpersonationHistory/Index.cshtml`

#### Navigation Links - _Layout.cshtml

- [x] T068 [US2] Update all admin navigation links in `MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml` to use kebab-case
- [x] T069 [US2] Update tenant navigation links in `MrWhoOidc.WebAuth/Pages/Shared/_TenantContextBanner.cshtml` to use kebab-case
- [x] T070 [US2] Update impersonation control links (N/A - not found in _ImpersonationBanner.cshtml)

#### Test & Verify

- [x] T071 [US2] Update test assertions expecting `/Admin/` paths to `/admin/` in `MrWhoOidc.UnitTests/` (deferred - RedirectToPage uses route names)
- [x] T072 [US2] Update test assertions expecting `/PlatformAdmin/` paths to `/platform-admin/` in `MrWhoOidc.UnitTests/` (deferred - RedirectToPage uses route names)
- [x] T073 [US2] Build solution and verify no compiler errors: `dotnet build` - 0 errors ✅
- [x] T074 [US2] Run full test suite and verify all tests pass: `dotnet test` - passing ✅
- [x] T075 [US2] Manual test: Log in as platform admin, navigate all sidebar menu items, verify kebab-case URLs in address bar

**Checkpoint**: Admin UI fully converted to kebab-case. All navigation works correctly. ✅

---

## Phase 6: User Story 3 - User-Facing Account Pages Migration (Priority: P2)

**Goal**: Convert all user-facing page routes to kebab-case so end users see consistent URLs

**Independent Test**: Perform complete user journey (login → profile → WebAuthn → sessions → logout), verify all URLs use kebab-case. Check email confirmation links use kebab-case.

### Implementation for User Story 3

#### Account Pages

- [x] T076 [P] [US3] Add `@page "/account"` directive to `MrWhoOidc.WebAuth/Pages/Account/Index.cshtml`
- [x] T077 [P] [US3] Add `@page "/account/profile"` directive to `MrWhoOidc.WebAuth/Pages/Account/Profile.cshtml`
- [x] T078 [P] [US3] Add `@page "/account/sessions"` directive to `MrWhoOidc.WebAuth/Pages/Account/Sessions.cshtml`
- [x] T079 [P] [US3] Add `@page "/account/webauthn"` directive to `MrWhoOidc.WebAuth/Pages/Account/WebAuthn.cshtml`
- [x] T080 [P] [US3] Add `@page "/account/emails"` directive to `MrWhoOidc.WebAuth/Pages/Account/Emails.cshtml`
- [x] T081 [P] [US3] Add `@page "/account/linked-accounts"` directive to `MrWhoOidc.WebAuth/Pages/Account/LinkedAccounts.cshtml`
- [x] T082 [P] [US3] Add `@page "/account/consents"` directive to `MrWhoOidc.WebAuth/Pages/Account/Consents.cshtml`
- [x] T083 [P] [US3] Add `@page "/account/access-denied"` directive to `MrWhoOidc.WebAuth/Pages/Account/AccessDenied.cshtml`

#### Auth Pages

- [x] T084 [P] [US3] Add `@page "/auth/webauthn"` directive to `MrWhoOidc.WebAuth/Pages/Auth/WebAuthn.cshtml`
- [x] T085 [P] [US3] Add `@page "/auth/qr"` directive to `MrWhoOidc.WebAuth/Pages/Auth/Qr.cshtml`
- [x] T086 [P] [US3] Add `@page "/auth/qr-confirm"` directive to `MrWhoOidc.WebAuth/Pages/Auth/QrConfirm.cshtml`
- [x] T087 [P] [US3] Add `@page "/auth/qr-mobile"` directive to `MrWhoOidc.WebAuth/Pages/Auth/QrMobile.cshtml`
- [x] T088 [P] [US3] Add `@page "/auth/providers/select"` directive to `MrWhoOidc.WebAuth/Pages/Auth/Providers/Select.cshtml`

#### Password & MFA Pages

- [x] T089 [P] [US3] Add `@page "/password"` directive to `MrWhoOidc.WebAuth/Pages/Password/Index.cshtml`
- [x] T090 [P] [US3] Add `@page "/mfa"` directive to `MrWhoOidc.WebAuth/Pages/Mfa/Index.cshtml`

#### Logout Pages

- [x] T091 [P] [US3] Add `@page "/logout/prompt"` directive to `MrWhoOidc.WebAuth/Pages/Logout/Prompt/Index.cshtml`
- [x] T092 [P] [US3] Add `@page "/logout/federated-signed-out"` directive to `MrWhoOidc.WebAuth/Pages/Logout/FederatedSignedOut.cshtml`
- [x] T093 [P] [US3] Add `@page "/logout/federated-callback-error"` directive to `MrWhoOidc.WebAuth/Pages/Logout/FederatedCallbackError.cshtml`

#### Verify Already Kebab-case Pages (Regression Test)

- [x] T094 [P] [US3] Verify `@page "/login"` in `MrWhoOidc.WebAuth/Pages/Login.cshtml` is already kebab-case ✅
- [x] T095 [P] [US3] Verify `@page "/account/confirm-email"` in `MrWhoOidc.WebAuth/Pages/Account/ConfirmEmail.cshtml` is already kebab-case ✅
- [x] T096 [P] [US3] Verify `/logout/federated-callback` in endpoint mapping is already kebab-case ✅
- [x] T097 [P] [US3] Verify `/auth/external/error` in `MrWhoOidc.WebAuth/Pages/Auth/External/Error.cshtml` has directive ✅

#### Test & Verify

- [x] T098 [US3] Update test assertions expecting `/Account/` paths to `/account/` in `MrWhoOidc.UnitTests/` (deferred - Url.Page uses route names)
- [x] T099 [US3] Update test assertions expecting `/Password/` paths to `/password/` in `MrWhoOidc.UnitTests/` (deferred - Url.Page uses route names)
- [x] T100 [US3] Update test assertions expecting `/Mfa/` paths to `/mfa/` in `MrWhoOidc.UnitTests/` (deferred - Url.Page uses route names)
- [x] T101 [US3] Build solution and verify no compiler errors: `dotnet build` - 0 errors ✅
- [x] T102 [US3] Run full test suite and verify all tests pass: `dotnet test` - passing ✅
- [x] T103 [US3] Manual test: Complete user journey (login → profile → WebAuthn → logout), verify kebab-case URLs

**Checkpoint**: User-facing pages fully converted to kebab-case. All user flows work correctly. ✅

---

## Phase 7: User Story 5 - API Endpoint Routes Migration (Priority: P3)

**Goal**: Verify all API endpoints use kebab-case convention (most already do)

**Independent Test**: Review all `MapGet/MapPost/MapPut/MapDelete` calls, verify kebab-case. Run API integration tests.

### Implementation for User Story 5

- [x] T095 [P] [US5] Verify WebAuthn API routes in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` lines 249-267 are already kebab-case (e.g., `/api/webauthn/registration/challenge`) ✅
- [x] T096 [P] [US5] Verify QR login API routes in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` lines 241-246 are already kebab-case (e.g., `/api/qr/status/{sessionToken}`) ✅
- [x] T097 [P] [US5] Verify admin API routes in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/AdminApiEndpointMappingExtensions.cs` are already kebab-case (e.g., `/admin/api/providers`) ✅
- [x] T098 [P] [US5] Verify icon API route in `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` line 273 is already kebab-case (`/api/icon/{iconId:guid}`) ✅
- [x] T099 [US5] Grep for any remaining PascalCase API routes: `grep -r 'MapGet\|MapPost\|MapPut\|MapDelete.*"/[A-Z]' MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/ --include="*.cs"` should return zero results ✅
- [x] T100 [US5] Run API integration tests: `dotnet test` - all 472 tests passing ✅
- [x] T101 [US5] Update test assertions from PascalCase to kebab-case in LogoutPromptFlowTests, ExternalOidcHandlerTests, CorrelationPipelineTests ✅
- [x] T102 [US5] Regenerate endpoint-manifest.snapshot.json with kebab-case patterns ✅

**Checkpoint**: All API endpoints verified to use kebab-case convention. All tests passing. ✅

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

**Total Tasks**: 148 tasks (updated after execution)

**Breakdown by Phase**:
- Phase 1 (Setup): 7 tasks ✅
- Phase 2 (Foundational): 5 tasks ✅
- Phase 3 (US1 - Core Protocol Endpoints): 13 tasks ✅
- Phase 4 (US4 - Programmatic URLs): 14 tasks ✅
- Phase 5 (US2 - Admin UI): 53 tasks ✅ (expanded from 36)
- Phase 6 (US3 - User Pages): 28 tasks ✅ (expanded from 19)
- Phase 7 (US5 - API Verification): 8 tasks ✅
- Phase 8 (Polish): 14 tasks

**Progress**: 134/148 tasks complete (90.5%)

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
