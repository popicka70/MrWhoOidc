# MrWhoOidc E2E Tests

End-to-end test suite for the MrWhoOidc OIDC Identity Provider using **Python + Playwright**.
Tests navigate the admin UI as a real user would, capture full-page screenshots, and optionally send them to either OpenAI or Ollama for evaluation. The suite also validates the dockerized RazorClient and TestApi example applications so the sample apps stay healthy with the identity provider. A comprehensive HTML report is produced after each run.

The example applications set `MrWhoOidc:DiscoveryUri` explicitly because the current `MrWhoOidc.Client` package requires an exact tenant discovery URL in multi-tenant deployments.

> Canonical guide: for `MrWhoOidc.WebAuth` E2E testing, this README is the source of truth. If an older note, terminal snippet, or another document conflicts with this file, follow this file.
> The workspace-root `QUICKSTART-TESTS.md` is not the canonical guide for the Python suite under `MrWhoOidc/e2e`.

---

## Prerequisites

| Requirement | Version |
| --- | --- |
| Python | 3.11+ |
| pip | latest |
| Docker + Docker Compose | for the running WebAuth instance |
| OpenAI API key or local Ollama | optional for LLM screenshot evaluation |

---

## Canonical Workflow

### 1. Use the supported Python environment

Use exactly one virtual environment for this suite: `MrWhoOidc/e2e/.venv`.

Do not create repo-root environments such as `.venv`, `.venv-1`, or `.venv-2`. Those are stray local artifacts and are not part of the supported workflow.

```bash
cd MrWhoOidc/e2e
sh ./setup-venv.sh
source .venv/bin/activate        # Linux/macOS
# .venv\Scripts\activate         # Windows PowerShell/CMD
python -m playwright install chromium
```

If you prefer not to activate the shell environment, call the interpreter directly.

```powershell
Push-Location .\MrWhoOidc\e2e
.\.venv\Scripts\python.exe -m playwright install chromium
Pop-Location
```

### 2. Let pytest own the environment reset

The session fixture in `conftest.py` is part of the supported workflow. At the start of a test session it:

- removes the old postgres container and volume
- starts a fresh `postgres`
- rebuilds and starts `webauth` from current source
- rebuilds and starts `testapi`, `razorclient`, `oidcdemo`, and `reactclient`
- waits for discovery and health endpoints
- deletes stale `.auth/state.json`

Do not rely on a long-lived dev stack when using this suite to validate code changes. The suite is intended to run from a clean seeded state.

### 3. Use the seeded admin credentials

The supported seeded defaults are:

- `ADMIN_USERNAME=admin@mrwho.local`
- `ADMIN_PASSWORD=E2E-test-password!`

`SEED_ADMIN_PASSWORD` must match `ADMIN_PASSWORD`. The defaults in `.env.example` and `conftest.py` already do this.

Do not hardcode `Admin123!` in new E2E tests.

### 4. Configure environment variables

```bash
cp .env.example .env
# Then edit .env and choose either the OpenAI or Ollama backend
```

| Variable | Default | Description |
| --- | --- | --- |
| `BASE_URL` | `https://localhost:8443` | URL of the running WebAuth instance |
| `EXAMPLE_RAZORCLIENT_URL` | `https://localhost:5003` | URL of the dockerized Razor Pages example client |
| `EXAMPLE_TESTAPI_URL` | `https://localhost:7149` | URL of the dockerized downstream example API |
| `EXAMPLE_OIDCDEMO_URL` | `https://localhost:5001` | URL of the dockerized OIDC demo app |
| `EXAMPLE_REACTCLIENT_URL` | `http://localhost:5173` | URL of the dockerized React demo app |
| `LLM_BACKEND` | `openai` | `openai` or `ollama` |
| `OPENAI_API_KEY` | _(empty)_ | OpenAI API key. If absent and `LLM_BACKEND=openai`, visual eval is skipped. |
| `OLLAMA_HOST` | `http://localhost:11434` | Base URL of the local Ollama server |
| `OLLAMA_MODEL` | `qwen3.5:397b-cloud` or custom | Model used when `LLM_BACKEND=ollama` |
| `ADMIN_USERNAME` | `admin@mrwho.local` | Admin login (seeded automatically on first start) |
| `ADMIN_PASSWORD` | `E2E-test-password!` | Admin password used by the suite and seed fixture |
| `HEADED` | `false` | Set `true` to watch the browser |
| `SLOW_MO` | `0` | Milliseconds between actions (for visual debugging) |
| `OPENAI_MODEL` | `gpt-4o` | Model used when `LLM_BACKEND=openai` |

### 5. Run the tests

From the workspace root in PowerShell:

```powershell
Push-Location .\MrWhoOidc\e2e
.\.venv\Scripts\python.exe -m pytest -v
Pop-Location
```

From inside `MrWhoOidc/e2e`:

```bash
# Run all tests
python -m pytest -v

# Run only public page tests (no login required)
python -m pytest tests/test_public_pages.py -v

# Run admin page tests
python -m pytest tests/test_admin_pages.py -v

# Run CRUD operation tests
python -m pytest tests/test_crud_operations.py -v

# Run tenant enrollment coverage
python -m pytest tests/test_tenant_domain_claims.py tests/test_tenant_enrollment.py -v

# Run the example application coverage
python -m pytest tests/test_example_apps.py -v

# Run a focused protocol slice
python -m pytest tests/test_oidc_flows.py::TestTokenExchangeFlow -v

# Show browser window
HEADED=true python -m pytest tests/test_public_pages.py

# Slow-motion mode for debugging
SLOW_MO=500 HEADED=true python -m pytest tests/test_admin_pages.py::TestAdminClients -v
```

### 6. Manual rebuild rules outside pytest

