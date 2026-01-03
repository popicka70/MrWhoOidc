# Research: Pairwise Subject Identifiers

**Primary source**: `docs/future-plans/pairwise-subject-identifiers.md`

## Key Decisions Captured

- OIDC feature scope: support both `public` and `pairwise` subject identifier types.
- Client configuration: per-client subject type with default `public`; optional `sector_identifier_uri`.
- Sector resolution:
  - If `sector_identifier_uri` is configured: must be HTTPS; must return JSON array of redirect_uris; all client redirect URIs must be present; sector is derived from the URI host.
  - Fallback: sector derived from the host of the first configured redirect URI.
- Persistence:
  - Store mapping per (tenant, user, sector identifier) → pairwise `sub`.
  - Mapping is created on-demand and reused.
- Token/UserInfo behavior:
  - For pairwise clients, `sub` is derived from the pairwise mapping.
  - For public clients, `sub` remains the user’s public identifier.
- Discovery:
  - `subject_types_supported` must include both `public` and `pairwise`.

- Pairwise `sub` generation:
  - CSPRNG random bytes encoded as base64url (no padding), persisted per (tenant, user, sector).

## Open Questions

- None outstanding.
