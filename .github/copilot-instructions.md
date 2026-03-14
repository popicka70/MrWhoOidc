# MrWhoOidc – AI Assistant Instructions

Purpose
- Make AI coding agents productive quickly in this codebase by capturing architecture, conventions, and workflows actually used here.

Tech stack & solution layout
- .NET 10, C#, MSTest.
- Projects:
  - MrWhoOidc.Auth: core OIDC domain (protocols, persistence, crypto, key mgmt, services). EF Core + PostgreSQL via Aspire–provided connection "authdb".
  - MrWhoOidc.WebAuth: OP (authorization server) HTTP surface (minimal APIs + Razor Pages), discovery, JWKS, admin UI.
  - MrWhoOidc.ApiService: sample downstream API used by E2E tests (incl. DPoP support).
  - MrWhoOidc.ServiceDefaults: logging/OpenTelemetry defaults.
  - MrWhoOidc.Security: cross-cutting security helpers (e.g., DPoP).
  - MrWhoOidc.AppHost: Aspire host wiring for local dev.
  - MrWhoOidc.UnitTests: unit/integration tests.

Core architectural rules (enforced by repo)
- Do NOT add or depend on OpenIddict or Microsoft Identity Platform packages.
- Place non-visual OIDC logic in MrWhoOidc.Auth. Keep HTTP endpoints, Razor UI, and discovery/JWKS in MrWhoOidc.WebAuth.
- Target .NET 10 across all projects.
- PostgreSQL via Aspire; never hardcode connection strings. Use named connection "authdb".

Key endpoints and flows
- Discovery and JWKS: implemented in MrWhoOidc.WebAuth (see `Handlers/DiscoveryHandler.cs`, `/jwks`).
- Authorization/OpenID flows: authorize/token/userinfo/logout implemented via minimal APIs + Razor Pages.
- Back-Channel Logout (BCL):
  - OP emits logout_token to RP backchannel URIs using a durable outbox + background dispatcher with retries/circuit breaker (`WebAuth/Background/BackchannelLogoutDispatcher.cs`).
  - Token built in `WebAuth/Handlers/LogoutHandler.cs` with required claims and `typ=logout+jwt`.
  - Admin UI/API surface client backchannel fields; audit logging implemented.
  - RP sample receiver lives in `MrWhoOidc.Web` with cookie revocation hook; strict JWKS validation and jti replay cache are TODOs.

Persistence & migrations
- EF Core migrations live in `MrWhoOidc.Auth/Persistence/Migrations`.
- Commands:
  - Add migration:
    - `dotnet ef migrations add <Name> --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations`
  - Update DB:
    - `dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth`

Build, run, and tests
- Build: `dotnet build` from repo root or use VS Code tasks (e.g., build-* tasks in workspace).
- Tests: `dotnet test` or VS Code tasks like:
  - test-mrwhooidc, test-obo-policy-extensions, build-and-test-obo-dpop-depth, etc. Prefer running from workspace tasks.
- Unit tests focus areas include token generation/validation, client store, consent, key rotation, PAR, token exchange, DPoP.

Security conventions
- Passwords/secrets: Argon2id or BCrypt; never store plaintext.
- Client secrets: Support multiple active secrets per client (up to 3) for zero-downtime rotation; secrets have expiry dates (default 90 days); multi-secret validation flow in ClientStore.
- Protocol validation: validate all OIDC/OAuth params; emit RFC-compliant error payloads.
- Signing keys: strong key mgmt with rotation; include `kid`.
- Backchannel auditing: structured logs with PII hashing; never log raw JWTs.

Observability
- Use `MrWhoOidc.ServiceDefaults` for logging and OpenTelemetry setup.
- BCL metrics/logging via `OidcMetrics` in dispatcher; admin and health endpoints exist.
- Client secret metrics via `IClientSecretMetrics`: authentication success/failure, expiry warnings, rotation events; expiry monitor runs daily; health endpoint at `/health/client-secrets`.

Project-specific patterns
- Minimal APIs for protocol endpoints inside MrWhoOidc.WebAuth `Program.cs` and handler classes under `Handlers/*`.
- Durable outbox pattern for BCL fan-out (AuthDbContext entity + background worker) with admin/health endpoints.
- Feature flags under appsettings (e.g., BackchannelFeatureOptions.Enabled; dev overrides for HTTP backchannel URIs).
- In tests, prefer using existing test helpers and seeds in `MrWhoOidc.UnitTests` (e.g., `TestDataSeeder.cs`).

