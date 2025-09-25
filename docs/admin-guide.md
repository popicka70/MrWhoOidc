# Admin guide: Multi-provider setup and OBO policy

Updated: 2025-09-25 (first draft)

This guide helps administrators configure providers, keys, client mappings, claim mapping, and OBO (token exchange) policy for common scenarios. Screenshots will be added; for now, follow the steps and examples.

## Prerequisites

- Access to the Admin UI (role: Admin)
- Basic knowledge of your external IdPs (issuer URLs, client IDs/secrets, JWKS)
- If using Redis for replay/rate-limit features, ensure `ConnectionStrings:redis` is configured for production

## 1) Providers

Add one or more external OpenID Connect providers.

- Navigate: Admin → Providers → New
- Required fields (typical):
  - Display name (shown on the provider picker)
  - Issuer URL (e.g., https://login.example.com)
  - Client ID and secret (if using code flow with client_secret)
  - Redirect URIs must include your WebAuth callback
- Optional:
  - Prompt hints (e.g., `prompt=login`), ACR values (e.g., `acr=urn:mfa`)
  - Scopes beyond `openid profile email`

Save and test sign-in. See Provider Picker section for UX hints.

## 2) Keys (PEM/JWK)

Keys sign and/or encrypt tokens and request objects. You can import PEM (PKCS8/PKCS1/EC) and convert to JWK.

- Navigate: Admin → Keys
- Import PEM or paste JWK
- Verify preview: kid, alg, kty, use, thumbprint
- Validation ensures alg/kty/use are consistent (e.g., RS256→RSA; ES256→EC P-256; use = sig|enc)
- Duplicate kid detection prevents collisions

Tips
- Keep at least two signing keys to support seamless rotation
- For request-object signing algs, align with `Auth:RequestObjectAllowedAlgorithms` (see replay cache doc)

See also: docs/jar-replay-cache.md for discovery alignment and TTL/skew guidance.

## 3) Client mappings

Map relying-party clients to behaviors and capabilities.

- Navigate: Admin → Clients → New or Edit
- Configure:
  - Allowed grant types (authorization_code, client_credentials, token-exchange)
  - Redirect URIs and post-logout URIs
  - Authentication methods (secret vs private_key_jwt)
  - Allowed audiences/resources and scopes
  - Token formats (JWT vs opaque) and lifetimes

## 4) Claim mappings

Define how upstream claims (from providers) become local claims and what flows emit them.

- Navigate: Admin → Claim mappings
- Examples:
  - Map upstream `email` to local `email`
  - Combine `given_name` + `family_name` → local `name`
  - Normalize `groups` or `roles` for downstream APIs

Validate via a test login and inspect the issued ID/access token in your app or via test utilities.

## 5) OBO policy (Token Exchange)

Configure per-client OBO rules that constrain exchanges, audiences, scopes, lifetimes, and DPoP bridging.

- Navigate: Admin → Clients → Edit → OBO tab
- Fields (summary):
  - Enable OBO
  - Allowed callers (client_id allow-list)
  - Allowed source audiences (subject token aud)
  - Allowed target audiences/resources
  - Allowed scopes (intersection with subject and request)
  - Max delegation depth and max lifetime
  - DPoP bridging mode: Deny | RequireSameJkt | AllowSameJktOnly

Reference: docs/obo-client-policy.md for full field descriptions and examples.

## 6) Provider picker UX (a11y + mobile)

Users see a list of available providers. The picker supports accessibility basics and mobile layout.

- Remembered provider hint: optionally pre-select or highlight the last provider used
- A11y: labels, roles, tab order, focus visible
- Mobile: responsive layout and touch targets

## 7) JAR and replay protection

If clients send JWT-secured authorization requests (JAR), enable replay protection.

- Production: configure Redis via `ConnectionStrings:redis`
- Auth options (`appsettings*.json`):
  - `Auth:RequestObjectClockSkewSeconds`
  - `Auth:RequestObjectReplayTtlSeconds`
  - `Auth:RequestObjectMaxLifetimeSeconds`
  - `Auth:RequestObjectAllowedAlgorithms`
- Discovery advertises `request_object_signing_alg_values_supported` from the allow-list

See: docs/jar-replay-cache.md

## 8) Rate limiting and headers (token/introspect)

When enabled with Redis, endpoints like /token and /introspect return appropriate rate-limit headers and 429 with Retry-After.

## 9) Troubleshooting

- External OIDC UX
  - Correlation IDs flow from start → callback in structured logs
  - Friendly error pages for cancel/timeout/invalid_scope (localization-ready)
- Token Exchange
  - `invalid_target`, `insufficient_scope`, DPoP errors (`dpop_same_key_required`, `dpop_bridging_not_supported`)
- Keys
  - Ensure alg/kty/use alignment; check duplicate `kid`

## Appendix: Minimal checklists

- New provider
  - Issuer URL resolves; metadata reachable
  - Client ID/secret valid; redirect URI registered
  - Test login round-trip works; claims as expected
- New OBO policy
  - Caller listed in Allowed callers
  - Target audience/resource allowed
  - Scopes narrowed appropriately
  - DPoP bridging mode matches upstream token binding

---

Related docs
- docs/obo-client-policy.md
- docs/obo-dpop-requiresamejkt-e2e.md
- docs/jar-replay-cache.md
