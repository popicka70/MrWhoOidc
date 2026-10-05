# MrWhoOidc – AI Assistant Instructions

Purpose
- Make AI coding agents productive quickly in this codebase by capturing architecture, conventions, and workflows actually used here.

Tech stack & solution layout
- .NET 10, C#, MSTest.
- Projects:
  - MrWhoOidc.Auth: core OIDC domain (protocols, persistence, crypto, key mgmt, services). EF Core + PostgreSQL via Aspire–provided connection "authdb".
  - MrWhoOidc.WebAuth: OP (authorization server) HTTP surface (minimal APIs + Razor Pages), discovery, JWKS, admin UI.
  - MrWhoOidc.ServiceDefaults: logging/OpenTelemetry defaults.
  - MrWhoOidc.Security: cross-cutting security helpers (e.g., DPoP).
  - MrWhoOidc.AppHost: Aspire host wiring for local dev.
  - MrWhoOidc.Cli: `mrwho-cli` admin tool (also exposes MCP tools).
  - MrWhoOidc.KeyGen: standalone key/license generator (WebAuth never generates client key pairs).
  - MrWhoOidc.UnitTests: unit/integration tests.
  - Examples/*: sample clients and APIs (RazorClient, TestApi, OidcDemo, ReactOidcClient, Go samples); MrWhoOidc.Web is the static marketing site.

Core architectural rules (enforced by repo)
- Do NOT add or depend on OpenIddict or Microsoft Identity Platform packages.
- Place non-visual OIDC logic in MrWhoOidc.Auth. Keep HTTP endpoints, Razor UI, and discovery/JWKS in MrWhoOidc.WebAuth.
- Target .NET 10 across all projects.
- PostgreSQL via Aspire; never hardcode connection strings. Use named connection "authdb".

Key endpoints and flows
- Discovery and JWKS: implemented in MrWhoOidc.WebAuth (see `Handlers/DiscoveryHandler.cs`, `/jwks`).
- Authorization/OpenID flows: authorize/token/userinfo/logout implemented via minimal APIs + Razor Pages.
- Logout (`WebAuth/Handlers/Logout/*`):
  - `LogoutTargetResolver` derives the subject only from the OP session user or a signature/issuer-verified `id_token_hint`; the `sid` query param is never used for targeting. Only RPs that received codes or hold live tokens for that user are notified, each with its own (pairwise-aware) `sub`.
  - Back-channel: `BackChannelLogoutEnqueuer` writes a durable outbox; `WebAuth/Background/BackchannelLogoutDispatcher.cs` delivers with retries/circuit breaker across all tenants. Logout tokens (`typ=logout+jwt`) are built by `Auth/Services/Token/LogoutTokenService.cs`.
- Client authentication & policy: no-credential auth only for public clients (`token_endpoint_auth_method=none`, or no credential material); `/token` enforces registered `token_endpoint_auth_method` and `grant_types`; failures return `invalid_client` (401 for Basic). Introspection is deny-by-default. PAR `request_uri` is single-use and client-bound; `RequirePar` (global, list, `Client.RequirePar`) is enforced. `resource` is limited to `Auth:ApiAudiences` + `Client.M2MAllowedAudiencesJson`. Encrypted ID token/JARM fail closed.
- Background jobs (BCL dispatch, token/PAR/support-access/QR cleanup, key rotation) must cover every tenant: use `IgnoreQueryFilters()` or `BackgroundServiceTenantHelper.ForEachActiveTenantAsync`; never pin a job to the default tenant.
- Authorization-code login context (`sid`, upstream idp/acr/amr, mapped claims) is persisted on the `AuthorizationCode` row; do not reintroduce per-process state that must survive between `/authorize` and `/token`.
- Private signing JWKs must not enter the distributed (Redis) cache tier; protect them before first save.

Persistence & migrations
- EF Core migrations live in `MrWhoOidc.Auth/Persistence/Migrations`.
- Use `GuidHelper.NewId()` for entity primary keys, following `MrWhoOidc.Auth/Persistence/GuidHelper.cs` (UUIDv7). Do not use time-ordered entity identifiers as secret or recovery tokens.
- Commands:
  - Add migration:
    - `dotnet ef migrations add <Name> --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations`
  - Update DB:
    - `dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth`

Build, run, and tests
- Build: `dotnet build MrWhoOidc.slnx` from repo root (or VS Code `build-*` tasks).
- Tests: `dotnet test` (or VS Code tasks such as `test-mrwhooidc`).
- Unit tests focus areas include token generation/validation, client store, consent, key rotation, PAR, token exchange, DPoP.
- Do not introduce compiler or analyzer warnings. Report pre-existing warnings separately rather than expanding unrelated changes to satisfy an old blanket zero-warning claim.

Security conventions
- Passwords/secrets: Argon2id (`Auth/Services/PasswordHasher.cs`); never store plaintext. Password updates go through `UserAccountService.UpdatePasswordAsync`, which rotates the security stamp and revokes the account's tokens.
- Client secrets: use the multi-secret validation flow in ClientStore. The admin creation path limits active secrets to 3 and supports expiry, but do not infer universal limits, expiry, or zero-downtime guarantees across every writer. See `docs/for-operators/client-secret-rotation.md` for the verified lifecycle and last-secret revocation guard.
- Protocol validation: validate all OIDC/OAuth params; emit RFC-compliant error payloads.
- Signing keys: strong key mgmt with rotation; include `kid`.
- Backchannel auditing: structured logs with PII hashing; never log raw JWTs.

Observability
- Use `MrWhoOidc.ServiceDefaults` for logging and OpenTelemetry setup.
- BCL metrics/logging via `OidcMetrics` in dispatcher; admin and health endpoints exist.
- Client secret metrics via `IClientSecretMetrics`: authentication success/failure, expiry warnings, rotation events; expiry monitor runs daily; health endpoint at `/health/client-secrets`.

Project-specific patterns
- Minimal APIs for protocol endpoints mapped in `WebAuth/Infrastructure/EndpointMapping/*` with handler classes under `Handlers/*`.
- Durable outbox pattern for BCL fan-out (AuthDbContext entity + background worker) with admin/health endpoints.
- Feature flags under appsettings (e.g., `Backchannel` section → `BackchannelFeatureOptions`; `Auth:Enable*` flags in `AuthOptions`).
- In tests, prefer using existing test helpers and seeds in `MrWhoOidc.UnitTests` (e.g., `TestDataSeeder.cs`).

E2E browser tests (e2e/)
- Location: `e2e/` directory at repo root — separate Python project, not part of the .NET solution.
- Stack: Python 3.13+, pytest, Playwright (sync API), LLM for screenshot evaluation (OpenAI or Ollama).
- Purpose: visual regression + functional smoke coverage of the entire MrWhoOidc.WebAuth UI — every Razor Page reachable by a logged-in admin is exercised. Tests capture full-page screenshots, send them to an LLM, and produce a scored HTML report.

E2E environment setup (canonical — do NOT deviate)
- Virtual environment: always `e2e/.venv`. Do NOT create venvs anywhere else (not repo root `.venv`, not `.venv-1`, etc.).
- One-time setup:
  ```bash
  cd e2e
  sh ./setup-venv.sh                         # creates .venv, installs requirements.txt + pip
  .venv/bin/python -m pip install cryptography PyJWT   # extra deps for OIDC-flow tests
  .venv/bin/playwright install chromium       # install browser binary
  cp .env.example .env                        # then edit .env (see below)
  ```
- The correct interpreter is always `e2e/.venv/bin/python`. Activate with `source e2e/.venv/bin/activate` or call directly.
- Installing packages: use `.venv/bin/python -m pip install <pkg>` (the venv may not have a standalone `pip` binary).
- Pinned packages in `requirements.txt` (pytest, playwright, openai, requests, jinja2, pillow, etc.) plus additional unlisted runtime deps: `cryptography>=46`, `PyJWT>=2.12`.
- Python version: 3.13+ (system python3 on the dev machine).

E2E configuration (.env)
- Copy `.env.example` → `.env` and set at minimum:
  | Variable | Required | Default | Notes |
  |---|---|---|---|
  | `BASE_URL` | yes | `https://localhost:8443` | Running WebAuth instance |
  | `ADMIN_USERNAME` | yes | `admin@mrwho.local` | Seeded admin. Use `admin` if default tenant changed |
  | `ADMIN_PASSWORD` | yes | `Admin123!` | Seeded admin password |
  | `LLM_BACKEND` | no | `openai` | `openai` or `ollama` |
  | `OPENAI_API_KEY` | if openai | — | Skip LLM eval if absent |
  | `OPENAI_MODEL` | no | `gpt-4o` | OpenAI model for vision eval |
  | `OLLAMA_HOST` | if ollama | `http://localhost:11434` | Local Ollama server |
  | `OLLAMA_MODEL` | if ollama | `qwen3.5:397b-cloud` | Model name |
  | `HEADED` | no | `false` | `true` to watch browser |
  | `SLOW_MO` | no | `0` | ms delay between actions |
  | `EXAMPLE_RAZORCLIENT_URL` | no | `https://localhost:5003` | Example app |
  | `EXAMPLE_TESTAPI_URL` | no | `https://localhost:7149` | Example API |

Running E2E tests
- Prerequisites: app running at `BASE_URL` (via `docker-compose -f docker-compose.dev.yml up -d`), `mrwho-cli` installed (for CLI and OIDC-flow tests).
- Run all: `cd e2e && .venv/bin/python -m pytest -v`
- Run subset: `.venv/bin/python -m pytest tests/test_admin_pages.py -v`
- Single test: `.venv/bin/python -m pytest tests/test_admin_pages.py::TestAdminClients::test_client_list_loads -v`
- Reports written to `e2e/reports/{timestamp}/report.html`; screenshots to `e2e/screenshots/{timestamp}/`.
- Roughly 500 tests across ~30 files under `e2e/tests/`.

E2E architecture
  - `conftest.py` — all fixtures. One browser + one authenticated BrowserContext shared for session. Login once at start, auth state saved to `.auth/state.json`. `authenticated_page` fixture gives a new tab; `page` gives an unauthenticated context.
  - `utils/screenshot_manager.py` — sync `capture(page, route)` saves PNGs per run. Waits for CSS transitions/animations to finish via `getAnimations()` before capturing.
  - `utils/llm_evaluator.py` — calls OpenAI or Ollama for screenshot scoring. Cloud models use text-only eval; local Ollama models support vision.
  - `utils/report_generator.py` — accumulates `EvaluationResult` objects; writes JSON + HTML reports.
  - `utils/instruction_loader.py` — per-route evaluation hints from `instructions/` directory.
  - `utils/cli_helper.py` — `CliHelper` wrapping `mrwho-cli` subprocess calls. Session fixture `cli_logged_in` handles enable → login → approve flow.
  - `utils/oidc_client.py` — HTTP-level OIDC client for protocol-flow tests (auth code, client_credentials, token exchange, DPoP).
  - `utils/dpop.py` — DPoP proof builder using `cryptography` + `PyJWT`.

E2E test files (main ones; protocol-specific files such as `test_ciba.py`, `test_device_flow.py`, `test_logout_flows.py`, `test_response_modes.py`, `test_mtls.py`, `test_dynamic_registration.py` also exist)
  - `test_public_pages.py` — unauthenticated: `/`, `/login`, `/Privacy`, `/Account/ForgotPassword`, `/select-tenant`, 404, discovery, JWKS.
  - `test_account_pages.py` — self-service: `/account`, profile, emails, webauthn, sessions, consents, linked-accounts, create-tenant, access-denied, password, mfa.
  - `test_admin_pages.py` — tenant-admin: realms, clients, providers (+claim-mappings, keys), scopes, roles, users (+sub-tabs), registrations, config-audit, backchannel, obo-setup, license (all variants), branding, settings, rate-limits.
  - `test_platform_admin_pages.py` — platform-admin: dashboard, tenants CRUD/import, impersonation (+history), settings, license.
  - `test_crud_operations.py` — create→edit UI flows (realm, client, scope, role, user, profile, tenant). `e2e-crud` prefix; ordered tests.
  - `test_cli_operations.py` — CLI E2E: read-only commands, profile management (rename, validation, server header), CRUD lifecycle, M2M, OBO provisioning, full workflow + export. `e2e-cli` prefix.
  - `test_oidc_flows.py` — protocol-level: auth-code+PKCE, client_credentials, token exchange, DPoP binding, negative cases. `e2e-oidc` prefix.
  - `test_example_apps.py` — exercises dockerized example apps (RazorClient, OidcDemo, ReactClient).

E2E conventions for new tests
  - Use `authenticated_page` fixture for any page requiring login; `page` for public pages.
  - Always `goto(path, wait_until="domcontentloaded")` — never `networkidle` (admin pages have WebSocket/SSE that never idle).
  - Wrap navigation in `_goto_admin()` / `_goto_platform()` helpers so tests skip gracefully on redirects/downloads.
  - Call `record_evaluation(page, route)` after navigating to capture screenshot + LLM score.
  - LLM scores: only scores below `min_score` (default 5) cause test failure. Target 7+ for well-designed pages.
  - Per-route instruction hints: add `e2e/instructions/<route-slug>.md` files.

When adding features
- Keep core protocol/business logic in MrWhoOidc.Auth; expose via WebAuth minimal APIs.
- Add/adjust migrations via the commands above; do not hand-edit DB schema.
- Update docs under /docs when changing protocols/endpoints (e.g., backchannel backlog, OBO policy).
- Consult `wiki/index.md` for the generated project architecture map when answering structural questions, but treat code and curated docs as the source of truth.
- Keep `wiki/` in sync for structural changes by updating relevant pages and appending to `wiki/log.md`; use `.github/prompts/index.prompt.md` when a broader refresh is needed.
- Add unit tests beside similar existing tests in MrWhoOidc.UnitTests.
- When adding new Razor Pages to MrWhoOidc.WebAuth, add a corresponding test in the appropriate e2e test file.

CLI administration (mrwho-cli)
- `mrwho-cli` is a globally-installed .NET tool for managing the IdP from the command line.
- Source: `MrWhoOidc.Cli/`; install via `bash deploy-mrwho-cli.sh` from the repo root.
- Full command reference and usage patterns: `skills/mrwho-cli.md` — **always read this file before generating or describing any `mrwho-cli` command.**
- Tool help is comprehensive; use `mrwho-cli <command> --help` for details on any command, its parameters, and examples. Or use `mrwho-cli --help` for a full list of commands and global options.
- Typical operations: `mrwho-cli login`, `tenant list`, `realm create`, `client create --create-initial-secret`, `export tenant`, `import apply`.
- Authentication: device-code flow (`mrwho-cli login --server https://host/t/<slug>`); tokens saved in named profiles.
- Multi-profile: each login creates/updates a named profile. Use `profile switch <name>` to change context. `profile rename` to relabel. `--profile` on login to name it at creation time.
- Server context: every authenticated command writes `Server: <url>  (profile: <name>)` to stderr so you always know the target. Does not affect JSON stdout.
- Profile names: codename format (alphanumeric + hyphens, e.g. `my-prod`) or the profile's exact server URL. No spaces or special characters.
- Output formats: `--format Table|Json|Yaml`; pipe JSON to `jq` for scripting.

File breadcrumbs worth reading first
- `MrWhoOidc.WebAuth/Program.cs` and `Infrastructure/EndpointMapping/*` – routing, admin groups, health endpoints.
- `MrWhoOidc.WebAuth/Handlers/*` – discovery, authorize, token, logout.
- `MrWhoOidc.WebAuth/Background/BackchannelLogoutDispatcher.cs` – durable outbox dispatcher.
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` – entities including backchannel outbox.
- `MrWhoOidc.UnitTests/*` – examples covering token, client, consent, key rotation, TE, DPoP.

Caveats
- Open items (key-rotation cross-replica lock, `AllowClientCredentials` default, tenant query filter failing open when no tenant is set, Medium findings) are tracked in `docs/oidc-idp-assessment-2026-10-04.md`.
