# Data Model: Global User Credentials

**Feature**: 008-global-user-credentials  
**Date**: 2025-12-05  
**Status**: Complete

## Entity Overview

This feature primarily uses **existing entities** with minor modifications. No new tables are required.

## Entities

### UserAccount (EXISTS - Primary Credential Store)

Global identity record containing credentials, security profile, and account metadata.

| Field | Type | Description | Status |
|-------|------|-------------|--------|
| `Id` | `Guid` | Primary key (UUIDv7 via `GuidHelper.NewId()`) | EXISTS |
| `Username` | `string(200)` | Unique username | EXISTS |
| `PasswordHash` | `string` | Argon2id password hash | EXISTS |
| `PasswordSalt` | `string(128)?` | Optional salt (algorithm-dependent) | EXISTS |
| `HashAlgorithm` | `string(50)` | Hash algorithm identifier | EXISTS |
| `Email` | `string(256)?` | Primary email | EXISTS |
| `NormalizedEmail` | `string(256)?` | Lowercase normalized email for lookups | EXISTS |
| `EmailVerified` | `bool` | Email verification status | EXISTS |
| `EmailVerifiedAt` | `DateTimeOffset?` | When email was verified | EXISTS |
| `Name` | `string(200)?` | Display name | EXISTS |
| `CreatedAt` | `DateTimeOffset` | Account creation timestamp | EXISTS |
| `SecurityStamp` | `string(200)?` | Security stamp for invalidating tokens | EXISTS |
| `SettingsJson` | `string(4000)?` | JSON blob for account preferences | EXISTS |
| `TotpSecret` | `string(200)?` | TOTP secret for MFA | EXISTS |
| `TotpEnabled` | `bool` | Whether TOTP MFA is enabled | EXISTS |
| `LockedOutUntil` | `DateTimeOffset?` | Lockout expiration timestamp | EXISTS |
| `FailedLoginAttempts` | `int` | Counter for failed login attempts | **NEW** |
| `LastFailedLoginAt` | `DateTimeOffset?` | Timestamp of last failed login | **NEW** |
| `PasswordUpdatedAt` | `DateTimeOffset?` | Timestamp of last password change | **NEW** |

**Relationships**:

- One-to-Many: `UserAccount` → `UserTenantMembership` (tenant access)
- One-to-Many: `UserAccount` → `WebAuthnCredential` (future migration)

**Validation Rules**:

- `Username` must be unique across all accounts
- `NormalizedEmail` must be unique when not null
- `PasswordHash` required, validated against password policy before hashing
- `FailedLoginAttempts` resets to 0 on successful login
- `LockedOutUntil` checked before allowing authentication

---

### UserTenantMembership (EXISTS - No Changes)

Association between a UserAccount and a Tenant. Contains tenant-specific settings but **no credentials**.

| Field | Type | Description | Status |
|-------|------|-------------|--------|
| `Id` | `Guid` | Primary key | EXISTS |
| `UserAccountId` | `Guid` | FK to UserAccount | EXISTS |
| `TenantId` | `Guid` | FK to Tenant | EXISTS |
| `DefaultRealmId` | `Guid?` | Default realm for this membership | EXISTS |
| `DisplayName` | `string(200)?` | Tenant-specific display name | EXISTS |
| `Status` | `TenantMembershipStatus` | Active/Suspended/Pending/Revoked | EXISTS |
| `IsTenantAdmin` | `bool` | Whether user is admin in this tenant | EXISTS |
| `CreatedAt` | `DateTimeOffset` | Membership creation | EXISTS |
| `ExpiresAt` | `DateTimeOffset?` | Optional membership expiry | EXISTS |
| `SuspendedAt` | `DateTimeOffset?` | When suspended | EXISTS |
| `SettingsJson` | `string(2000)?` | Tenant-specific preferences | EXISTS |

**No changes required for this feature.**

---

### User (EXISTS - Legacy, Credential Fields Deprecated)

Per-tenant user record. Credentials will be deprecated in favor of `UserAccount`.

| Field | Type | Description | Status |
|-------|------|-------------|--------|
| `Id` | `Guid` | Primary key | EXISTS |
| `TenantId` | `Guid` | FK to Tenant | EXISTS |
| `Username` | `string(200)` | Username within tenant | EXISTS |
| `PasswordHash` | `string` | **DEPRECATED** - migrate to UserAccount | DEPRECATE |
| `PasswordSalt` | `string?` | **DEPRECATED** | DEPRECATE |
| `HashAlgorithm` | `string` | **DEPRECATED** | DEPRECATE |
| `Email` | `string(256)?` | Email | EXISTS |
| `NormalizedEmail` | `string(256)?` | Normalized email | EXISTS |
| `EmailVerified` | `bool` | Verification status | EXISTS |
| `Name` | `string(200)?` | Display name | EXISTS |
| `TotpSecret` | `string(200)?` | **DEPRECATED** - migrate to UserAccount | DEPRECATE |
| `TotpEnabled` | `bool` | **DEPRECATED** | DEPRECATE |
| ... | ... | Other fields remain | EXISTS |

**Migration Notes**:

