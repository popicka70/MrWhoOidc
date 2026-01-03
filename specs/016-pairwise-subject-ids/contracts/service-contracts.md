# Service Contracts: Pairwise Subject Identifiers

**Derived from**: `docs/future-plans/pairwise-subject-identifiers.md` and `specs/016-pairwise-subject-ids/spec.md`

## ISectorIdentifierResolver

Responsibility: Determine the sector identifier used for pairwise subject computation.

Proposed contract:

- `Task<string> ResolveAsync(Client client, CancellationToken ct = default)`

Rules:
- If `SectorIdentifierUri` is set:
  - Must be HTTPS
  - Response must be JSON array of redirect URIs
  - All configured client redirect URIs must be present
  - Sector identifier is derived from the `SectorIdentifierUri` host
  - If the URI cannot be fetched/validated at issuance time, issuance must fail (no fallback)
- Else:
  - Sector identifier is derived from the host of the first configured redirect URI

## IPairwiseSubjectService

Responsibility: Create/retrieve the pairwise subject for a user+sector.

Proposed contract:

- `Task<string> GetOrCreateAsync(Guid tenantId, Guid userId, string sectorIdentifier, CancellationToken ct = default)`

Rules:
- Lookup mapping by (tenantId, userId, sectorIdentifier)
- If present, return persisted `PairwiseSubject`
- If missing, generate `PairwiseSubject` using CSPRNG random bytes encoded as base64url (no padding), persist, and return

## Subject Selection

Responsibility: pick correct `sub` per client.

Rules:
- If client subject type is `public`: `sub = user.Id` (existing behavior)
- If client subject type is `pairwise`:
  - `sector = ResolveAsync(client)`
  - `sub = GetOrCreateAsync(tenantId, user.Id, sector)`
- Apply consistently to ID token and UserInfo.
