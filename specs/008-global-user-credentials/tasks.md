# Tasks: Global User Credentials

**Input**: Design documents from `/specs/008-global-user-credentials/`
**Prerequisites**: ✅ plan.md, ✅ spec.md, ✅ research.md, ✅ data-model.md, ✅ contracts/service-contracts.md, ✅ quickstart.md

## Format: `[ID] [P?] [Story] Description`
- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions (per plan.md)
- **Auth Layer**: `MrWhoOidc.Auth/` - core domain logic
- **Web Layer**: `MrWhoOidc.WebAuth/` - HTTP endpoints, Razor Pages
- **Tests**: `MrWhoOidc.UnitTests/` - unit and integration tests

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Database schema changes and project structure updates

- [ ] T001 Add new fields to UserAccount entity in `MrWhoOidc.Auth/Persistence/Entities/UserAccount.cs`
  - `FailedLoginAttempts` (int, default 0)
  - `LastFailedLoginAt` (DateTimeOffset?)
  - `PasswordUpdatedAt` (DateTimeOffset?)
- [ ] T002 Add EF Core migration for UserAccount schema changes
  - Run: `dotnet ef migrations add AddGlobalAuthFieldsToUserAccount --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations`
- [ ] T003 [P] Add index on `Email` column for UserAccount (case-insensitive, unique) in migration
- [ ] T004 [P] Create `GlobalAuthenticationResult.cs` record in `MrWhoOidc.Auth/Services/`
  - Include `AuthenticationFailureReason` enum in same file

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core services that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [ ] T005 Create `IGlobalAuthenticationService.cs` interface in `MrWhoOidc.Auth/Services/`
  - Methods: `AuthenticateAsync`, `RecordFailedAttemptAsync`, `ClearFailedAttemptsAsync`, `IsLockedOutAsync`
- [ ] T006 Extend `IUserAccountService.cs` with new methods in `MrWhoOidc.Auth/Services/`
  - `FindByEmailAsync(string email)`
  - `UpdatePasswordAsync(Guid userId, string newPasswordHash)`
  - `GetActiveMembershipsAsync(Guid userId)`
  - `UpdateLockoutAsync(Guid userId, int failedAttempts, DateTimeOffset? lastFailedAt, DateTimeOffset? lockedUntil)`
- [ ] T007 Implement `GlobalAuthenticationService.cs` in `MrWhoOidc.Auth/Services/`
  - Inject `IUserAccountService`, `IPasswordHasher`
  - Implement lockout logic per data-model.md state machine
  - Lockout threshold: 5 attempts, duration: 15 minutes
- [ ] T008 Implement extended methods in `UserAccountService.cs` for `IUserAccountService`
- [ ] T009 Register `IGlobalAuthenticationService` in DI container in `MrWhoOidc.Auth/Extensions/ServiceCollectionExtensions.cs`
- [ ] T010 [P] Add `OidcMetrics` counters for global authentication events
  - `oidc_global_auth_success_total`
  - `oidc_global_auth_failure_total`
  - `oidc_global_account_lockout_total`

**Checkpoint**: Foundation ready - user story implementation can now begin

---

## Phase 3: User Story 1 - Single Password Across All Tenants (Priority: P1) 🎯 MVP

**Goal**: Users can log in to any tenant using the same credentials stored in UserAccount

**Independent Test**: User logs into tenant A, then tenant B with same email/password - both succeed

### Tests for User Story 1

- [ ] T011 [P] [US1] Unit test: `GlobalAuthenticationServiceTests.cs` in `MrWhoOidc.UnitTests/Services/`
  - `AuthenticateAsync_ValidCredentials_ReturnsSuccess`
  - `AuthenticateAsync_InvalidPassword_ReturnsFailure`
  - `AuthenticateAsync_UserNotFound_ReturnsUserNotFound`
  - `AuthenticateAsync_NoActiveMemberships_ReturnsNoActiveMemberships`
- [ ] T012 [P] [US1] Integration test: `GlobalAuthenticationIntegrationTests.cs` in `MrWhoOidc.UnitTests/Integration/`
  - `User_CanLoginToMultipleTenants_WithSameCredentials`

### Implementation for User Story 1

