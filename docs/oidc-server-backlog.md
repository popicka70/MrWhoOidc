MrWhoOidc OIDC Server Backlog

Overview
- Goal: Implement an OpenID Connect (OIDC) Authorization Server where `MrWhoOidc.WebAuth` hosts the endpoints and UI (login/logout/consent) and `MrWhoOidc.Auth` contains the non-visual logic, protocols, persistence, and crypto.
- Constraints: Do not use OpenIddict or Microsoft identity platforms (e.g., Azure AD). Use our own implementation with standard .NET libraries where needed.
- Persistence: PostgreSQL managed by Aspire in `MrWhoOidc.AppHost` and consumed by `MrWhoOidc.WebAuth`/`MrWhoOidc.Auth`.
- UI Tech: Razor Pages in `MrWhoOidc.WebAuth` for login, logout, consent. The workspace also contains a Blazor project (`MrWhoOidc.Web`), but the server UI will be Razor Pages.
- Target: .NET 9 across projects.

Non-goals (initially)
- Dynamic client registration (RFC 7591/7592)
- FAPI, PAR, JAR/JARM, DPoP (may be added later)
- Federation/openid-federation

High-level Architecture
- `MrWhoOidc.Auth` (Class Library)
  - Protocol: OIDC/OAuth2 endpoint logic (business rules, not HTTP transport), parameter validation, error model.
  - Services: token issuance, authorization code handling, consent, user management, client store, key management, claims mapping.
  - Crypto: key generation, JWT signing, JWKS publishing, key rotation.
  - Persistence: EF Core + Npgsql entities and DbContext.
  - Contracts: interfaces and DTOs separating protocol from transport.
- `MrWhoOidc.WebAuth` (Razor Pages + Minimal APIs)
  - Endpoints: `/.well-known/openid-configuration`, `/jwks`, `/authorize`, `/token`, `/userinfo`, `/logout`, `/introspect` (optional), `/revoke` (optional).
  - UI: login, consent, logout pages.
  - Hosting: DI, auth cookie scheme, anti-forgery, CORS, model binding, pipeline hardening.
- `MrWhoOidc.AppHost` (Aspire)
  - Postgres resource + wiring of connection string to `MrWhoOidc.WebAuth`.
  - Health, logging, metrics wiring via `MrWhoOidc.ServiceDefaults`.

Milestones

M0 – Repository readiness
- Verify project references: `MrWhoOidc.WebAuth` -> `MrWhoOidc.Auth`.
- Decide token format for access tokens: JWT for `MrWhoOidc.ApiService` audience.
- Choose password hashing: Argon2id or BCrypt (prefer Argon2 if available) and policy for iterations.
- Define environment naming: `AuthDb` connection.
- Deliverables:
  - ADR: token format choice and password hashing algorithm.
  - Checklist for security headers, CORS strategy (allow-list).

M1 – Infrastructure & Persistence
- Add PostgreSQL in Aspire (`MrWhoOidc.AppHost`):
  - Define Postgres resource, database `authdb`.
  - Propagate connection string to `MrWhoOidc.WebAuth` via Aspire connection wiring.
- Add EF Core (in `MrWhoOidc.Auth`):
  - Packages: `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design`.
  - Create `AuthDbContext` and entity models.
  - Migrations and database initialization.
- Entities (initial set):
  - `User`, `UserCredential` (password hash, salt, algorithm), `UserClaim`.
  - `Client`, `ClientSecret` (hashed), `RedirectUri`, `ClientGrantType`, `ClientScope`.
  - `Scope`.
  - `AuthorizationCode` (includes: code, client_id, user_id, nonce, pkce_verifier_hash, redirect_uri, scopes, expires_at).
  - `Token` (discriminator: access/refresh; includes claims snapshot, audience, expires_at, rotation info).
  - `SigningKey` (current/previous keys, jwk json, kid, alg, created_at, expires_at/retired_at).
  - `Consent` (user_id, client_id, scopes, created_at, revoked_at).
  - `UserSession` (for logout / session mgmt).
  - `Nonce` (optional if stored inside `AuthorizationCode`).
- Deliverables / Acceptance
  - `MrWhoOidc.AppHost` starts a Postgres container.
  - `MrWhoOidc.WebAuth` applies migrations on startup (or via CLI) and passes DB health checks.
  - Seed script for one test client and one test user.

M2 – Crypto & Discovery
- Implement key management service in `MrWhoOidc.Auth`:
  - Generate RSA (or EC) keys, persist as JWK, track `kid`, algorithm.
  - Provide active signing key and JWKS projection.
- Endpoints in `MrWhoOidc.WebAuth` (minimal APIs):
  - `/.well-known/openid-configuration` (discovery)
  - `/jwks` (JWKS)
- Deliverables / Acceptance
  - Discovery returns valid metadata for configured issuer.
  - JWKS returns current signing keys; caches and ETag.

M3 – User auth & Authorization Endpoint (Code Flow + PKCE)
- Razor Pages in `MrWhoOidc.WebAuth`:
  - `Login` page with anti-forgery, CSRF protection, lockout policy.
  - `Consent` page, honoring client `require_consent` flag.
- Protocol services in `MrWhoOidc.Auth`:
  - Request validation for `/authorize`: client_id, redirect_uri, response_type=code, scope, state, nonce, code_challenge (+method), prompt.
  - State/nonce generation and validation strategy.
  - Authorization code issuance and persistence.
- Endpoint in `MrWhoOidc.WebAuth`:
  - `/authorize` GET: authenticate user, request consent, create code, redirect with `code` and `state`.
- Deliverables / Acceptance
  - Happy-path for Code + PKCE with seeded client and user.
  - CSRF-safe login and consent.

