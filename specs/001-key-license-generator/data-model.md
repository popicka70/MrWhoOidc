# Data Model: Key and License Management Service

**Date**: October 28, 2025  
**Feature**: Key and License Management Service  
**Branch**: 001-key-license-generator

## Overview

This document defines the data entities for key pair metadata, license token information, and audit trail records. The model supports key lifecycle management (generation, download tracking, revocation) and license token generation.

## Entities

### KeyPairMetadata

Represents a generated cryptographic key pair with metadata for tracking and audit purposes.

**Purpose**: Track generated key pairs, their algorithms, creation timestamps, and lifecycle status.

**Fields**:

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| `Id` | Guid | Primary Key, UUIDv7 | Unique identifier for the key pair metadata record |
| `Kid` | string | Required, Unique, MaxLength(100) | Key ID used in JWK/JWKS (e.g., "7f9c8b3a-5d2e-4f1b-9c8a-3e5d7f9c8b3a") |
| `Algorithm` | string | Required, MaxLength(20) | Signing algorithm (RS256, RS384, RS512, ES256, ES384, ES512, PS256) |
| `KeyType` | string | Required, MaxLength(10) | Key type (RSA, EC) |
| `KeySize` | int? | Optional | Key size in bits for RSA (2048, 3072, 4096); null for EC |
| `Curve` | string? | Optional, MaxLength(20) | Elliptic curve name for EC keys (P-256, P-384, P-521); null for RSA |
| `PublicKeyJwks` | string | Required | Public key in JWKS format (JSON string) |
| `CreatedAt` | DateTimeOffset | Required, Default=Now | When the key pair was generated |
| `Status` | string | Required, MaxLength(20), Default="Active" | Key status (Active, Revoked) |
| `RevokedAt` | DateTimeOffset? | Optional | When the key was revoked (null if active) |
| `CreatedBy` | string? | Optional, MaxLength(200) | User/identity who generated the key (if auth is implemented) |
| `DownloadCount` | int | Required, Default=0 | Number of times the private key was downloaded |

**Relationships**:

- One-to-many with `KeyDownloadRecord` (cascade delete)

**Validation Rules**:

- `Kid` must be unique across all records
- `Algorithm` must be one of: RS256, RS384, RS512, ES256, ES384, ES512, PS256
- `KeyType` must be one of: RSA, EC
- If `KeyType` = RSA, `KeySize` must be 2048, 3072, or 4096
- If `KeyType` = EC, `Curve` must be P-256, P-384, or P-521
- `Status` must be one of: Active, Revoked
- If `Status` = Revoked, `RevokedAt` must not be null
- `DownloadCount` must be ≥ 0

**State Transitions**:

```text
[Created] → Active (default)
Active → Revoked (manual revocation)
```

**Indexes**:

- Unique index on `Kid`
- Index on `CreatedAt` (for sorting/filtering)
- Index on `Status` (for filtering active/revoked keys)

### KeyDownloadRecord

Tracks when keys were downloaded for audit and compliance purposes.

**Purpose**: Maintain audit trail of private key downloads.

**Fields**:

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| `Id` | Guid | Primary Key, UUIDv7 | Unique identifier for the download record |
| `KeyPairMetadataId` | Guid | Foreign Key, Required | Reference to the key pair that was downloaded |
| `DownloadType` | string | Required, MaxLength(20) | Type of download (PrivateKey, PublicKey) |
| `DownloadedAt` | DateTimeOffset | Required, Default=Now | When the download occurred |
| `DownloadedBy` | string? | Optional, MaxLength(200) | User/identity who downloaded the key (if auth is implemented) |
| `IpAddress` | string? | Optional, MaxLength(50) | IP address of the requester (for audit) |
| `UserAgent` | string? | Optional, MaxLength(500) | User agent string (for audit) |

**Relationships**:

- Many-to-one with `KeyPairMetadata` (foreign key)

**Validation Rules**:

- `DownloadType` must be one of: PrivateKey, PublicKey
- `KeyPairMetadataId` must reference an existing `KeyPairMetadata` record

**Indexes**:

- Index on `KeyPairMetadataId` (for querying downloads per key)
- Index on `DownloadedAt` (for sorting/filtering by time)

### LicenseTokenMetadata (Optional - for audit trail)

Represents a generated license token with metadata for tracking purposes.

**Purpose**: Track generated license tokens for audit trail (optional; license tokens are not stored, only metadata).

**Fields**:

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| `Id` | Guid | Primary Key, UUIDv7 | Unique identifier for the license token metadata record |
| `TokenId` | string | Required, Unique, MaxLength(100) | JWT `jti` claim (unique token identifier) |
| `Tier` | string | Required, MaxLength(50) | License tier (community, professional, enterprise) |
| `Organization` | string? | Optional, MaxLength(200) | Organization name from license |
| `ValidFrom` | DateTimeOffset | Required | License validity start (`nbf` claim) |
| `ValidUntil` | DateTimeOffset | Required | License validity end (`exp` claim) |
| `Features` | string? | Optional | JSON array of features (e.g., ["analytics","dpop","multi-tenant"]) |
| `Limits` | string? | Optional | JSON object of limits (e.g., {"tenants":50,"users":1000}) |
| `GeneratedAt` | DateTimeOffset | Required, Default=Now | When the license token was generated |
| `GeneratedBy` | string? | Optional, MaxLength(200) | User/identity who generated the token (if auth is implemented) |

