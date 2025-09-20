MrWhoOidc OIDC Server Backlog

Status summary (MVP scope)
- [x] M1 Infrastructure & Persistence (partial: core entities, migrations, Aspire Postgres, persistent DataProtection keys)
- [x] M2 Crypto & Discovery (JWKS, discovery with cache headers, configurable issuer)
- [x] M3 User auth & Authorization (login, consent, /authorize with Code + PKCE)
- [x] M4 Token endpoint (authorization_code, ID/Access, Refresh with rotation)
- [x] M5 UserInfo + Logout (basic sub-only userinfo, local + RP-initiated logout)
- [ ] M6 Introspection & Revocation (revocation implemented)
- [ ] M7 Key rotation & hardening (rate limiting + antiforgery + lockout/backoff done)
- [ ] M8 Observability, DX & Docs

Overview
- Goal: Implement an OpenID Connect (OIDC) Authorization Server where `MrWhoOidc.WebAuth` hosts the endpoints and UI (login/logout/consent) and `MrWhoOidc.Auth` contains the non-visual logic, protocols, persistence, and crypto.
- Constraints: Do not use OpenIddict or Microsoft identity platforms (e.g., Azure AD). Use our own implementation with standard .NET libraries where needed.
- Persistence: PostgreSQL managed by Aspire in `MrWhoOidc.AppHost` and consumed by `MrWhoOidc.WebAuth`/`MrWhoOidc.Auth`.
- UI Tech: Razor Pages in `MrWhoOidc.WebAuth` for login, logout, consent. The workspace also contains a Blazor project (`MrWhoOidc.Web`), but the server UI will be Razor Pages.
- Target: .NET 9 across projects.

Milestones

M0 – Repository readiness
- [x] Verify project references: `MrWhoOidc.WebAuth` -> `MrWhoOidc.Auth`.
- [x] Decide token format for access tokens: JWT for API audience (temporary `api`).
- [x] Choose password hashing: Argon2id (Isopoh).
- [x] Define environment naming/connection: Aspire `authdb`.
- Deliverables:
  - [ ] ADR: token format choice and password hashing algorithm.
  - [ ] Checklist for security headers, CORS strategy (allow-list).

M1 – Infrastructure & Persistence
- [x] Add PostgreSQL in Aspire (`MrWhoOidc.AppHost`) with persistent volume.
- [x] Propagate connection string to `MrWhoOidc.WebAuth`.
- [x] Add EF Core (in `MrWhoOidc.Auth`).
- [x] Create `AuthDbContext` and initial entities: `User`, `Client`, `SigningKey`, `AuthorizationCode`, `Consent`, `Token` (refresh).
- [x] Migrations and database initialization (auto-migrate on startup).
- [x] Seed script for one test client and one test user.
- [x] Persist ASP.NET Core DataProtection keys to DB (antiforgery survives restarts).

M2 – Crypto & Discovery
- [x] Key management service: RSA keypair persisted as JWK, `kid`, `alg`.
- [x] JWKS endpoint with cache headers.
- [x] Discovery endpoint with configurable issuer.

M3 – User auth & Authorization Endpoint (Code Flow + PKCE)
- [x] Razor Pages: `Login` and `Consent` (with basic flow).
- [x] `/authorize` GET: validation, login requirement, consent enforcement, code issuance + state.

M4 – Token Endpoint & ID/Access/Refresh Tokens
- [x] `/token` grant `authorization_code` + PKCE verifier validation.
- [x] ID token (nonce, auth_time, at_hash + optional profile/email), access token (JWT + scope claim); refresh token issuance + rotation.
- [x] Refresh token grant implemented.

M5 – UserInfo, Logout/End Session
- [x] `/userinfo`: validates bearer token, returns claims; sends `invalid_token` on failures; short private cache headers.
- [x] Logout: local `/logout` and RP-initiated `/connect/endsession` with allow-listed `post_logout_redirect_uri`.

M6 – Introspection & Revocation (optional for MVP)
- [ ] `/introspect` for confidential clients (when opaque access tokens are enabled).
- [x] `/revoke` for refresh tokens.

M7 – Key Rotation & Hardening
- [ ] Automated signing key rotation + JWKS publishing overlap.
- [x] Rate limiting middleware:
  - [x] `/authorize` (60/min/IP)
  - [x] `/token` (30/min/IP)
  - [x] `/userinfo` (120/min/IP)
- [x] Anti-forgery tokens on login and consent forms.
- [x] Basic lockout/backoff on login (in-memory, per IP+username).
- [~] RFC-aligned error responses for `/token`, `/authorize` (when not redirecting), `/userinfo` (`invalid_token`).
- [ ] CORS allow-list for `/token` and `/userinfo` (if cross-origin).

M8 – Observability, DX & Docs
- [ ] Logging & tracing via `MrWhoOidc.ServiceDefaults`/OpenTelemetry.
- [ ] Metrics: request counts, failures, latency, token issuance, login failures.
- [ ] Dev UX: `dotnet run -- seed` or hosted seeder; Postman collection; sample `.http`.
- [ ] Documentation in `/docs` (setup, endpoints, examples, ADRs).

Next steps (proposed)
1) Key rotation and configuration
   - Implement key rotation service with overlap; expose previous keys in JWKS until tokens expire.
   - Add config for API audience(s) instead of hardcoded `api`; include in discovery.
2) Client authentication & revocation
   - Add client authentication for `/token` and `/revoke` (client_secret_basic; later private_key_jwt).
   - Audit revocation events (user/client/time, IP) and add idempotency.
3) Claims and scopes fidelity
   - Enforce scope filtering in `/userinfo` (and ID token) strictly based on requested/granted scopes.
   - Persist per-scope consent and honor deltas when scope changes.
4) Security hardening
   - Add CORS allow-list if cross-origin access to `/token`/`/userinfo` is required.
   - Add `WWW-Authenticate` header on `invalid_token` responses per RFC 6750.
   - Move rate limiting keys to a distributed store (for scale-out) and tune limits.
5) Observability & DX
   - Wire OpenTelemetry (traces, logs) and basic metrics (authorize/token/userinfo counts and latencies).
   - Add ADRs for token format and password hashing; document endpoints and examples; add Postman/.http samples.
6) Optional: Introspection/opaque tokens
   - Add opaque access token mode and `/introspect` for confidential clients; return RFC 7662-compliant responses.
