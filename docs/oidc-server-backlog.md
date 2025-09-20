MrWhoOidc OIDC Server Backlog

Status summary (MVP scope)
- [x] M1 Infrastructure & Persistence (core entities, migrations, Aspire Postgres, persistent DataProtection keys)
- [x] M2 Crypto & Discovery (key store, JWKS w/ cache headers, discovery w/ configurable issuer)
- [x] M3 User auth & Authorization (login, consent, /authorize with Code + PKCE)
- [x] M4 Token endpoint (authorization_code, ID/Access, Refresh with rotation; configurable API audiences)
- [x] M5 UserInfo + Logout (sub-based userinfo, local + RP-initiated logout)
- [~] M6 Introspection & Revocation (revocation implemented with client auth, idempotency + audit; introspection pending)
- [x] M7 Key rotation & hardening (automated key rotation + JWKS overlap, rate limiting, antiforgery, login backoff, WWW-Authenticate, CORS allow-list, HTTPS/HSTS/forwarded headers)
- [~] M8 Observability, DX & Docs (OpenTelemetry wired; custom meters + tags; ADRs + samples; docs pending)

Overview
- Goal: Implement an OpenID Connect (OIDC) Authorization Server where `MrWhoOidc.WebAuth` hosts the endpoints and UI (login/logout/consent) and `MrWhoOidc.Auth` contains the non-visual logic, protocols, persistence, and crypto.
- Constraints: Do not use OpenIddict or Microsoft identity platforms (e.g., Azure AD). Use our own implementation with standard .NET libraries where needed.
- Persistence: PostgreSQL managed by Aspire in `MrWhoOidc.AppHost` and consumed by `MrWhoOidc.WebAuth`/`MrWhoOidc.Auth`.
- UI Tech: Razor Pages in `MrWhoOidc.WebAuth` for login, logout, consent. The workspace also contains a Blazor project (`MrWhoOidc.Web`), but the server UI will be Razor Pages.
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
- [ ] `/introspect` for confidential clients (when opaque access tokens are enabled).
- [x] `/revoke` for refresh tokens with client auth (basic/post), idempotency, and audit.

M7 – Key Rotation & Hardening
- [x] Automated signing key rotation + JWKS publishing overlap (with hosted service).
- [x] Rate limiting middleware:
  - [x] `/authorize` (60/min/IP)
  - [x] `/token` (30/min/IP)
  - [x] `/userinfo` (120/min/IP)
- [x] Anti-forgery tokens on login and consent forms.
- [x] Basic lockout/backoff on login (in-memory, per IP+username).
- [x] RFC-aligned auth error headers (`WWW-Authenticate` on `invalid_token` for `/userinfo`).
- [x] CORS allow-list for `/token` and `/userinfo` (tightened to minimal headers/methods/origins).
- [x] HTTPS redirection/HSTS and forwarded headers behind reverse proxy.
- [~] Optional distributed/global rate limiter (token bucket) wiring (coarse throttle).

M8 – Observability, DX & Docs
- [x] Logging & tracing via `MrWhoOidc.ServiceDefaults`/OpenTelemetry (base wiring).
- [x] Metrics: custom meters for authorize/token/userinfo/revoke with tags (grant_type/outcome); meter registered for export.
- [x] Dev UX: `dotnet run -- --seed` command; Postman collection; `.http` sample.
- [~] Documentation in `/docs` (setup, flows, endpoints, examples) – ADRs done; full docs pending.

Backlog – Introspection (RFC 7662)
- Phase 1 – Basic JWT introspection (current)
  - [x] Endpoint: POST `/introspect` accepts `token`, optional `token_type_hint`.
  - [x] Client auth: `client_secret_basic`, `client_secret_post`, `private_key_jwt` (aud = introspection endpoint).
  - [x] Validate JWT access tokens against local keys; return `{ active:false }` on failures.
  - [x] Respond with: `active`, `token_type`, `scope`, `sub`, `username`, `aud`, `iss`, `iat`, `nbf`, `exp`.
  - [x] Rate limit policy `rl-introspect` (~60/min/IP).
  - [ ] Add metrics: requests, active_true/false, per `client_id` bucket (hash/bucketize for privacy).
  - [ ] Audit log minimal fields (client_id, outcome, ip, aud).

- Phase 2 – Policy & authorization
  - [ ] Restrict which clients may call introspection (allow-list per client).
  - [ ] Enforce audience match: only allow introspection if caller is authorized for the token `aud` (resource).
  - [ ] Add `introspection permissions` model/table or config (client_id -> allowed audiences/resources).
  - [ ] Return limited fields based on caller policy (privacy by default).
  - [ ] Optionally include `client_id` claim in response when caller is authorized to see it.

- Phase 3 – Opaque access tokens
  - [ ] Add server config to toggle opaque access tokens for APIs (per audience or global).
  - [ ] Persist access tokens (opaque): hash, `client_id`, `user_id`, `scope[]`, `aud`, `exp`, `jti`, `revoked_at`.
  - [ ] Update `/token` to issue opaque tokens when configured; include `jti`.
  - [ ] Update `/introspect` to resolve opaque tokens from DB and reflect `revoked_at`/`exp` in `active`.
  - [ ] Background cleanup for expired tokens.

- Phase 4 – Fidelity & extensions
  - [ ] Discovery: advertise `introspection_endpoint_auth_methods_supported` and signing algs.
  - [ ] Support `aud` as array in response when token carries multiple audiences.
  - [ ] Include `jti`, `cnf` (for DPoP/bound tokens) when available.
  - [ ] Optional mTLS client auth for introspection (future hardening).
  - [ ] Implement `token_type_hint` handling for refresh tokens (if introspection of RT is desired/allowed).

- Phase 5 – Security hardening
  - [ ] Constant-time validation and uniform responses to avoid oracle characteristics.
  - [ ] Strict input size limits on form fields; reject overly large payloads.
  - [ ] No CORS for `/introspect`; ensure HTTPS/HSTS, appropriate cache headers.
  - [ ] Structured audit events with correlation id and user agent/IP.

- Phase 6 – Observability & tests
  - [ ] Tracing: span attributes (client_id bucket, outcome, token_type_hint).
  - [ ] Unit tests: client auth (basic/post/pkjwt), JWT path (valid/expired/aud-mismatch), opaque path.
  - [ ] Integration tests with a sample API calling `/introspect`.
  - [ ] Docs and samples: endpoint usage, example responses, `private_key_jwt` configuration.

Next steps (proposed)
1) Observability & Metrics
   - Add dashboards; refine metric dimensions (client_id bucketization, error types).

2) Security hardening
   - Replace coarse global limiter with a true distributed limiter per policy (Redis-backed) if scale-out required.

3) Protocol fidelity improvements
   - Complete `private_key_jwt` by configuring per-client public JWKs (config or DB) and enabling validation.
   - Consider RFC 8707 resource indicators for APIs; keep discovery metadata strictly standard.
   - Implement `/introspect` with opaque access token mode for confidential clients.

4) Dev experience & Docs
   - Write full documentation in `/docs` (setup, flows, endpoints, examples) and export a Postman JSON.
   - Scripted seeding for AppHost, or developer task wiring.
