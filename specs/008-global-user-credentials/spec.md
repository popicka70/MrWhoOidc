# Feature Specification: Global User Credentials

**Feature Branch**: `008-global-user-credentials`  
**Created**: 2025-12-05  
**Status**: Draft  
**Input**: User description: "I want to implement single global credential per user. As of now we have password per tenant. That is counterintuitive. We need to implement single credentials per user."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Single Password Across All Tenants (Priority: P1)

As a user with access to multiple tenants, I want to use the same password to log in to any of my tenants, so that I don't have to remember different passwords for each organization I belong to.

**Why this priority**: This is the core value proposition. Users currently must maintain separate passwords per tenant, which is confusing, error-prone, and creates support burden. Solving this delivers immediate user value and reduces password reset requests.

**Independent Test**: Can be fully tested by creating a user with access to two tenants, setting one password, and verifying login works for both tenants with that single password.

**Acceptance Scenarios**:

1. **Given** a user has memberships in Tenant A and Tenant B, **When** the user sets their password in Tenant A, **Then** the same password works for logging into Tenant B.
2. **Given** a user is logged into Tenant A and changes their password, **When** the user logs out and attempts to log into Tenant B, **Then** the new password works and the old password is rejected.
3. **Given** a user exists only in one tenant, **When** the user changes their password, **Then** login continues to work normally with the new password.

---

### User Story 2 - Password Change Propagates Globally (Priority: P1)

As a user, when I change my password from any tenant's profile page, I want that change to apply to all my tenant memberships immediately, so I have a consistent experience regardless of which tenant I'm currently using.

**Why this priority**: Password changes must propagate globally for the single-credential model to work. Without this, users would have inconsistent experiences and security gaps.

**Independent Test**: Can be tested by changing password in one tenant context and immediately verifying the new password works in another tenant context.

**Acceptance Scenarios**:

1. **Given** a user is logged into Tenant A, **When** the user changes their password via the profile page, **Then** the change applies immediately to all tenant memberships.
2. **Given** a user changed their password in Tenant A, **When** an active session in Tenant B attempts a sensitive operation requiring re-authentication, **Then** the new password is required.
3. **Given** a user has an active session in Tenant B, **When** the user changes password in Tenant A, **Then** the Tenant B session remains valid until it expires or the user logs out.

---

### User Story 3 - Password Reset Works Globally (Priority: P1)

As a user who forgot my password, when I reset it through the forgot password flow, I want the new password to work for all my tenants, so I can regain access to all my organizations with one reset action.

**Why this priority**: Password reset is a critical recovery flow. Users must be able to recover access to all their tenants with a single reset action.

**Independent Test**: Can be tested by triggering password reset, setting new password, and verifying access to multiple tenants.

**Acceptance Scenarios**:

1. **Given** a user has memberships in multiple tenants, **When** the user completes the password reset flow, **Then** the new password grants access to all tenants.
2. **Given** a user triggers password reset from Tenant A's login page, **When** the reset is completed, **Then** access to Tenant B is also restored with the new password.
3. **Given** a user has active sessions in other tenants, **When** the password is reset, **Then** existing sessions remain valid but new logins require the new password.

---

### User Story 4 - MFA Settings Are Global (Priority: P2)

As a security-conscious user, when I enable multi-factor authentication, I want it to protect access to all my tenants, so I have consistent security across all my organizations.

**Why this priority**: MFA is part of the credential/security profile. Having different MFA settings per tenant is confusing and creates security inconsistencies. This follows naturally from global credentials.

**Independent Test**: Can be tested by enabling TOTP in one tenant context and verifying MFA is required when logging into another tenant.

**Acceptance Scenarios**:

1. **Given** a user enables TOTP authentication in Tenant A, **When** the user logs into Tenant B, **Then** TOTP verification is required.
2. **Given** a user has TOTP enabled globally, **When** the user disables TOTP from any tenant's profile page, **Then** TOTP is no longer required for any tenant.
3. **Given** a user has WebAuthn credentials registered, **When** logging into any tenant, **Then** the same WebAuthn credentials can be used.

---

### User Story 5 - Admin Password Reset Affects Global Account (Priority: P2)

As a tenant administrator, when I reset a user's password, I want to understand that this affects the user's access to all tenants, so I can make informed decisions and communicate appropriately with the user.

**Why this priority**: Admins need to understand the impact of their actions. A password reset in one tenant now affects the user's global account.

**Independent Test**: Can be tested by having an admin reset a user's password and verifying the user must use the new password for all tenants.

**Acceptance Scenarios**:

1. **Given** a tenant admin resets a user's password in Tenant A, **When** the user attempts to log into Tenant B, **Then** the new password is required.
2. **Given** a tenant admin initiates a password reset, **When** viewing the confirmation dialog, **Then** a clear message indicates this affects the user's global account.
3. **Given** a user's password was reset by Admin in Tenant A, **When** the user receives the notification, **Then** the notification indicates all tenant access is affected.

---

### User Story 6 - Migration of Existing Users (Priority: P3)

As a platform operator, I want existing users with per-tenant passwords to be smoothly migrated to global credentials, so that the transition is seamless and doesn't disrupt users.

**Why this priority**: Critical for adoption but happens once. Existing users must not lose access during migration.

**Independent Test**: Can be tested by migrating a user with different passwords in two tenants and verifying they can log in with their most recently used password.

**Acceptance Scenarios**:

1. **Given** a user has different passwords in Tenant A and Tenant B, **When** the migration runs, **Then** the user can log in with the most recently set password.
2. **Given** migration has completed, **When** a user logs in for the first time post-migration, **Then** login works without requiring a password reset.
3. **Given** a user had MFA enabled in only one tenant, **When** migrated, **Then** MFA status reflects the most secure configuration (enabled if enabled anywhere).

---

### Edge Cases

- What happens when a user has conflicting passwords across tenants during migration? **Answer**: Use the most recently updated password; log the conflict for audit purposes.
- What happens if a tenant admin tries to set a password that doesn't meet another tenant's policy? **Answer**: The global password policy applies (strictest policy or platform-defined policy).
- How does account lockout work across tenants? **Answer**: Lockout is global—failed attempts in any tenant contribute to the lockout counter.
- What happens to existing per-tenant password history during migration? **Answer**: Password history is consolidated; the global account inherits combined history to prevent recent password reuse.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST store user credentials (password hash, MFA settings) in a single global account record, not per-tenant.
- **FR-002**: System MUST authenticate users against their global credentials regardless of which tenant context the login occurs in.
- **FR-003**: System MUST propagate password changes immediately to all tenant access (no per-tenant password sync needed since there's only one).
- **FR-004**: System MUST apply password reset operations to the global account, restoring access to all tenant memberships.
- **FR-005**: System MUST store MFA configuration (TOTP secrets, WebAuthn credentials) at the global account level.
- **FR-006**: System MUST enforce account lockout globally—failed login attempts accumulate across all tenants.
- **FR-007**: System MUST provide a migration path for existing per-tenant user records to global accounts.
- **FR-008**: System MUST maintain backward compatibility during migration—existing sessions remain valid.
- **FR-009**: System MUST display clear messaging to administrators when password reset affects global account.
- **FR-010**: System MUST apply a consistent password policy for global accounts (platform-defined or strictest tenant policy).
- **FR-011**: System MUST maintain audit trail of credential changes indicating the tenant context where the change was initiated.
- **FR-012**: System MUST allow users to manage their global credentials from any tenant's profile page they have access to.

### Key Entities

- **UserAccount**: Global identity record containing credentials (password hash, MFA configuration), security profile (lockout status, security stamp), and account metadata. Independent of any specific tenant.
- **UserTenantMembership**: Association between a UserAccount and a Tenant, containing tenant-specific settings (display name, roles, preferences) but no credentials.
- **User (Legacy)**: Existing per-tenant user records. Will be deprecated after migration; during transition, kept in sync via dual-write pattern.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can log into any of their tenant memberships with a single password, verified by 100% of multi-tenant users using one credential.
- **SC-002**: Password changes take effect across all tenants within 1 second (no propagation delay since credential is centralized).
- **SC-003**: Password reset requests decrease by 50% for multi-tenant users (no more "wrong password for this tenant" confusion).
- **SC-004**: Zero users lose access during migration—all existing users can log in post-migration without forced password reset.
- **SC-005**: Admin password reset operations complete with clear confirmation messaging indicating global impact, verified by admin UX testing.
- **SC-006**: Account lockout triggered in Tenant A prevents login to Tenant B, verified by security testing.
- **SC-007**: 95% of users complete migration without requiring support intervention.

## Assumptions

- The existing `UserAccount` and `UserTenantMembership` entities (already in schema) will be leveraged for this implementation.
- The dual-write pattern currently in place for new accounts will be extended to handle migration of existing accounts.
- Feature flags (`UserAccountDecouplingEnabled`) will control the rollout.
- Platform password policy will be defined (or derived as the strictest of all tenant policies) before migration.
- Existing sessions will not be invalidated during migration to avoid disruption.

## Out of Scope

- Per-tenant password policies (single global policy applies).
- Federated/external IdP credential management (those are handled separately).
- Self-service account deletion (separate feature).
- Account merging (when same email exists in multiple tenants with different usernames).

