# Data Model: Remove Client Selection from User Registration

**Feature**: 006-remove-registration-client-select  
**Date**: 2024-12-02  
**Status**: Complete

## Overview

This feature **removes** UI elements and code, so there are no new entities or schema changes. This document describes the existing entities that are affected and their unchanged behavior.

## Affected Entities

### Registration (Unchanged Schema)

**Location**: `MrWhoOidc.Auth/Persistence/Registration.cs`

The `Registration` entity retains its existing schema for backward compatibility.

| Field | Type | Change | Notes |
|-------|------|--------|-------|
| `Id` | `Guid` | None | Primary key |
| `Email` | `string` | None | Required |
| `NormalizedEmail` | `string` | None | For lookup |
| `FirstName` | `string?` | None | Optional |
| `LastName` | `string?` | None | Optional |
| `ClientId` | `Guid?` | **Behavior change** | Will always be `null` from UI; retained for programmatic use |
| `PasswordHash` | `string?` | None | Optional local password |
| `State` | `string` | None | "pending", "approved", "rejected" |
| `CreatedAt` | `DateTimeOffset` | None | Timestamp |
| `IsTenantAdmin` | `bool` | None | Self-service tenant creation |
| `TenantSlug` | `string?` | None | For new tenant creation |
| `TenantName` | `string?` | None | For new tenant creation |
| `TenantDescription` | `string?` | None | For new tenant creation |
| `TenantId` | `Guid` | None | Target tenant |

**Key Point**: The `ClientId` field remains nullable and functional. The only change is that the UI no longer populates it. Programmatic registration (e.g., external IdP provisioning) can still set it.

---

### TenantContext (Unchanged)

**Location**: `MrWhoOidc.Auth/MultiTenancy/TenantContext.cs`

No changes. The registration page continues to use `ITenantAccessor.CurrentTenant` to determine which tenant the registration belongs to.

| Field | Type | Notes |
|-------|------|-------|
| `TenantId` | `Guid` | From database |
| `Slug` | `string` | URL-safe identifier |
| `Name` | `string` | Display name |
| `IssuerUri` | `string` | Computed issuer |
| `IsMultiTenantMode` | `bool` | From configuration |

---

### User (Unchanged)

**Location**: `MrWhoOidc.Auth/Persistence/User.cs`

No changes. Users created from registration continue to work normally. Client assignment is handled separately via `UserClientAssignment`.

---

### UserClientAssignment (Unchanged)

**Location**: `MrWhoOidc.Auth/Persistence/UserClientAssignment.cs`

No changes. When `registration.ClientId` is null, no assignment is created during approval. Admins can create assignments later via the admin UI.

## State Transitions

### Registration Flow (Simplified)

```
[Unauthenticated User]
        │
        ▼
┌─────────────────────┐
│   Registration Page │  ← No client dropdown
│   (Index.cshtml)    │
└─────────────────────┘
        │
        │ Submit (email, name?, password?, tenant creation?)
        ▼
┌─────────────────────┐
│  RegistrationService│
│  CreateAndMaybe...  │  ← ClientId = null (always)
└─────────────────────┘
        │
        ▼
┌─────────────────────┐
│   Registration      │
│   State: "pending"  │  ← TenantId from ITenantAccessor
│   ClientId: null    │
└─────────────────────┘
        │
        │ Admin approves
        ▼
┌─────────────────────┐
│   User Created      │
│   No client assign  │  ← Because ClientId was null
└─────────────────────┘
        │
        │ Admin assigns client (optional, via admin UI)
        ▼
┌─────────────────────┐
│ UserClientAssignment│
│ Created by admin    │
└─────────────────────┘
```

## Validation Rules (Unchanged)

| Rule | Entity | Description |
|------|--------|-------------|
| Required email | Registration | Email address must be provided and valid |
| Unique email | Registration | No duplicate pending registrations |
| Tenant required | Registration | Must resolve to a tenant (from URL or default) |
| Tenant slug format | Registration | If creating tenant, slug must be URL-safe |

## Database Migrations

**None required.** This feature only removes UI elements and changes the value passed to an existing nullable field.

## Backward Compatibility

| Scenario | Behavior |
|----------|----------|
| Existing registrations with ClientId | Processed normally during approval |
| New registrations from UI | ClientId will be null |
| External IdP provisioning | Can still set ClientId programmatically |
| Admin user-client assignment | Works as before via admin UI |
