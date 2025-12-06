# Research: Global User Credentials

**Feature**: 008-global-user-credentials  
**Date**: 2025-12-05  
**Status**: Complete

## Research Tasks

### 1. Existing Infrastructure Assessment

**Question**: What infrastructure already exists for global user accounts?

**Findings**:

- ✅ `UserAccount` entity already exists in `AuthDbContext` with credential fields:
  - `PasswordHash`, `PasswordSalt`, `HashAlgorithm`
  - `TotpSecret`, `TotpEnabled`
  - `SecurityStamp`, `LockedOutUntil`
- ✅ `UserTenantMembership` join table exists linking accounts to tenants
- ✅ `IUserAccountService` interface and implementation exist
- ✅ `IUserAccountProvisioner` handles dual-write from `User` to `UserAccount`
- ✅ Feature flag `UserAccountDecouplingEnabled` controls the rollout
- ✅ Seeder already dual-writes when flag is enabled

**Decision**: Leverage existing infrastructure. No new entities needed—extend existing services.

**Rationale**: The schema and basic services are already in place. The work is to switch authentication reads from `User` to `UserAccount`.

---

### 2. Authentication Flow Analysis

**Question**: How does authentication currently work and what needs to change?

**Current Flow** (per-tenant):

1. `Login.cshtml.cs` receives username/password
2. Calls `IUserService.FindByUsernameAsync()` (tenant-scoped via `ITenantAccessor`)
3. Calls `IUserService.VerifyPasswordAsync(user, password)` → checks `User.PasswordHash`
4. Signs in with claims from the tenant-scoped `User`

**Target Flow** (global):

1. `Login.cshtml.cs` receives username/password
2. Calls new `IGlobalAuthenticationService.AuthenticateAsync(username, password)`
3. Service finds `UserAccount` by username/email (no tenant scope)
4. Verifies password against `UserAccount.PasswordHash`
5. Checks lockout status on `UserAccount.LockedOutUntil`
6. Returns authenticated `UserAccount` with available `UserTenantMembership` list
7. Login handler resolves correct membership (from tenant context or tenant picker)
8. Signs in with claims (uses `UserAccount.Id` as `sub`)

**Decision**: Create `IGlobalAuthenticationService` in `MrWhoOidc.Auth` that authenticates against `UserAccount`.

**Rationale**: Clean separation—new service handles global auth, existing `IUserService` continues for tenant-scoped user queries.

---

### 3. Password Change/Reset Analysis

**Question**: How do password changes and resets need to change?

**Current Flow**:

- Profile page changes `User.PasswordHash` for current tenant
- Admin reset changes `User.PasswordHash` for target user in current tenant
- Password reset email flow updates `User.PasswordHash`

**Target Flow**:

- All password operations target `UserAccount.PasswordHash`
- Changes apply globally (single credential)
- Dual-write pattern during migration: update both `User` and `UserAccount`
- Post-migration: only update `UserAccount`

**Decision**: Modify password change handlers to update `UserAccount` (via `IUserAccountService`) and dual-write to `User` during transition.

**Rationale**: Maintains backward compatibility while transitioning.

---

### 4. Migration Strategy Analysis

**Question**: How do we migrate existing users with different passwords per tenant?

**Challenge**: User "bob@example.com" may have different passwords in Tenant A and Tenant B.

**Strategy Options Considered**:

| Option | Approach | Pros | Cons |
|--------|----------|------|------|
| A | Use most recent password | Simple, predictable | Users may forget which password |
| B | Require password reset | Clean slate | Disrupts all users |
| C | Accept any valid password on first login | User-friendly | Complex implementation |

**Decision**: Option A - Use the most recently updated password.

**Rationale**:

- Users typically remember their most recent password
- Audit log records which password was selected and from which tenant
- Users who can't remember can use password reset
- Simple implementation with predictable behavior

**Implementation**:

1. Migration identifies users with `UserAccount` entries (from dual-write)
2. For users without `UserAccount`, create one using credentials from `User` with most recent `CreatedAt` or a new `PasswordUpdatedAt` timestamp
3. Log conflicts for audit: "User {email} had different passwords in tenants {A, B}; selected from tenant {X}"

---

### 5. Lockout Globalization

**Question**: How should account lockout work globally?

**Current State**: Lockout is implemented in `Login.cshtml.cs` using a static dictionary keyed by IP+username. Not persisted.

**Target State**: Lockout persisted on `UserAccount.LockedOutUntil` field (already exists).

**Decision**: Implement global lockout using `UserAccount.LockedOutUntil`:

- Failed attempts increment a counter (new field or use existing pattern)
- After threshold, set `LockedOutUntil` to future timestamp
- All tenants check this field during authentication
- Lockout applies globally across all tenant logins

**Rationale**: Centralized lockout prevents attackers from distributing attempts across tenants.

---

### 6. MFA Globalization

**Question**: How do TOTP/WebAuthn credentials transition to global?

**Current State**:

- `User.TotpSecret` and `User.TotpEnabled` per tenant
- `WebAuthnCredential` entity has `TenantId` and `UserId`

**Target State**:

- `UserAccount.TotpSecret` and `UserAccount.TotpEnabled` (global)
- `WebAuthnCredential` should reference `UserAccountId` instead of per-tenant `UserId`

**Decision**:

- Phase 1: Migrate TOTP to `UserAccount` (already has fields)
- Phase 2 (separate feature): Migrate WebAuthn to reference `UserAccountId`

**Rationale**: TOTP migration is straightforward (fields exist). WebAuthn requires schema changes and is lower priority.

---

### 7. Token Claims Compatibility

**Question**: Do token claims need to change?

**Current State**: `sub` claim uses `User.Id`

**Target State**: `sub` claim should use `UserAccount.Id`

**Compatibility Note**: The `IUserAccountProvisioner` already sets `UserAccount.Id = User.Id` for dual-written accounts, maintaining claim compatibility.

**Decision**: No token format changes needed. `sub` continues to be the user ID, which is identical between `User` and `UserAccount` for migrated users.

**Rationale**: Backward compatibility with existing tokens and integrations.

---

## Summary of Decisions

| Area | Decision | Alternatives Rejected |
|------|----------|----------------------|
| Infrastructure | Extend existing `UserAccount`/`UserAccountService` | Creating new entities (unnecessary duplication) |
| Auth Service | New `IGlobalAuthenticationService` | Modifying `IUserService` (breaks tenant-scoped queries) |
| Password Migration | Use most recent password | Reset all (disruptive), Accept any (complex) |
| Lockout | Global via `UserAccount.LockedOutUntil` | Per-tenant lockout (defeats purpose) |
| MFA | Migrate TOTP to `UserAccount`; WebAuthn later | All-at-once (too risky) |
| Token Claims | Keep `sub` = UserAccount.Id = User.Id | New claim format (breaking change) |

## Open Items Resolved

All [NEEDS CLARIFICATION] items from Technical Context have been resolved through this research.