- [ ] T013 [US1] Modify `Login.cshtml.cs` in `MrWhoOidc.WebAuth/Pages/` to use `IGlobalAuthenticationService`
  - Replace call to `IUserService.ValidateCredentialsAsync` with `IGlobalAuthenticationService.AuthenticateAsync`
  - Handle `GlobalAuthenticationResult` outcomes (success, locked, invalid password)
  - Display appropriate error messages for each failure reason
- [ ] T014 [US1] Update login error messages in `Login.cshtml` for global auth failures
  - Account locked message with retry time
  - Invalid credentials message (generic for security)
- [ ] T015 [US1] Add logging for authentication attempts in `GlobalAuthenticationService`
  - Log email (hashed for PII), tenant context, success/failure reason

**Checkpoint**: User Story 1 complete - users can login with global credentials

---

## Phase 4: User Story 2 - Password Change Propagates Globally (Priority: P1)

**Goal**: When user changes password, it applies to all tenants immediately

**Independent Test**: User changes password in tenant A, can login to tenant B with new password

### Tests for User Story 2

- [ ] T016 [P] [US2] Unit test: `UserAccountServiceTests.cs` additions in `MrWhoOidc.UnitTests/Services/`
  - `UpdatePasswordAsync_UpdatesPasswordHash`
  - `UpdatePasswordAsync_SetsPasswordUpdatedAt`
  - `UpdatePasswordAsync_ClearsLockoutState`
- [ ] T017 [P] [US2] Integration test: `PasswordChangeIntegrationTests.cs` in `MrWhoOidc.UnitTests/Integration/`
  - `PasswordChange_PropagatesAcrossAllTenants`

### Implementation for User Story 2

- [ ] T018 [US2] Create/update `ChangePassword.cshtml.cs` in `MrWhoOidc.WebAuth/Pages/Profile/`
  - Inject `IUserAccountService` and `IGlobalAuthenticationService`
  - Call `UpdatePasswordAsync` on UserAccount (not per-tenant User)
  - Clear lockout state on successful password change
  - Set `PasswordUpdatedAt` timestamp
- [ ] T019 [US2] Update `ChangePassword.cshtml` UI to clarify password applies to all tenants
  - Add info banner: "This password change will apply to all your tenants"
- [ ] T020 [US2] Update `UserAccountService.UpdatePasswordAsync` to clear failed attempts
  - Reset `FailedLoginAttempts = 0`
  - Clear `LockedOutUntil`

**Checkpoint**: User Story 2 complete - password changes affect all tenants

---

## Phase 5: User Story 3 - Password Reset Works Globally (Priority: P1)

**Goal**: Password reset via email resets the UserAccount password, granting access to all tenants

**Independent Test**: User resets password, can login to all previously accessible tenants

### Tests for User Story 3

- [ ] T021 [P] [US3] Unit test: `PasswordResetServiceTests.cs` additions in `MrWhoOidc.UnitTests/Services/`
  - `ResetPassword_UpdatesUserAccountPassword`
  - `ResetPassword_ClearsLockoutState`
- [ ] T022 [P] [US3] Integration test: `PasswordResetIntegrationTests.cs` in `MrWhoOidc.UnitTests/Integration/`
  - `PasswordReset_RestoresAccessToAllTenants`

### Implementation for User Story 3

- [ ] T023 [US3] Update `ResetPassword.cshtml.cs` in `MrWhoOidc.WebAuth/Pages/Account/`
  - Look up UserAccount by email (not per-tenant User)
  - Generate reset token associated with UserAccount
  - Store token in UserAccount or dedicated reset token table
- [ ] T024 [US3] Update `ConfirmResetPassword.cshtml.cs` in `MrWhoOidc.WebAuth/Pages/Account/`
  - Validate reset token against UserAccount
  - Call `IUserAccountService.UpdatePasswordAsync` to set new password
  - Clear lockout state
- [ ] T025 [US3] Update email templates to clarify password reset affects all tenants
  - Add note: "This will reset your password for all tenants"

**Checkpoint**: User Story 3 complete - password reset works globally

---

## Phase 6: User Story 4 - MFA Settings Are Global (Priority: P2)

**Goal**: MFA enrollment/settings stored at UserAccount level, not per-tenant

**Independent Test**: User enables MFA in tenant A, challenged for MFA when logging into tenant B

### Tests for User Story 4