- Credential fields (`PasswordHash`, `PasswordSalt`, `HashAlgorithm`, `TotpSecret`, `TotpEnabled`) are deprecated
- During dual-write phase: writes go to both `User` and `UserAccount`
- During read-switch phase: reads come from `UserAccount`
- Final cleanup phase: remove deprecated columns

---

## State Transitions

### Account Lockout State Machine

```text
┌─────────────┐
│   Active    │ ←─────────────────────────────┐
│ (Normal)    │                               │
└──────┬──────┘                               │
       │ Failed login                         │
       │ (FailedLoginAttempts++)              │
       ▼                                      │
┌─────────────┐                               │
│  Counting   │ FailedLoginAttempts < Max     │
│  Failures   │ ─────────────────────────────►│
└──────┬──────┘ Successful login              │
       │ FailedLoginAttempts >= Max           │
       ▼                                      │
┌─────────────┐                               │
│  Locked     │ LockedOutUntil expired ───────┘
│  Out        │ (auto-unlock)
└─────────────┘
```

**State Rules**:

- `Active`: `LockedOutUntil` is null or in the past, `FailedLoginAttempts` < threshold
- `Counting Failures`: `FailedLoginAttempts` > 0 but < threshold
- `Locked Out`: `LockedOutUntil` is in the future

**Transition Triggers**:

- Failed login: Increment `FailedLoginAttempts`, set `LastFailedLoginAt`
- Successful login: Reset `FailedLoginAttempts` to 0
- Threshold exceeded: Set `LockedOutUntil` to now + lockout duration
- Lockout expired: Natural transition back to Active

---

## Migration Plan

### Schema Migration

```text
Migration: AddGlobalCredentialFields

1. Add to UserAccount:
   - FailedLoginAttempts (int, default 0)
   - LastFailedLoginAt (DateTimeOffset?, nullable)
   - PasswordUpdatedAt (DateTimeOffset?, nullable)

2. Create index:
   - IX_UserAccounts_NormalizedEmail (unique, filtered where not null)
```

### Data Migration

```text
Migration: BackfillUserAccounts

For each User without corresponding UserAccount:
1. Find or create UserAccount with matching email/username
2. Copy credential fields:
   - PasswordHash → UserAccount.PasswordHash
   - PasswordSalt → UserAccount.PasswordSalt
   - HashAlgorithm → UserAccount.HashAlgorithm
   - TotpSecret → UserAccount.TotpSecret
   - TotpEnabled → UserAccount.TotpEnabled
3. Create UserTenantMembership linking account to tenant
4. Log any conflicts (same email, different passwords across tenants)
```

---

## Indexes

### New Indexes

| Table | Index Name | Columns | Type | Notes |
|-------|------------|---------|------|-------|
| `UserAccounts` | `IX_UserAccounts_NormalizedEmail` | `NormalizedEmail` | Unique, Filtered | WHERE NormalizedEmail IS NOT NULL |
| `UserAccounts` | `IX_UserAccounts_Username` | `Username` | Unique | Global username uniqueness |

### Existing Indexes (Verified)

| Table | Index Name | Columns | Status |
|-------|------------|---------|--------|
| `UserTenantMemberships` | `IX_UserTenantMemberships_UserAccountId_TenantId` | `UserAccountId`, `TenantId` | EXISTS (unique) |

---

## Entity Relationship Diagram

```text
┌─────────────────────────────────────────────────────────────┐
│                        UserAccount                          │
│  ─────────────────────────────────────────────────────────  │
│  Id (PK)                                                    │
│  Username (unique)                                          │
│  PasswordHash, PasswordSalt, HashAlgorithm                  │
│  Email, NormalizedEmail (unique)                            │
│  TotpSecret, TotpEnabled                                    │
│  LockedOutUntil, FailedLoginAttempts                        │
│  SecurityStamp, PasswordUpdatedAt                           │
└───────────────────────────┬─────────────────────────────────┘
                            │ 1
                            │
                            │ *
┌───────────────────────────┴─────────────────────────────────┐
│                   UserTenantMembership                       │
│  ─────────────────────────────────────────────────────────  │
│  Id (PK)                                                    │
│  UserAccountId (FK) ─────────────────────► UserAccount      │
│  TenantId (FK) ──────────────────────────► Tenant           │
│  Status, IsTenantAdmin                                      │
│  DisplayName, DefaultRealmId                                │
└─────────────────────────────────────────────────────────────┘
                            │
                            │ *
                            │
┌───────────────────────────┴─────────────────────────────────┐
│                      User (Legacy)                           │
│  ─────────────────────────────────────────────────────────  │
│  Id (PK) ───────────────────────────────── = UserAccount.Id │
│  TenantId (FK)                                              │
│  Username, Email                                            │
│  PasswordHash (DEPRECATED)                                   │
│  TotpSecret (DEPRECATED)                                     │
└─────────────────────────────────────────────────────────────┘
```

**Key Relationships**:

- `UserAccount` is the source of truth for credentials
- `UserTenantMembership` links accounts to tenants (no credentials)
- `User` remains for backward compatibility; credential fields deprecated
- `User.Id` matches `UserAccount.Id` for migrated accounts (claim compatibility)
