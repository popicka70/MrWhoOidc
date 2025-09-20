MrWhoOidc OIDC Server Backlog

Status summary (MVP scope)
- [x] M1 Infrastructure & Persistence (core entities, migrations, Aspire Postgres, persistent DataProtection keys)
- [x] M2 Crypto & Discovery (key store, JWKS w/ cache headers, discovery w/ configurable issuer)
- [x] M3 User auth & Authorization (login, consent, /authorize with Code + PKCE)
- [x] M4 Token endpoint (authorization_code, ID/Access, Refresh with rotation; configurable API audiences)
- [x] M5 UserInfo + Logout (sub-based userinfo, local + RP-initiated logout)
- [~] M6 Introspection & Revocation (revocation implemented with client auth, idempotency + audit; introspection pending)
- [x] M7 Key rotation & hardening (automated key rotation + JWKS overlap, rate limiting, antiforgery, login backoff, WWW-Authenticate, CORS allow-list)
- [~] M8 Observability, DX & Docs (base OpenTelemetry wired; custom meters added; exporter wiring/docs pending)

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
- [x] Create `AuthDbContext` and entities: `User`, `Client`, `SigningKey`, `AuthorizationCode`, `Consent`, `Token` (refresh), `DataProtectionKey`.
- [x] Migrations and database initialization (auto-migrate on startup).
- [x] Seed script for one test client and one test user (now includes profile/email).
- [x] Persist ASP.NET Core DataProtection keys to DB (antiforgery survives restarts).

M2 – Crypto & Discovery
- [x] RSA key management with persisted JWKs, `kid`, `alg`.
- [x] JWKS endpoint with cache headers.
- [x] Discovery endpoint with configurable issuer (+ token_endpoint_auth_methods_supported, revocation_endpoint; publishes configured audiences as non-standard field).

M3 – User auth & Authorization Endpoint (Code Flow + PKCE)
- [x] Razor Pages: `Login` (with antiforgery + basic lockout/backoff) and `Consent`.
- [x] `/authorize` GET: validation, login requirement, consent enforcement (per-scope), code issuance + state.

M4 – Token Endpoint & ID/Access/Refresh Tokens
- [x] `/token` grant `authorization_code` + PKCE verifier validation.
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
- [x] CORS allow-list for `/token` and `/userinfo` (config-driven).

M8 – Observability, DX & Docs
- [x] Logging & tracing via `MrWhoOidc.ServiceDefaults`/OpenTelemetry (base wiring).
- [~] Metrics: basic custom meters for authorize/token/userinfo/revoke (counts, success/failure, latency); exporter registration pending.
- [ ] Dev UX: `dotnet run -- seed` or hosted seeder; Postman collection; sample `.http`.
- [ ] Documentation in `/docs` (setup, endpoints, examples, ADRs).

Next steps (proposed)
1) Observability & Metrics
   - Register custom meter with OpenTelemetry (e.g., AddMeter("MrWhoOidc.WebAuth")) and export (OTLP/Prometheus).
   - Add useful metric tags (e.g., grant_type, outcome) and dashboards.

2) Security hardening
   - Enable HTTPS redirection/HSTS appropriately behind Aspire/reverse proxy (respect forwarded headers).
   - Move rate limiting counters to a distributed store (e.g., Redis) for scale-out.
   - Tighten CORS (limit methods/headers to what's required).

3) Protocol fidelity improvements
   - Persist and include accurate `auth_time` from login session in ID tokens.
   - Consider `private_key_jwt` client authentication for `/token` and `/revoke`.
   - Document/remove non-standard `audiences` in discovery or adopt RFC 8707 resource indicators.

4) Dev experience & Docs
   - ADRs: token format and password hashing.
   - Postman collection and `.http` samples; endpoint and flow documentation.
   - Optional: `dotnet run -- seed` command or hosted seeder mode for local dev.

5) Optional: Opaque access tokens & Introspection
   - Add opaque access token mode and `/introspect` for confidential clients; return RFC 7662-compliant responses.
