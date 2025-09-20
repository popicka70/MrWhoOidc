MrWhoOidc OIDC Server Backlog

Status summary (MVP scope)
- [x] M1 Infrastructure & Persistence (partial: core entities in place, migrations wired, Aspire Postgres)
- [x] M2 Crypto & Discovery (JWKS, discovery with cache headers, configurable issuer)
- [x] M3 User auth & Authorization (login, consent, /authorize with Code + PKCE)
- [x] M4 Token endpoint (authorization_code, ID/Access, Refresh with rotation)
- [x] M5 UserInfo + Logout (basic sub-only userinfo, local + RP-initiated logout)
- [ ] M6 Introspection & Revocation (revocation implemented)
- [ ] M7 Key rotation & hardening
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
- Notes:
  - Always create schema changes with: dotnet ef migrations add <Name> --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations
  - Remaining entities (future): `ClientSecret`, `RedirectUri`, `Scope`, etc.

M2 – Crypto & Discovery
- [x] Key management service: RSA keypair persisted as JWK, `kid`, `alg`.
- [x] JWKS endpoint with cache headers.
- [x] Discovery endpoint with configurable issuer.

M3 – User auth & Authorization Endpoint (Code Flow + PKCE)
- [x] Razor Pages: `Login` and `Consent` (with basic flow).
- [x] `/authorize` GET: validation, login requirement, consent enforcement, code issuance + state.

M4 – Token Endpoint & ID/Access/Refresh Tokens
- [x] `/token` grant `authorization_code` + PKCE verifier validation.
- [x] ID token (minimal claims), access token (JWT) issuance; refresh token issuance + rotation.
- [x] Refresh token grant implemented.

M5 – UserInfo, Logout/End Session
- [x] `/userinfo`: validates bearer token, returns `sub`.
- [x] Logout: local `/logout` and RP-initiated `/connect/endsession` with allow-listed `post_logout_redirect_uri`.

M6 – Introspection & Revocation (optional for MVP)
- [ ] `/introspect` for confidential clients (when opaque access tokens are enabled).
- [x] `/revoke` for refresh/access tokens (refresh implemented).

M7 – Key Rotation & Hardening
- [ ] Automated signing key rotation + JWKS publishing overlap.
- [ ] Security hardening:
  - [ ] Rate limiting for login, token, introspection.
  - [ ] RFC-compliant error objects throughout.
  - [ ] Anti-forgery enforcement on forms (login/consent) and lockout policy.
  - [ ] CORS allow-list for `/token` and `/userinfo` (if cross-origin).

M8 – Observability, DX & Docs
- [ ] Logging & tracing via `MrWhoOidc.ServiceDefaults`/OpenTelemetry.
- [ ] Metrics: request counts, failures, latency, token issuance, login failures.
- [ ] Dev UX: `dotnet run -- seed` or hosted seeder; Postman collection; sample `.http`.
- [ ] Documentation in `/docs` (setup, endpoints, examples, ADRs).

Next steps (proposed)
1) Security & hardening
   - Add anti-forgery tokens to login/consent and implement lockout/backoff.
   - Add rate limiting on `/authorize`, `/token`, `/userinfo`.
   - RFC-aligned error bodies + consistent cache headers.
2) Rotation and revocation
   - Implement key rotation with overlap; publish previous keys.
   - Add `/revoke` client authentication (basic/private_key_jwt stub) and audit.
3) Claims and tokens
   - Map profile/email claims in access/ID tokens strictly by requested scopes.
   - Add `c_hash` if returning code in front-channel in future flows.
4) Observability and docs
   - Wire OpenTelemetry and metrics.
   - Add ADRs for token format and hashing; update docs and sample requests.
5) Introspection (optional)
   - Add opaque access token mode + `/introspect` for confidential clients.
