# Pairwise Subject Identifiers

## Overview

Pairwise subject identifiers are a privacy-enhancing feature defined in the [OpenID Connect Core 1.0 specification](https://openid.net/specs/openid-connect-core-1_0.html#PairwiseAlg). When enabled, the IdP provides a different `sub` (subject) claim value to each client for the same user, preventing clients from correlating users across different applications.

## Current State

MrWhoOidc supports **public** and **pairwise** subject identifiers:
- **public**: the same `sub` value is returned to all clients for a given user
- **pairwise**: a stable, non-public `sub` is issued per client (or per sector identifier)
- Advertised in discovery document: `"subject_types_supported": ["public", "pairwise"]`

## Why Implement Pairwise?

### Privacy Benefits
- Prevents cross-client user tracking without user consent
- Clients cannot correlate users by comparing `sub` values
- Required for compliance with certain privacy regulations (GDPR principle of data minimization)

### Use Cases
- Multi-tenant SaaS where tenants shouldn't correlate users
- Consumer-facing applications with strict privacy requirements
- B2B scenarios where partners shouldn't share user identifiers

## Implemented Behavior

### 1. Database Schema

Pairwise mappings are persisted via EF Core migrations.

Key points:
- Mappings are tenant-scoped and keyed by (tenant, user, sector)
- The stored pairwise subject value is unique within a tenant

### 2. Client Configuration

The client configuration includes:
- `SubjectType`: `public` (default) or `pairwise`
- `SectorIdentifierUri` (optional): HTTPS URI used to group multiple clients into a shared sector

### 3. Sector Identifier Resolution

Sector identifier determines which clients share the same pairwise `sub`.

Rules:
- If `SectorIdentifierUri` is configured:
  - Must be a valid absolute HTTPS URI
  - The URI is fetched and must return a JSON array of redirect URIs
  - The fetched set must include all of the client’s configured redirect URIs
  - The sector identifier is the host of `SectorIdentifierUri` (normalized to lowercase)
  - If validation fails or the URI is unreachable at issuance time, token issuance fails (no fallback)
- If `SectorIdentifierUri` is not configured:
  - Sector identifier is derived from the host of the client’s allowed login redirect URIs
  - All allowed login redirect URIs must share exactly one host
  - The derived host is normalized to lowercase

### 4. Pairwise Subject Generation

Pairwise subjects are generated using cryptographically secure randomness and encoded as base64url (no padding), then persisted for stable reuse.

### 5. Token and UserInfo Behavior

When a client is configured for `pairwise`, the `sub` claim value is selected via the pairwise mapping service (stable per user+sector). Public clients continue to receive the public subject.

### 6. Discovery Document

The discovery document advertises both subject identifier types via `subject_types_supported`.

### 7. Admin UI

- Client configuration UI includes subject type selection and optional sector identifier URI
- Validation enforces that sector identifier URI is an absolute HTTPS URI (when using pairwise)

## Testing Requirements

### Unit Tests
- Sector identifier resolution logic
- Pairwise subject generation and persistence
- Same user + same sector = same pairwise sub
- Same user + different sector = different pairwise sub

### Integration Tests
- Token contains correct pairwise sub for pairwise clients
- Token contains real sub for public clients
- UserInfo endpoint returns correct sub type
- Sector identifier URI validation (HTTPS, valid JSON, redirect_uri matching)

### Compliance Tests
- Verify against OIDC certification test suite for pairwise subjects

## Migration Considerations

### Backward Compatibility
- Default new clients to `public` subject type
- Existing clients remain `public` unless explicitly changed
- Changing a client from public to pairwise will change its `sub` values

### Data Migration
- No migration needed for existing users/clients
- Pairwise mappings created on-demand

## Security Considerations

1. **Pairwise sub must be unpredictable** - Use cryptographically secure generation
2. **Sector identifier validation** - Strictly validate sector_identifier_uri responses
3. **Storage security** - Pairwise mappings are sensitive (could deanonymize users)
4. **Audit logging** - Log pairwise identifier creation for security audits

## Estimated Effort

| Component | Effort |
|-----------|--------|
| Database schema | 2 hours |
| Entity/model changes | 2 hours |
| Sector identifier resolver | 4 hours |
| Pairwise subject service | 4 hours |
| Token generation integration | 4 hours |
| Admin UI changes | 4 hours |
| Unit tests | 4 hours |
| Integration tests | 4 hours |
| Documentation | 2 hours |
| **Total** | **~30 hours** |

## References

- [OIDC Core 1.0 - Pairwise Identifier Algorithm](https://openid.net/specs/openid-connect-core-1_0.html#PairwiseAlg)
- [OIDC Core 1.0 - Subject Identifier Types](https://openid.net/specs/openid-connect-core-1_0.html#SubjectIDTypes)
- [RFC 7636 - Sector Identifier](https://openid.net/specs/openid-connect-registration-1_0.html#SectorIdentifierValidation)

## Related Files

- `MrWhoOidc.Auth/Protocols/OidcConstants.cs` - Subject type constants (already added)
- `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs` - Discovery document
- `MrWhoOidc.WebAuth/Handlers/TokenHandler.cs` - Token generation
- `MrWhoOidc.Auth/Persistence/Entities/Client.cs` - Client entity
