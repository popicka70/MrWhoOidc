MrWhoOidc OIDC Server Backlog

Status summary (MVP scope)
- [x] M1 Infrastructure & Persistence (core entities, migrations, Aspire Postgres, persistent DataProtection keys)
- [x] M2 Crypto & Discovery (key store, JWKS w/ cache headers, discovery w/ configurable issuer)
- [x] M3 User auth & Authorization (login, consent, /authorize with Code + PKCE)
- [x] M4 Token endpoint (authorization_code, ID/Access, Refresh with rotation; configurable API audiences)
- [x] M5 UserInfo + Logout (sub-based userinfo, local + RP-initiated logout)
- [x] M6 Introspection & Revocation (introspection enforces client policy + audience match, supports JWT/opaque, DPoP nonce + replay checks, optional mTLS; privacy-based response shaping implemented)
- [x] M7 Key rotation & hardening (automated key rotation + JWKS overlap, rate limiting, antiforgery, login backoff, WWW-Authenticate, CORS allow-list, HTTPS/HSTS/forwarded headers)
- [x] M8 Observability, DX & Docs (OpenTelemetry wired; custom meters + tags; ADRs + samples; docs partially done; JAR/PAR admin UX added)

Overview
- Goal: Implement an OpenID Connect (OIDC) Authorization Server where `MrWhoOidc.WebAuth` hosts the endpoints and UI (login/logout/consent) and `MrWhoOidc.Auth` contains the non-visual logic, protocols, persistence, and crypto.
- Constraints: Do not use OpenIddict or Microsoft identity platforms (e.g., Azure AD). Use our own implementation with standard .NET libraries where needed.
- Persistence: PostgreSQL managed by Aspire in `MrWhoOidc.AppHost` and consumed by `MrWhoOidc.WebAuth`/`MrWhoOidc.Auth`.
- UI Tech: Razor Pages in `MrWhoOidc.WebAuth` for login, logout, consent. The workspace also contains a Blazor project (`MrWhoOidc.Web`).
- Target: .NET 9 across projects.

Coding guidelines
- In `MrWhoOidc.Auth` (library code), always use `ConfigureAwait(false)` on `await` for asynchronous calls to avoid capturing SynchronizationContext. Do not apply this guideline to `MrWhoOidc.WebAuth` or other application entrypoint projects.

Milestones

M0 – Repository readiness
- [x] Verify project references: `MrWhoOidc.WebAuth` -> `MrWhoOidc.Auth`.
- [x] Decide token format for access tokens: JWT for API audience (temporary `api`).
- [x] Choose password hashing: Argon2id (Isopoh).
- [x] Define environment naming/connection: Aspire `authdb`.
- Deliverables:
  - [x] ADR: token format choice and password hashing algorithm.
  - [x] Checklist/implementation for security headers, CORS strategy (allow-list).

M1 – Infrastructure & Persistence
- [x] Add PostgreSQL in Aspire (`MrWhoOidc.AppHost`) with persistent volume.
- [x] Propagate connection string to `MrWhoOidc.WebAuth`.
- [x] Add EF Core (in `MrWhoOidc.Auth`).
- [x] Create `AuthDbContext` and entities: `User`, `Client`, `SigningKey`, `AuthorizationCode`, `Consent`, `Token` (refresh), `DataProtectionKey`.
- [x] Migrations and database initialization (auto-migrate on startup).
- [x] Seed script for one test client and one test user (now includes profile/email).
- [x] Persist ASP.NET Core DataProtection keys to DB (antiforgery survives restarts).

M2 – Crypto & Discovery
- [x] RSA key management with persisted JWKs, `kid`, `alg`.
- [x] JWKS endpoint with cache headers.
- [x] Discovery endpoint with configurable issuer; advertises token/revocation endpoints and `private_key_jwt` support.
- [x] Discovery advertises `require_pushed_authorization_requests` when server requires PAR.

