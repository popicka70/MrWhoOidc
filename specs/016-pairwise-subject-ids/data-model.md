# Data Model: Pairwise Subject Identifiers

**Derived from**: `docs/future-plans/pairwise-subject-identifiers.md` and `specs/016-pairwise-subject-ids/spec.md`

## Entities

### Client (existing)

Add fields (names TBD to match existing conventions):

- `SubjectType` (string, required): `public` (default) or `pairwise`
- `SectorIdentifierUri` (string?, optional): HTTPS URI used to validate redirect URIs and derive sector identifier

Notes:
- Default for existing/new clients remains `public`.

### Pairwise Subject Mapping (new)

Purpose: persist mapping of (tenant, user, sector identifier) → pairwise `sub`.

Proposed fields:

- `Id` (Guid, PK): generated via `GuidHelper.NewId()`
- `TenantId` (Guid, required): tenant scope for isolation
- `UserId` (Guid, required): FK to users
- `SectorIdentifier` (string, required): normalized sector identifier (host-based)
- `PairwiseSubject` (string, required): base64url-encoded random identifier (no padding)
- `CreatedAt` (DateTimeOffset, required)

## Constraints & Indexes

- Unique: (`TenantId`, `UserId`, `SectorIdentifier`)
- Unique: (`TenantId`, `PairwiseSubject`) (tenant-scoped uniqueness)
- Index: (`TenantId`, `UserId`)
- Index: (`TenantId`, `SectorIdentifier`)

## Notes

- Pairwise subjects are created on-demand and reused.
- Sector identifier is derived from either `sector_identifier_uri` host or redirect URI host fallback.
