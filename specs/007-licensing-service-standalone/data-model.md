# Data Model: Standalone Licensing Service

**Feature**: 007-licensing-service-standalone  
**Date**: 2025-12-04  
**Status**: Complete

## Entity Relationship Diagram

```text
┌─────────────────────┐       ┌─────────────────────────┐
│      Customer       │       │    LicensedProduct      │
├─────────────────────┤       ├─────────────────────────┤
│ Id (PK, UUIDv7)     │       │ Id (PK, UUIDv7)         │
│ Identifier          │       │ Identifier               │
│ DisplayName         │       │ DisplayName              │
│ ContactEmail        │       │ Description              │
│ ContactName         │       │ Status                   │
│ Status              │       │ CreatedAt                │
│ CreatedAt           │       │ UpdatedAt                │
│ UpdatedAt           │       └───────────┬─────────────┘
└─────────┬───────────┘                   │
          │                               │ 1:N
          │ 1:N                           ▼
          │               ┌───────────────────────────────┐
          │               │   ProductOptionDefinition     │
          │               ├───────────────────────────────┤
          │               │ Id (PK, UUIDv7)               │
          │               │ ProductId (FK)                │
          │               │ OptionKey                     │
          │               │ DisplayName                   │
          │               │ DataType                      │
          │               │ DefaultValue                  │
          │               │ Description                   │
          │               │ SortOrder                     │
          │               └───────────────────────────────┘
          │
          ▼
┌─────────────────────────────────────────────────────────┐
│                        License                          │
├─────────────────────────────────────────────────────────┤
│ Id (PK, UUIDv7)                                         │
│ TokenId (jti, unique)                                   │
│ CustomerId (FK → Customer)                              │
│ ProductId (FK → LicensedProduct)                        │
│ Tier                                                    │
│ Scope                                                   │
│ ValidFrom (nbf)                                         │
│ ValidUntil (exp)                                        │
│ Options (JSON)                                          │
│ Status                                                  │
│ ParentLicenseId (FK → License, nullable, self-ref)      │
│ CreatedAt                                               │
│ CreatedBy                                               │
│ RevokedAt                                               │
│ RevokedBy                                               │
│ RevocationReason                                        │
└─────────────────────────┬───────────────────────────────┘
                          │
                          │ 1:N
                          ▼
┌─────────────────────────────────────────────────────────┐
│                    LicenseEvent                         │
├─────────────────────────────────────────────────────────┤
│ Id (PK, UUIDv7)                                         │
│ LicenseId (FK → License)                                │
│ EventType                                               │
│ Timestamp                                               │
│ Actor                                                   │
│ Details (JSON)                                          │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│                     SigningKey                          │
├─────────────────────────────────────────────────────────┤
│ Id (PK, UUIDv7)                                         │
│ Kid (unique)                                            │
│ Algorithm                                               │
│ PublicKeyJwks                                           │
│ Status                                                  │
│ CreatedAt                                               │
│ RotatedAt                                               │
└─────────────────────────────────────────────────────────┘
```

## Entity Definitions

### Customer

Represents a licensed customer or organization.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| Id | Guid | PK, UUIDv7 | Unique identifier |
| Identifier | string | Required, MaxLength(100), Unique | Business identifier (e.g., "ACME-001") |
| DisplayName | string | Required, MaxLength(200) | Human-readable name |
| ContactEmail | string | MaxLength(254) | Primary contact email |
| ContactName | string | MaxLength(200) | Primary contact person |
| Status | string | Required, MaxLength(20), Default("Active") | Active, Inactive |
| CreatedAt | DateTimeOffset | Required | Record creation timestamp |
| UpdatedAt | DateTimeOffset | Nullable | Last modification timestamp |

**Indexes**:
- Unique on `Identifier`
- Non-unique on `Status`
- Non-unique on `DisplayName` (for search)

**Validation Rules**:
- Identifier must be alphanumeric with hyphens (pattern: `^[A-Za-z0-9-]+$`)
- Cannot delete customer with existing licenses (soft-delete only)

---

### LicensedProduct

Represents a product/service that can be licensed.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| Id | Guid | PK, UUIDv7 | Unique identifier |
| Identifier | string | Required, MaxLength(100), Unique | Product code (e.g., "mrwho-oidc") |
| DisplayName | string | Required, MaxLength(200) | Human-readable name |
| Description | string | MaxLength(1000) | Product description |
| Status | string | Required, MaxLength(20), Default("Active") | Active, Inactive |
| CreatedAt | DateTimeOffset | Required | Record creation timestamp |
| UpdatedAt | DateTimeOffset | Nullable | Last modification timestamp |

**Indexes**:
- Unique on `Identifier`
- Non-unique on `Status`

**Validation Rules**:
- Identifier must be lowercase alphanumeric with hyphens
- Cannot delete product with existing licenses (soft-delete only)

**Navigation Properties**:
- `OptionDefinitions`: Collection of `ProductOptionDefinition`

---

### ProductOptionDefinition

Defines an available licensable option for a product.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| Id | Guid | PK, UUIDv7 | Unique identifier |
| ProductId | Guid | FK, Required | Reference to LicensedProduct |
| OptionKey | string | Required, MaxLength(50) | Option identifier (e.g., "max_users") |
| DisplayName | string | Required, MaxLength(100) | Human-readable name |
| DataType | string | Required, MaxLength(20) | string, number, boolean |
| DefaultValue | string | MaxLength(200) | Default value (as string) |
| Description | string | MaxLength(500) | Help text for administrators |
| SortOrder | int | Default(0) | Display order in UI |