M3 – User auth & Authorization Endpoint (Code Flow + PKCE)
- [x] Razor Pages: `Login` (with antiforgery + basic lockout/backoff) and `Consent`.
- [x] `/authorize` GET: validation, login requirement, consent enforcement (per-scope), code issuance + state; persists `auth_time` for ID tokens.

M4 – Token Endpoint & ID/Access/Refresh Tokens
- [x] `/token` grant `authorization_code` + PKCE verifier validation (S256 fix to full base64url).
- [x] ID token (nonce, auth_time, at_hash + optional profile/email), access token (JWT + scope claim); refresh token issuance + rotation.
- [x] Refresh token grant implemented.
- [x] Configurable API audiences used for access token `aud`.

M5 – UserInfo, Logout/End Session
- [x] `/userinfo`: validates bearer token, returns claims; sends `invalid_token` on failures; short private cache headers; adds `WWW-Authenticate` header on 401; filters claims by granted scopes.
- [x] Logout: local `/logout` and RP-initiated `/connect/endsession` with allow-listed `post_logout_redirect_uri`.

M6 – Introspection & Revocation (optional for MVP)
- [x] `/introspect` for confidential clients (supports JWT tokens now; opaque tokens supported when enabled).
- [x] Validate JWT access tokens against local keys; return `{ active:false }` on failures.
- [x] Respond with: `active`, `token_type`, `scope`, `sub`, `username`, `aud`, `iss`, `iat`, `nbf`, `exp`.
- [x] Rate limit policy `rl-introspect` (~60/min/IP).
- [x] Add metrics: requests, active_true/false, per `client_id` bucket (hash/bucketize for privacy).
- [x] Audit log minimal fields (client_id, outcome, ip, aud).
- [x] Per-client policy knobs persisted in DB: introspection audiences allow-list, response fields allow-list, optional mTLS thumbprints.

