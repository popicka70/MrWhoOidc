# ADR-0007: Per-Provider JWKS Endpoint Path

Status: Accepted 2025-09-27

## Context
The system now supports outbound JAR and optional per-provider signing keys. Upstream IdPs (or other relying services) need a stable, cacheable JWKS location to validate artifacts (e.g., signed request objects, future logout tokens). We already expose a server-wide JWKS at `/jwks` and optional client and aggregated provider endpoints. A per-provider endpoint was required.

Options considered:
1. `/providers/{providerName}/jwks`
2. `/.well-known/providers/{providerName}/jwks`
3. `/.well-known/{providerName}/jwks`
4. Only an aggregate `/providers/jwks`
5. Custom composite metadata under `.well-known` embedding all provider keys.

Evaluation dimensions included standards alignment, clarity, caching, complexity, risk of client over-assumption, and future extensibility. Parameterized paths under `/.well-known` are non-standard and risk implying discovery semantics the OpenID metadata spec does not define. The REST-like `/providers/{name}/jwks` path keeps expectations modest and avoids polluting `.well-known`.

## Decision
Adopt `/providers/{providerName}/jwks` as the canonical per-provider JWKS endpoint. Do not add a `/.well-known/` variant.

## Consequences
- Documentation: Clearly distinguish server JWKS (`/jwks`) vs per-provider JWKS.
- No changes to primary OpenID discovery document (avoid adding proprietary per-provider locations).
- Rotation strategy documented separately (overlap old/new publishable keys).
- Simpler caching & ETag semantics; per-provider isolation minimizes invalidation blast radius.

## Implementation Notes
- Added `Publishable` boolean column to `IdentityProviderKeys`; only `Active && Publishable` (and purpose=Signing unless encryption inclusion is enabled) are emitted.
- Filtering updated in `PublicJwksCache`.
- Migration: `AddPublishableToIdentityProviderKeys` plus supporting index `(IdentityProviderId, Active, Publishable)`.
- Backlog updated to mark path decision and remove open path question.

## Future Considerations
- If discovery advertisement is requested, consider a vendor extension property listing provider JWKS URIs, but keep it opt-in.
- Potential realm scoping later could nest the segment: `/realms/{realm}/providers/{providerName}/jwks`.
- If x5c chains are ever required, add optional query parameter or versioned path (`/providers/{name}/jwks?v=2`).

## Alternatives Rejected
- `/.well-known/providers/{name}/jwks`: Non-standard; invites expectation of appearance in main metadata; offers no real benefit.
- Aggregate-only: Insufficient isolation; larger payload; harder rotation impact containment.
- Composite mega-metadata: Adds parsing complexity and divergence from common JWKS consumption patterns.