- [ ] T026 [P] [US4] Unit test: `MfaServiceTests.cs` additions in `MrWhoOidc.UnitTests/Services/`
  - `IsMfaEnabled_ReturnsTrue_WhenUserAccountHasMfa`
  - `ValidateMfaCode_ValidatesAgainstUserAccount`
- [ ] T027 [P] [US4] Integration test: `MfaIntegrationTests.cs` in `MrWhoOidc.UnitTests/Integration/`
  - `MfaEnabled_AppliesAcrossAllTenants`

### Implementation for User Story 4

- [ ] T028 [US4] Ensure MFA fields exist on UserAccount entity
  - `MfaEnabled` (bool)
  - `MfaSecret` (string, encrypted)
  - `MfaRecoveryCodes` (string[], encrypted)
  - Check data-model.md - add migration if needed
- [ ] T029 [US4] Update MFA enrollment pages in `MrWhoOidc.WebAuth/Pages/Profile/Mfa/`
  - Store MFA secret in UserAccount, not per-tenant User
  - Update `Enable.cshtml.cs`, `Verify.cshtml.cs`
- [ ] T030 [US4] Update login flow in `Login.cshtml.cs` to check UserAccount MFA
  - After successful password auth, check `UserAccount.MfaEnabled`
  - Redirect to MFA challenge if enabled
- [ ] T031 [US4] Update MFA validation in `MfaChallenge.cshtml.cs`
  - Validate TOTP code against `UserAccount.MfaSecret`

**Checkpoint**: User Story 4 complete - MFA is global

---

## Phase 7: User Story 5 - Admin Password Reset Affects Global Account (Priority: P2)

**Goal**: Admin resets password for user, it applies to global UserAccount with proper messaging

**Independent Test**: Admin resets password, user can login to all tenants with new password

### Tests for User Story 5

- [ ] T032 [P] [US5] Unit test: `AdminPasswordResetTests.cs` in `MrWhoOidc.UnitTests/Admin/`
  - `AdminReset_UpdatesUserAccountPassword`
  - `AdminReset_ClearsLockoutState`
  - `AdminReset_LogsAuditEvent`
- [ ] T033 [P] [US5] Integration test: `AdminPasswordResetIntegrationTests.cs` in `MrWhoOidc.UnitTests/Integration/`
  - `AdminReset_AffectsAllTenants`

### Implementation for User Story 5

- [ ] T034 [US5] Update admin user edit page in `MrWhoOidc.WebAuth/Pages/Admin/Users/Edit.cshtml.cs`
  - Add warning: "Resetting password will affect user's access to ALL tenants"
  - Call `IUserAccountService.UpdatePasswordAsync` instead of per-tenant reset
- [ ] T035 [US5] Update `Edit.cshtml` UI with confirmation dialog
  - Add modal: "This will reset the user's password across all tenants. Continue?"
- [ ] T036 [US5] Add audit logging for admin password reset
  - Log admin user, target user (hashed), timestamp, affected tenant count
  - Use structured logging per ServiceDefaults conventions

**Checkpoint**: User Story 5 complete - admin reset is global

---

## Phase 8: User Story 6 - Migration of Existing Users (Priority: P3)

**Goal**: Existing per-tenant passwords are consolidated into UserAccount credentials

**Independent Test**: After migration, user can login with most recent password to all tenants

### Tests for User Story 6

- [ ] T037 [P] [US6] Unit test: `PasswordMigrationServiceTests.cs` in `MrWhoOidc.UnitTests/Migration/`
  - `MigratePassword_UsesMostRecentPassword`
  - `MigratePassword_PreservesLockoutState`
  - `MigratePassword_HandlesConflicts`
- [ ] T038 [P] [US6] Integration test: `PasswordMigrationIntegrationTests.cs` in `MrWhoOidc.UnitTests/Integration/`
  - `Migration_PreservesUserAccess`

### Implementation for User Story 6

- [ ] T039 [US6] Create `IPasswordMigrationService.cs` interface in `MrWhoOidc.Auth/Services/`
  - `MigrateUserCredentialsAsync(Guid userAccountId)`
  - `GetMigrationStatusAsync(Guid userAccountId)`
- [ ] T040 [US6] Implement `PasswordMigrationService.cs` in `MrWhoOidc.Auth/Services/`
  - Find all User entities linked to UserAccount via UserTenantMembership
  - Select password with most recent `LastModified` timestamp
  - Copy to UserAccount.PasswordHash
  - Set `PasswordUpdatedAt`
  - Log migration with affected tenant count