- Phase 2 – Policy & authorization
  - [x] Restrict which clients may call introspection (allow-list per client) – via DB and global config fallback.
  - [x] Enforce audience match: only allow introspection if caller is authorized for the token `aud` (resource).
  - [x] Return limited fields based on caller policy (privacy by default) via default allow-list and per-client DB allow-list.
  - [x] Optionally include `client_id` in response when authorized by policy (included when available for opaque/refresh tokens; JWT access tokens typically don't carry `client_id`).

- Phase 3 – Opaque access tokens
  - [x] Add server config to toggle opaque access tokens for APIs (per audience or global).
  - [x] Persist access tokens (opaque): hash, `client_id`, `user_id`, `scope[]`, `aud`, `exp`, `jti`, `revoked_at`.
  - [x] Update `/token` to issue opaque tokens when configured; include `jti`.
  - [x] Update `/introspect` to resolve opaque tokens from DB and reflect `revoked_at`/`exp` in `active`.
  - [x] Background cleanup for expired tokens (hourly hosted service; complements opportunistic cleanup).

- Phase 4 – Fidelity & extensions
  - [x] Discovery: advertise `introspection_endpoint_auth_methods_supported` and signing algs.
  - [x] Include `jti`, `cnf` (for DPoP/bound tokens) when available.
  - [x] Support `aud` as array in response when token carries multiple audiences.
  - [x] Optional mTLS client auth for introspection (config + per-client DB override).
  - [x] Implement `token_type_hint` handling for refresh tokens (gated by `Auth:AllowRefreshTokenIntrospection`; owner-only RT).
  - [x] Discovery: advertise `introspection_token_types_supported` (non-standard, DX).

New Backlog – DPoP / Bound Access Tokens (RFC 9449)
- Phase 1 – Core support
  - [x] Token endpoint: accept `DPoP` proofs and issue DPoP-bound access tokens (include `cnf.jkt`).
  - [x] Validate DPoP at `/userinfo` and `/introspect` (verify `htm/htu/iat/jti`, `ath` matches token, replay cache).
  - [x] Discovery: advertise `dpop_signing_alg_values_supported`, `dpop_bound_access_tokens: true`.

- Phase 2 – Nonce and robustness
  - [x] Support DPoP nonce challenges (`WWW-Authenticate: DPoP ...` and `DPoP-Nonce` header); Blazor backchannel retries with nonce.
  - [~] Persist DPoP replay IDs to prevent replays (bounded window via distributed replay cache; in-memory cache implemented; distributed store pending).

- Phase 3 – Samples & docs
  - [x] Sample API validating DPoP (MrWhoOidc.ApiService).
  - [x] Blazor client using DPoP (attaches proofs to token and userinfo; handles nonce).
  - [ ] Docs: how to configure and use DPoP; privacy and security considerations.

New Backlog – JAR & JARM
- JAR (JWT-Secured Authorization Request)
  - [x] `/authorize`: accept signed `request` objects (JWT) and validate (iss/aud/client_id, exp/nbf).
  - [x] Verify signatures using per-client public JWKs (DB first, config fallback); support `RS256`/`ES256`.
  - [x] Precedence/immutability: parameters in `request` take precedence; enforce immutable claims.
  - [x] PAR support: `/par` endpoint (EF-backed store), returns `request_uri`; sanitize address bar during `/authorize`.
  - [x] Blazor integration: OIDC `OnRedirectToIdentityProvider` builds PAR (`request_uri`) with the framework-issued `state/nonce/PKCE`.
  - [x] Admin UX: extract public JWKS from private JWK, sign a test JWT, validate against saved JWKS; explicit Save handler.
  - [x] Require PAR policy: global (server setting) + per-client (DB); discovery advertises global requirement.
  - [x] Enforce request object max size (server setting) for both `/par` and `/authorize?request=`; record request sizes in metrics.
  - [x] PAR cleanup hosted service (consumed/expired rows).
  - [x] Metrics: counters for JAR valid/invalid and PAR requests/success/fail/consumed + histograms for request size.
  - [x] PAR hardening: per-client rate limit (in-memory) and pending storage quota per client; error responses include correlation id; logs include client bucket and corr.
  - [x] JAR: enforce max request lifetime and clock skew limits.

- JARM (JWT-secured authorization response mode)
  - [ ] Support `response_mode` values `form_post.jwt` and `query.jwt` for code flow.
  - [ ] Issue signed JARM JWT (iss/aud/iat/exp, `code`, `state`, `c_hash`, `s_hash` when applicable) with AS signing keys.
  - [ ] Optional: encrypted JARM responses when client has encryption JWK.
  - [ ] Discovery: add `response_modes_supported` with `*.jwt`, `authorization_response_iss_parameter_supported`, and advertise signing/encryption algs.
  - [ ] Tests and samples for JAR/JARM paths; docs updates.

Next steps (proposal)
- Server hardening
  - [ ] Move per-client `/par` limiter to distributed store (Redis) and make limits configurable per environment.
  - [ ] Add per-client persistent storage quotas and admin-configurable limits; surface utilization in Admin UI.
  - [ ] Add correlation id and structured logging to all `/authorize` error paths (not only JAR); include request size and client bucket tags in metrics.

- Admin UX
  - [x] Surface JWKS/Require PAR badges in clients list; link to Edit.
  - [ ] Allow uploading a JWKS file; pretty-print/compact toggles; kid uniqueness validation.

- Client/Blazor
  - [ ] Optional: feature flag to turn JAR/PAR on/off per environment.
  - [ ] Persist code_verifier securely in auth properties (verify compatibility across restarts).

- Docs
  - [ ] How to enable JAR/PAR: configure client JWKS, set private JWK in Blazor, flows, troubleshooting.
  - [ ] Admin guide for JWKS management (extract/sign/validate).
  - [ ] Known errors: state unprotect, missing JWKS, algorithm mismatch; remediation steps.

- Tests
  - [ ] Integration tests for JAR/PAR: success, missing JWKS, invalid signature, immutability violations, expired PAR, require-PAR policy.
  - [ ] Load tests for `/par` to size rate-limit thresholds and storage quotas.