M4 – Token Endpoint & ID/Access/Refresh Tokens
- Protocol services in `MrWhoOidc.Auth`:
  - `/token` grant `authorization_code` + `code_verifier` validation.
  - ID token creation (claims: iss, sub, aud, iat, exp, nonce, auth_time; optionally `at_hash`/`c_hash`).
  - Access token (JWT) issuance with audience and scopes; refresh token issuance + rotation policy.
  - Token lifetime and clock skew handling.
- Endpoint in `MrWhoOidc.WebAuth`:
  - `/token` POST: returns `id_token`, `access_token`, `refresh_token`, `expires_in`, `token_type`.
- Deliverables / Acceptance
  - End-to-end auth code flow: `/authorize` -> `/token`.
  - Token validation succeeds for `MrWhoOidc.ApiService` with issuer/audience check.

M5 – UserInfo, Logout/End Session
- `/userinfo` endpoint: bearer access token validation and claims projection.
- Logout UI and endpoint:
  - Local logout clears auth cookie; optional OIDC RP-initiated logout support.
  - `end_session` endpoint and `post_logout_redirect_uri` validation.
- Deliverables / Acceptance
  - UserInfo returns claims per scopes.
  - Logout flow completes and redirects when valid.

M6 – Introspection & Revocation (optional for MVP)
- `/introspect` and `/revoke` endpoints for confidential clients.
- Opaque access tokens support (toggle) and introspection response.
- Deliverables / Acceptance
  - Valid introspection for active/expired tokens.
  - Revocation invalidates refresh tokens and associated access tokens.

M7 – Key Rotation & Hardening
- Automated signing key rotation + JWKS publishing of previous keys until tokens expire.
- Security:
  - Rate limiting (login, token, introspection). 
  - Robust error responses per RFC 6749/6750/8414.
  - Cookie policy: `Secure`, `HttpOnly`, `SameSite=Lax` for auth cookies; anti-forgery on forms.
  - CORS allow-list for `/token` (if needed) and `/userinfo`.
- Deliverables / Acceptance
  - Rotation procedure documented and tested.
  - Basic hardening checks pass (ZAP baseline, etc.).

M8 – Observability, DX & Docs
- Logging & tracing via `MrWhoOidc.ServiceDefaults`/OpenTelemetry.
- Metrics: request counts, failures, latency, token issuance counts, login failures.
- Dev UX:
  - Seeders for test data (`dotnet run -- seed`) or hosted service.
  - Postman collection and sample `.http` files.
  - Sample client in `MrWhoOidc.Web` or `MrWhoOidc.ApiService` for validation.
- Documentation in `/docs` for setup, endpoints, and examples.

Cross-cutting Tasks
- Validation layer (prefer FluentValidation or custom validators for protocol requests).
- Time abstraction (`ITimeProvider`) for testability.
- Error contract alignment with RFCs.
- Strong typing for scopes and claims mapping.
- Threat model: CSRF, SSRF (none), XSS on pages, replay protection (PKCE), fixation, brute-force mitigation.

Project Structure & Key Interfaces (`MrWhoOidc.Auth`)
- Namespaces
  - `.Protocols` – DTOs, validation, error codes, constants.
  - `.Services` – `IClientStore`, `IUserService`, `IAuthorizationService`, `ITokenService`, `IKeyStore`, `IJwtService`, `IConsentService`.
  - `.Crypto` – key generation, signing, hashing.
  - `.Persistence` – EF Core DbContext, entities, configurations, repositories.
  - `.Util` – clock/time provider, random, URL building.
- Packages
  - `Npgsql.EntityFrameworkCore.PostgreSQL`
  - `Microsoft.EntityFrameworkCore.Design`
  - `System.IdentityModel.Tokens.Jwt` (and/or `Microsoft.IdentityModel.JsonWebTokens`)
  - `BCrypt.Net-Next` or `Isopoh.Cryptography.Argon2`
  - `FluentValidation` (optional)

Endpoints map (`MrWhoOidc.WebAuth`)
- Discovery: `GET /.well-known/openid-configuration`
- JWKS: `GET /jwks`
- Authorization: `GET /authorize`
- Token: `POST /token`
- UserInfo: `GET /userinfo`
- Logout/End session: `GET /logout`, `GET /connect/endsession` (exact paths tbd)
- Introspection: `POST /introspect` (optional)
- Revocation: `POST /revoke` (optional)

Acceptance Test Matrix (core)
- Code flow + PKCE: success path, invalid client, invalid redirect_uri, missing nonce, invalid code_verifier, expired code.
- Token: invalid code, replayed code, mismatched client, invalid grant type, missing PKCE, invalid client auth.
- UserInfo: missing token, expired token, insufficient scope.
- Logout: invalid `post_logout_redirect_uri`, session cleared.
- Discovery/JWKS: well-formed responses, cache headers.

Risks & Mitigations
- Building an OIDC server from scratch carries protocol compliance risk.
  - Mitigation: start minimal (Code + PKCE + ID Token), add conformance tests gradually.
- Key management mistakes can break clients.
  - Mitigation: stage rotation, long overlap, integration tests, backups.
- Security oversights in login/consent.
  - Mitigation: strict anti-forgery, headers, validation, rate limiting.

Initial Tasks (Next up)
- AppHost: add Postgres resource and wire connection to `MrWhoOidc.WebAuth`.
- Auth: add EF Core packages and create empty `AuthDbContext`.
- Auth: define entities for `Client`, `User`, `SigningKey` minimal set.
- WebAuth: add `/ .well-known/openid-configuration` and `/jwks` endpoint stubs returning placeholders.
- Docs: add ADR for token format and password hashing.