**Indexes**:
- Unique composite on `(ProductId, OptionKey)`
- Non-unique on `ProductId`

**Validation Rules**:
- OptionKey must be lowercase alphanumeric with underscores
- DataType must be one of: string, number, boolean
- DefaultValue must be valid for the specified DataType

---

### License

Represents an issued license token.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| Id | Guid | PK, UUIDv7 | Unique identifier |
| TokenId | string | Required, MaxLength(100), Unique | JWT jti claim |
| CustomerId | Guid | FK, Required | Reference to Customer |
| ProductId | Guid | FK, Required | Reference to LicensedProduct |
| Tier | string | Required, MaxLength(50) | License tier (Community, Professional, Enterprise) |
| Scope | string | Required, MaxLength(50) | License scope (platform, tenant) |
| ValidFrom | DateTimeOffset | Required | Not-before date (nbf) |
| ValidUntil | DateTimeOffset | Required | Expiration date (exp) |
| Options | string | JSON | Product options as key-value JSON |
| Status | string | Required, MaxLength(20) | Active, Expired, Revoked, Renewed, Upgraded, Downgraded |
| ParentLicenseId | Guid | FK, Nullable | Reference to parent license (for renewals/upgrades) |
| CreatedAt | DateTimeOffset | Required | License creation timestamp |
| CreatedBy | string | MaxLength(200) | Creator user identifier |
| RevokedAt | DateTimeOffset | Nullable | Revocation timestamp |
| RevokedBy | string | MaxLength(200) | Revoker user identifier |
| RevocationReason | string | MaxLength(500) | Reason for revocation |

**Indexes**:
- Unique on `TokenId`
- Non-unique on `CustomerId`
- Non-unique on `ProductId`
- Non-unique on `Status`
- Composite on `(CustomerId, ProductId)` for renewal lookup
- Composite on `(CustomerId, Status, ValidUntil)` for expiry queries

**Validation Rules**:
- ValidUntil must be after ValidFrom
- Options JSON must contain only keys defined in ProductOptionDefinition for the product
- Option values must match declared DataType
- ParentLicenseId must reference license with same CustomerId and ProductId

**State Transitions**:
```text
Created → Active (automatic on creation if ValidFrom <= now)
Active → Expired (automatic when ValidUntil < now)
Active → Revoked (manual action with reason)
Active → Renewed (when child renewal license created)
Active → Upgraded (when child upgrade license created)
Active → Downgraded (when child downgrade license created)
```

**Navigation Properties**:
- `Customer`: Reference to Customer
- `Product`: Reference to LicensedProduct
- `ParentLicense`: Self-reference to parent License
- `ChildLicenses`: Collection of child Licenses
- `Events`: Collection of LicenseEvent

---

### LicenseEvent

Audit trail entry for license lifecycle events.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| Id | Guid | PK, UUIDv7 | Unique identifier |
| LicenseId | Guid | FK, Required | Reference to License |
| EventType | string | Required, MaxLength(50) | Created, Renewed, Revoked, Upgraded, Downgraded, Validated |
| Timestamp | DateTimeOffset | Required | Event occurrence time |
| Actor | string | Required, MaxLength(200) | User who performed action |
| Details | string | JSON | Event-specific details |

**Indexes**:
- Non-unique on `LicenseId`
- Non-unique on `Timestamp`
- Composite on `(LicenseId, Timestamp)` for ordered history

**Validation Rules**:
- Events are append-only (never updated or deleted)
- Actor must be authenticated user identifier

---

### SigningKey

Signing key for license tokens.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| Id | Guid | PK, UUIDv7 | Unique identifier |
| Kid | string | Required, MaxLength(100), Unique | Key identifier in JWT header |
| Algorithm | string | Required, MaxLength(20) | Signing algorithm (ES256) |
| PublicKeyJwks | string | Required | Public key in JWK format |
| Status | string | Required, MaxLength(20) | Active, Rotated, Retired |
| CreatedAt | DateTimeOffset | Required | Key creation timestamp |
| RotatedAt | DateTimeOffset | Nullable | When key was rotated out |

**Indexes**:
- Unique on `Kid`
- Non-unique on `Status`

**Notes**:
- Private key stored externally (file or secret manager)
- Only public key persisted in database for JWKS endpoint
- Multiple keys can be Active during rotation overlap

---

## JSON Schema: License Options

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "additionalProperties": {
    "oneOf": [
      { "type": "string" },
      { "type": "number" },
      { "type": "boolean" }
    ]
  },
  "examples": [
    {
      "max_users": 100,
      "region": "EU",
      "analytics_enabled": true,
      "support_tier": "premium"
    }
  ]
}
```

## JSON Schema: LicenseEvent Details

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "properties": {
    "previousStatus": { "type": "string" },
    "newStatus": { "type": "string" },
    "reason": { "type": "string" },
    "parentLicenseId": { "type": "string", "format": "uuid" },
    "childLicenseId": { "type": "string", "format": "uuid" },
    "changes": {
      "type": "object",
      "additionalProperties": {
        "type": "object",
        "properties": {
          "before": {},
          "after": {}
        }
      }
    }
  }
}
```

## Migration Notes

- All primary keys use `GuidHelper.NewId()` (UUIDv7) per constitution
- Generate migrations using EF Core tools, not hand-written
- PostgreSQL uses `jsonb` for JSON columns; SQLite uses `TEXT`
- Initial migration creates all tables with proper indexes
- Seed data: one SigningKey (active), one sample Product (optional)
