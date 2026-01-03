# Admin Surface: Pairwise Subject Identifiers

**Derived from**: `docs/future-plans/pairwise-subject-identifiers.md` and `specs/016-pairwise-subject-ids/spec.md`

## Client Configuration Fields

Admin must be able to set:

- `SubjectType`: `public` or `pairwise` (default `public`)
- `SectorIdentifierUri` (optional): HTTPS URI

## Validation Rules

- `SubjectType` must be one of: `public`, `pairwise`
- If `SectorIdentifierUri` is set:
  - Must be HTTPS
  - Must validate redirect URIs per spec policy (JSON array returned; client redirect URIs included)

## Operator Guidance

- Switching `public` → `pairwise` changes `sub` values for that client (expected breaking change).
- Switching `pairwise` → `public` reverts to public `sub` behavior.