E2E browser tests (e2e/)
- Location: `e2e/` directory at repo root — separate Python project, not part of the .NET solution.
- Stack: Python 3, pytest, Playwright (sync API), Ollama LLM for screenshot evaluation.
- Purpose: visual regression + functional smoke coverage of the entire MrWhoOidc.WebAuth UI — every Razor Page that can be reached with a logged-in admin is exercised. Tests capture full-page screenshots, send them to an LLM, and generate a scored HTML report.
- Architecture:
  - `conftest.py` — all fixtures. One browser + one authenticated BrowserContext shared for the whole session (fast). Login done once at session start, auth state saved to `.auth/state.json`. Each test gets a new Page (tab) via `authenticated_page` fixture; unauthenticated tests get a fresh context via `page` fixture.
  - `utils/screenshot_manager.py` — sync `capture(page, route)` saves PNGs to `screenshots/{run_id}/`.
  - `utils/llm_evaluator.py` — calls Ollama `/api/chat` (model configured via `OLLAMA_MODEL`). Cloud-proxied models (name ends in `-cloud`) use text-only evaluation; local Ollama models support vision.
  - `utils/report_generator.py` — accumulates `EvaluationResult` objects; writes `reports/{run_id}/report.{json,html}` at session end.
  - `utils/instruction_loader.py` — loads per-route evaluation hints from `instructions/` directory.
- Test files and coverage:
  - `tests/test_public_pages.py` — unauthenticated pages: `/`, `/login`, `/Privacy`, `/Account/ForgotPassword`, `/select-tenant`, 404, OIDC discovery, JWKS.
  - `tests/test_account_pages.py` — self-service account pages: `/account`, `/account/profile`, `/account/emails`, `/account/web-authn`, `/account/sessions`, `/account/consents`, `/account/linked-accounts`, `/account/create-tenant`, `/account/access-denied`, `/password`, `/mfa`.
  - `tests/test_admin_pages.py` — all tenant-admin pages including list, add, import, and edit variants for realms, clients, providers, scopes, roles, users (+ user sub-tabs: clients, emails, roles, linked), plus registrations, configuration-audit, backchannel, obo-setup, all license pages, branding, settings, rate-limits.
  - `tests/test_platform_admin_pages.py` — platform-admin pages: dashboard, tenant list/create/edit/import, impersonation, impersonation history, platform settings, platform license.
  - `tests/test_crud_operations.py` — create→edit flows for realm, client, scope, role, user, account profile, and tenant. Records use `e2e-crud` prefix for easy cleanup. Test order within each class is significant (01_create before 02_edit).
- Running:
  - Requires the app running at `https://localhost:8443` (e.g., via `docker-compose.dev.yml`).
  - Copy `.env.example` to `.env`, set `ADMIN_USERNAME`, `ADMIN_PASSWORD`, `OLLAMA_MODEL`, `HEADED`.
  - `cd e2e && python3 -m pytest -v` — runs all ~109 tests; report written to `e2e/reports/{timestamp}/report.html`.
  - Single test: `python3 -m pytest tests/test_admin_pages.py::TestAdminClients::test_client_list_loads -v`.
- Conventions when adding new e2e tests:
  - Use `authenticated_page` fixture for any page requiring login; `page` for public pages.
  - Always `goto(path, wait_until="domcontentloaded")` — never `networkidle` (admin pages have WebSocket/SSE connections that never idle).
  - Wrap navigation in `_goto_admin()` / `_goto_platform()` helpers so tests skip gracefully on redirects or download-triggering endpoints rather than hard-failing.
  - Call `record_evaluation(page, route)` after navigating to capture the screenshot and LLM score.
  - LLM scores are informational (7/10 is typical for a functional but plain admin UI); only scores below `min_score` (default 5) cause test failure.
  - New page instruction hints can be added to `e2e/instructions/` as `<route-slug>.md` files.

When adding features
- Keep core protocol/business logic in MrWhoOidc.Auth; expose via WebAuth minimal APIs.
- Add/adjust migrations via the commands above; do not hand-edit DB schema.
- Update docs under /docs when changing protocols/endpoints (e.g., backchannel backlog, OBO policy).
- Add unit tests beside similar existing tests in MrWhoOidc.UnitTests.
- When adding new Razor Pages to MrWhoOidc.WebAuth, add a corresponding test in the appropriate e2e test file.

File breadcrumbs worth reading first
- `MrWhoOidc.WebAuth/Program.cs` – routing, admin groups, health endpoints.
- `MrWhoOidc.WebAuth/Handlers/*` – discovery, logout token creation.
- `MrWhoOidc.WebAuth/Background/BackchannelLogoutDispatcher.cs` – durable outbox dispatcher.
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` – entities including backchannel outbox.
- `MrWhoOidc.UnitTests/*` – examples covering token, client, consent, key rotation, TE, DPoP.

Caveats
- Multi-tenant, mTLS for backchannel, and RP strict validation are partially implemented/TODO—consult `docs/backchannel-logout-backlog.md`.