- [ ] T041 [US6] Create migration admin endpoint in `MrWhoOidc.WebAuth/Handlers/AdminMigrationHandler.cs`
  - POST `/admin/migrate-credentials` - batch migration
  - GET `/admin/migrate-credentials/status` - migration status
- [ ] T042 [US6] Add migration script for bulk processing
  - Create SQL script for one-time batch migration
  - Document in `docs/global-credentials-migration.md`

**Checkpoint**: User Story 6 complete - existing users migrated

---

## Phase 9: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [ ] T043 [P] Update `docs/developer-guide.md` with global credentials architecture
- [ ] T044 [P] Update `docs/admin-guide.md` with password reset behavior changes
- [ ] T045 [P] Add health check for global auth service in `MrWhoOidc.WebAuth/Program.cs`
- [ ] T046 Code cleanup: remove unused per-tenant password validation code paths
- [ ] T047 Run `quickstart.md` validation steps to verify implementation
- [ ] T048 Security review: ensure no password hash leakage in logs or responses

---

## Dependencies & Execution Order

### Phase Dependencies

```
Phase 1 (Setup) ──┬──► Phase 2 (Foundational) ──┬──► Phase 3 (US1) ──► Phase 9 (Polish)
                  │                              │
                  │                              ├──► Phase 4 (US2) ──► Phase 9
                  │                              │
                  │                              ├──► Phase 5 (US3) ──► Phase 9
                  │                              │
                  │                              ├──► Phase 6 (US4) ──► Phase 9
                  │                              │
                  │                              ├──► Phase 7 (US5) ──► Phase 9
                  │                              │
                  │                              └──► Phase 8 (US6) ──► Phase 9
```

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 - **BLOCKS all user stories**
- **User Stories (Phases 3-8)**: All depend on Phase 2 completion
  - Can proceed in parallel (if multiple developers)
  - Or sequentially in priority order: US1 → US2 → US3 → US4 → US5 → US6
- **Polish (Phase 9)**: Depends on all desired user stories being complete

### User Story Dependencies

| Story | Priority | Can Start After | Dependencies on Other Stories |
|-------|----------|-----------------|-------------------------------|
| US1   | P1       | Phase 2         | None                          |
| US2   | P1       | Phase 2         | None (uses same services)     |
| US3   | P1       | Phase 2         | None (uses same services)     |
| US4   | P2       | Phase 2         | None (independent feature)    |
| US5   | P2       | Phase 2         | None (admin-facing)           |
| US6   | P3       | Phase 2         | US1-US3 recommended first     |

### Within Each User Story

1. Tests MUST be written and FAIL before implementation (TDD)
2. Implementation follows test requirements
3. Story complete when all tests pass

### Parallel Opportunities

**Within Phase 1:**
- T003 (index) and T004 (result record) can run in parallel

**Within Phase 2:**
- T010 (metrics) can run in parallel with T005-T009

**Across User Stories (after Phase 2):**
- US1, US2, US3 can all start in parallel (same priority, no dependencies)
- US4 and US5 can run in parallel with each other
- US6 should wait for US1-US3 (needs working global auth to validate migration)

**Within Each Story:**
- All test tasks marked [P] can run in parallel
- Implementation follows test completion

---

## Parallel Example: Maximum Parallelism After Phase 2

```
Developer 1: US1 (Login flow)
Developer 2: US2 (Password change)
Developer 3: US3 (Password reset)
```

All three P1 stories can be worked simultaneously since they use the same foundational services (`IGlobalAuthenticationService`, `IUserAccountService`) but modify different pages.

---

## Summary

| Phase | Story | Task Count | Estimated Effort |
|-------|-------|------------|------------------|
| 1     | Setup | 4          | Small            |
| 2     | Foundation | 6     | Medium           |
| 3     | US1   | 5          | Medium           |
| 4     | US2   | 5          | Small            |
| 5     | US3   | 5          | Small            |
| 6     | US4   | 6          | Medium           |
| 7     | US5   | 5          | Small            |
| 8     | US6   | 6          | Medium           |
| 9     | Polish | 6         | Small            |
| **Total** |   | **48**     |                  |
