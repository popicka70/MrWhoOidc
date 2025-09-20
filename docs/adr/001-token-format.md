# ADR 001: Token format

Status: Accepted
Date: 2025-09-20

Context
- We need standards-compliant tokens for an OIDC Authorization Server without depending on external identity stacks.

Decision
- ID tokens: JWT (JWS) signed with RS256 using server-managed RSA keys (rotated; previous keys published in JWKS during overlap).
- Access tokens: JWT targeting API audiences (default `api`). Contains `sub` and space-delimited `scope`. Signed with RS256.
- Refresh tokens: Opaque, high-entropy random values; only stored as SHA-256 hashes in DB; rotation on refresh.

Rationale
- JWT ID tokens are required by OIDC; RS256 maximizes ecosystem compatibility.
- JWT access tokens simplify resource-server validation (no introspection for MVP) and fit our key rotation model.
- Opaque refresh tokens minimize exfiltration impact and align with rotation + revocation auditing.

Consequences
- Resource servers must validate JWTs using the JWKS and issuer.
- When opaque access tokens are introduced, we will add the `/introspect` endpoint.

Security notes
- Private keys never leave the server; JWKS serves only public components.
- Keys rotate automatically; retired keys remain published until expiration overlap ends.