`docker-compose.dev.yml` uses built images, not bind-mounted source. If you edit `MrWhoOidc.WebAuth` or a dockerized example app and then verify behavior manually outside pytest, rebuild the affected service first.

```powershell
Push-Location .\MrWhoOidc
docker compose -f docker-compose.dev.yml up -d --build webauth
# or rebuild other services: testapi razorclient oidcdemo reactclient
Pop-Location
```

When you run pytest, the session fixture already performs the supported rebuild/start sequence. Do not duplicate it unless you are debugging a live container outside the suite.

### 7. View the report

After a run, find the output in:

```text
e2e/
├── screenshots/
│   └── 20260314_120000/        ← timestamped per run
│       ├── home.png
│       ├── login.png
│       ├── admin-clients.png
│       └── ...
└── reports/
    └── 20260314_120000/
        ├── report.json         ← machine-readable
        └── report.html         ← open in browser for visual review
```

Open `report.html` in your browser to see:

- Summary scores and high-severity issue count
- Per-page screenshot with LLM evaluation
- Category scores (layout, contrast, typography…)
- Actionable recommendations

---

## Adding Page Instructions

Each page can have a Markdown instruction file that tells the LLM exactly what to look for and what to verify. The test framework automatically loads the right instruction file based on the page route.

### Naming convention

| Route | Instruction file |
| --- | --- |
| `/` | `instructions/home.md` |
| `/login` | `instructions/login.md` |
| `/admin/clients` | `instructions/admin-clients.md` |
| `/admin/users/123/emails` | `instructions/admin-users-emails.md` |
| `/PlatformAdmin/Tenants` | `instructions/platformadmin-tenants.md` |

### File structure

```markdown
# Page: Client List
## Route: /admin/clients

## Expectations
- What should be visible on this page

## Actions
- What the test should click / verify interactively

## CRUD Operations
### Add
1. Step 1
2. Step 2

## Visual Checks
- Specific UI quality criteria for this page
```

The LLM receives the instruction file content alongside the screenshot, making the evaluation context-aware and specific.

If no instruction file exists for a page, the LLM still evaluates it using a generic UI quality rubric.

---

## Test Structure

```text
e2e/
├── conftest.py                   # Shared fixtures (auth, browser context, report)
├── pyproject.toml                # pytest configuration
├── requirements.txt              # Python deps
├── .env.example                  # Environment variable template
│
├── utils/
│   ├── instruction_loader.py     # Loads per-page MD instruction files
│   ├── screenshot_manager.py     # Captures & organises screenshots
│   ├── llm_evaluator.py          # GPT-4o vision integration
│   └── report_generator.py       # JSON + HTML report writer
│
├── tests/
│   ├── test_public_pages.py      # Home, Login, Privacy, Discovery endpoints
│   ├── test_account_pages.py     # Account Dashboard, Profile, Sessions, MFA…
│   ├── test_admin_pages.py       # All tenant-admin pages (60+ pages)
│   ├── test_platform_admin_pages.py  # Platform admin pages
│   ├── test_tenant_domain_claims.py  # Domain claims and auto-enrollment
│   ├── test_tenant_enrollment.py     # Tenant invitations and invite acceptance
│   ├── test_crud_operations.py   # Focused create/edit CRUD flows
│   └── test_example_apps.py      # Razor client + downstream API health-paths
│
├── instructions/                 # Per-page test instruction MD files
│   ├── home.md
│   ├── login.md
│   ├── admin-clients.md
│   └── ... (add more here)
│
└── templates/
    └── report.html.j2            # Jinja2 HTML report template
```

---

## Authentication

The test suite logs in as `admin@mrwho.local` / `E2E-test-password!` by default.

Browser session state is saved to `.auth/state.json` after the first login and reused for subsequent authenticated tests in the same run. The session reset fixture removes stale state before a fresh seeded run, and the `.auth/` directory is git-ignored.

## Python Environment Notes

- Canonical environment: `e2e/.venv`
- Bootstrap command: `sh ./e2e/setup-venv.sh`
- Activation: `source e2e/.venv/bin/activate`
- Direct invocation without activation: `e2e/.venv/bin/python -m pytest -v`
- Repo-root virtualenvs are not used by the E2E suite and should not be recreated.

---

## CI Integration (future)

The suite is CI-ready. To run in GitHub Actions:

1. Add `OPENAI_API_KEY` as a repository secret.
2. Use a service container for docker-compose.dev.yml.
3. Run `pytest --tb=short -q` and publish `e2e/reports/` as CI artifacts.

---

## FAQ

**Q: Tests fail with "Connection refused" or "SSL certificate error"?**  
A: If you are running pytest, let the session fixture start the stack. If you are manually checking the apps outside pytest, make sure `docker compose -f docker-compose.dev.yml up -d --build` has started the required services. The Playwright suite already uses `ignore_https_errors=True` for the local self-signed cert.

**Q: All tests skip with "Redirected to login — auth expired"?**  
A: Delete `.auth/state.json` to force a fresh login, or check that `ADMIN_PASSWORD` matches the seeded default `E2E-test-password!`.

**Q: My manual browser check still shows old behavior after a code change. Why?**  
A: `docker-compose.dev.yml` uses built images. Rebuild the changed service with `docker compose -f docker-compose.dev.yml up -d --build <service>` before retesting outside pytest.

**Q: LLM evaluation is skipped for all pages?**  
A: Set `OPENAI_API_KEY` in your `.env` file. Tests still pass without it — screenshots are captured but not evaluated.

**Q: How do I add a test for a new page?**  
A: Add a test class to the appropriate `test_*.py` file, create an instruction file in `instructions/`, and run `python -m pytest tests/your_file.py::YourClass -v`.
