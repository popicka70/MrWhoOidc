# Data Model: Platform QR Login at DiscoverTenant

**Feature**: 014-platform-qr-login  
**Date**: 2025-12-26

## New Entities

### PlatformSettings

System-wide configuration that applies across all tenants. Single-row table pattern.

```text
PlatformSettings
├── Id: Guid (PK, UUIDv7 via GuidHelper.NewId())
├── QrLoginAtDiscoveryEnabled: bool (default: false)
├── CreatedAt: DateTimeOffset
├── UpdatedAt: DateTimeOffset
└── UpdatedBy: string? (user who last modified)
```

**Constraints**:

- Only one row should exist (enforce via application logic or unique constraint on sentinel column)
- QrLoginAtDiscoveryEnabled defaults to false (opt-in feature)

**Relationships**:

- None (standalone configuration entity)

**Validation Rules**:

- No complex validation; boolean toggle only
- Future: may add JSON column for extensibility (like Tenant.SettingsJson)

## Modified Entities

### QrLoginSession (existing)

No schema changes required. The existing entity supports sessions without client binding:

```text
QrLoginSession (existing)
├── Id: Guid (PK)
├── SessionToken: string (unique)
├── TenantId: Guid? (nullable - resolved during mobile auth)
├── ClientId: Guid? (nullable - for DiscoverTenant flow)
├── UserId: Guid? (nullable until authenticated)
├── Status: QrLoginStatus enum
├── ReturnUrl: string? (preserved from DiscoverTenant)
├── ... other existing fields
```

**Note**: ClientId is already nullable, which supports the DiscoverTenant use case.

## Entity Relationships Diagram

```text
┌─────────────────────┐
│  PlatformSettings   │  (NEW - single row)
├─────────────────────┤
│ QrLoginAtDiscovery  │──┐
│ Enabled             │  │
└─────────────────────┘  │
                         │ controls visibility
                         ▼
┌─────────────────────┐
│   DiscoverTenant    │  (Page - reads setting)
│      Page           │
└─────────────────────┘
           │
           │ initiates (if enabled)
           ▼
┌─────────────────────┐
│   QrLoginSession    │  (existing entity)
├─────────────────────┤
│ ClientId = null     │  (for platform QR)
│ TenantId = null     │  (resolved on mobile)
│ ReturnUrl = ?       │  (from DiscoverTenant)
└─────────────────────┘
```

## State Transitions

### PlatformSettings Lifecycle

```text
[Not Exists] ──(first access)──> [Created with defaults]
     │                                    │
     │                                    ▼
     │                           [QrLoginAtDiscoveryEnabled = false]
     │                                    │
     │                            (admin toggles)
     │                                    │
     │                                    ▼
     │                           [QrLoginAtDiscoveryEnabled = true]
     │                                    │
     │                            (admin toggles)
     │                                    │
     └────────────────────────────────────┘
```

### QrLoginSession State (existing, unchanged)

```text
[Pending] ──(mobile scans)──> [Scanned] ──(user approves)──> [Authenticated]
    │                             │                                │
    │                             │                                │
    ▼                             ▼                                ▼
[Expired]                    [Cancelled]                      [Completed]
```

## Database Migration

### Migration: AddPlatformSettings

```sql
-- Up
CREATE TABLE "PlatformSettings" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
    "QrLoginAtDiscoveryEnabled" boolean NOT NULL DEFAULT false,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedBy" text NULL,
    CONSTRAINT "PK_PlatformSettings" PRIMARY KEY ("Id")
);

-- Seed initial row (application can also handle this on first access)
INSERT INTO "PlatformSettings" ("Id", "QrLoginAtDiscoveryEnabled", "CreatedAt", "UpdatedAt")
VALUES (gen_random_uuid(), false, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);

-- Down
DROP TABLE "PlatformSettings";
```

**Note**: Actual migration will be generated via `dotnet ef migrations add AddPlatformSettings`.

## Caching Strategy

### Platform Settings Cache

```text
Cache Key: "platform:settings"
TTL (L1 memory): 15 minutes
TTL (L2 Redis): 1 hour
Invalidation: On save via IPlatformSettingsService.UpdateAsync()
Tags: ["platform-settings"]
```

**Pattern**: Same as TenantSettingsService (HybridCache with GetOrCreateAsync).

## Indexes

No additional indexes required for PlatformSettings (single-row table, primary key sufficient).