**Relationships**: None

**Validation Rules**:

- `TokenId` must be unique across all records
- `Tier` must be one of: community, professional, enterprise
- `ValidFrom` must be < `ValidUntil`
- `Features` must be valid JSON array (if not null)
- `Limits` must be valid JSON object (if not null)

**Indexes**:

- Unique index on `TokenId`
- Index on `GeneratedAt` (for sorting/filtering)
- Index on `Tier` (for filtering by tier)

## Entity Relationships Diagram

```text
KeyPairMetadata (1) ──────< (N) KeyDownloadRecord
    │
    └── Id (PK)
         │
         └──< KeyPairMetadataId (FK)

LicenseTokenMetadata (standalone, no relationships)
```

## Database Schema (SQLite)

### KeyPairMetadata Table

```sql
CREATE TABLE KeyPairMetadata (
    Id TEXT PRIMARY KEY,
    Kid TEXT NOT NULL UNIQUE,
    Algorithm TEXT NOT NULL,
    KeyType TEXT NOT NULL,
    KeySize INTEGER,
    Curve TEXT,
    PublicKeyJwks TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    Status TEXT NOT NULL DEFAULT 'Active',
    RevokedAt TEXT,
    CreatedBy TEXT,
    DownloadCount INTEGER NOT NULL DEFAULT 0,
    CHECK (Algorithm IN ('RS256', 'RS384', 'RS512', 'ES256', 'ES384', 'ES512', 'PS256')),
    CHECK (KeyType IN ('RSA', 'EC')),
    CHECK (Status IN ('Active', 'Revoked')),
    CHECK (DownloadCount >= 0)
);

CREATE UNIQUE INDEX IX_KeyPairMetadata_Kid ON KeyPairMetadata(Kid);
CREATE INDEX IX_KeyPairMetadata_CreatedAt ON KeyPairMetadata(CreatedAt);
CREATE INDEX IX_KeyPairMetadata_Status ON KeyPairMetadata(Status);
```

### KeyDownloadRecord Table

```sql
CREATE TABLE KeyDownloadRecord (
    Id TEXT PRIMARY KEY,
    KeyPairMetadataId TEXT NOT NULL,
    DownloadType TEXT NOT NULL,
    DownloadedAt TEXT NOT NULL,
    DownloadedBy TEXT,
    IpAddress TEXT,
    UserAgent TEXT,
    FOREIGN KEY (KeyPairMetadataId) REFERENCES KeyPairMetadata(Id) ON DELETE CASCADE,
    CHECK (DownloadType IN ('PrivateKey', 'PublicKey'))
);

CREATE INDEX IX_KeyDownloadRecord_KeyPairMetadataId ON KeyDownloadRecord(KeyPairMetadataId);
CREATE INDEX IX_KeyDownloadRecord_DownloadedAt ON KeyDownloadRecord(DownloadedAt);
```

### LicenseTokenMetadata Table

```sql
CREATE TABLE LicenseTokenMetadata (
    Id TEXT PRIMARY KEY,
    TokenId TEXT NOT NULL UNIQUE,
    Tier TEXT NOT NULL,
    Organization TEXT,
    ValidFrom TEXT NOT NULL,
    ValidUntil TEXT NOT NULL,
    Features TEXT,
    Limits TEXT,
    GeneratedAt TEXT NOT NULL,
    GeneratedBy TEXT,
    CHECK (Tier IN ('community', 'professional', 'enterprise')),
    CHECK (ValidFrom < ValidUntil)
);

CREATE UNIQUE INDEX IX_LicenseTokenMetadata_TokenId ON LicenseTokenMetadata(TokenId);
CREATE INDEX IX_LicenseTokenMetadata_GeneratedAt ON LicenseTokenMetadata(GeneratedAt);
CREATE INDEX IX_LicenseTokenMetadata_Tier ON LicenseTokenMetadata(Tier);
```

## Notes

### Storage Strategy

- Private keys are NEVER stored in the database
- Only public keys (JWKS format) are persisted in `KeyPairMetadata.PublicKeyJwks`
- License tokens (JWTs) are not stored; only metadata for audit trail

### Data Retention

- Key metadata retained indefinitely for audit purposes
- Revoked keys remain in database with `Status=Revoked` for historical record
- Download records retained indefinitely for compliance
- Consider adding data retention policy (e.g., archive records >2 years old)

### Performance Considerations

- Expected record counts: ~100-1000 key pairs, ~500-5000 download records
- SQLite performs well for this scale (<10MB database size)
- Indexes on foreign keys and query filters ensure fast lookups
- No performance concerns for projected usage (~10-50 operations per day)

### Migration Strategy

- Initial migration creates all three tables
- Future migrations may add fields or indexes
- Use EF Core migrations: `dotnet ef migrations add <Name>`
- Test migrations against SQLite database file before applying to production
