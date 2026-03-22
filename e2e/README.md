# MrWhoOidc E2E Tests

End-to-end test suite for the MrWhoOidc OIDC Identity Provider using **Python + Playwright**.  
Tests navigate all pages of the admin UI as a real user would, capture full-page screenshots, and use **GPT-4o Vision** to evaluate UI quality and functional correctness. The suite also validates the dockerized RazorClient and TestApi example applications so the sample apps stay healthy with the identity provider. A comprehensive HTML report is produced after each run.

The example applications set `MrWhoOidc:DiscoveryUri` explicitly because the current `MrWhoOidc.Client` package requires an exact tenant discovery URL in multi-tenant deployments.

---

## Prerequisites

| Requirement | Version |
|---|---|
| Python | 3.11+ |
| pip | latest |
| Docker + Docker Compose | for the running WebAuth instance |
| OpenAI API key | for LLM screenshot evaluation (optional) |

---

## Quick Start

### 1. Start the application

```bash
# From repo root
docker compose -f docker-compose.dev.yml up -d
```

Wait ~30 seconds for PostgreSQL to be healthy and the app to seed default data.
The WebAuth UI will be available at **<https://localhost:8443>**.
The example applications used by the suite will be available at **<https://localhost:5003>** and **<https://localhost:7149>**.

### 2. Set up the Python environment

Use exactly one virtualenv for E2E work: `e2e/.venv`.

Do not create repo-root environments such as `.venv`, `.venv-1`, or `.venv-2`. Those are stray local artifacts and are not part of the supported workflow.

```bash
cd e2e
sh ./setup-venv.sh
source .venv/bin/activate        # Linux/macOS
# .venv\Scripts\activate         # Windows
playwright install chromium
```

If you prefer not to activate the shell environment, call the interpreter directly:

```bash
e2e/.venv/bin/python -m pytest -v
e2e/.venv/bin/playwright install chromium
```

### 3. Configure environment variables

```bash
cp .env.example .env
# Then edit .env and fill in your OPENAI_API_KEY
```

| Variable | Default | Description |
|---|---|---|
| `BASE_URL` | `https://localhost:8443` | URL of the running WebAuth instance |
| `EXAMPLE_RAZORCLIENT_URL` | `https://localhost:5003` | URL of the dockerized Razor Pages example client |
| `EXAMPLE_TESTAPI_URL` | `https://localhost:7149` | URL of the dockerized downstream example API |
| `OPENAI_API_KEY` | _(empty)_ | GPT-4o API key. If absent, visual eval is skipped. |
| `ADMIN_USERNAME` | `admin@mrwho.local` | Admin login (seeded automatically on first start) |
| `ADMIN_PASSWORD` | `Admin123!` | Admin password |
| `HEADED` | `false` | Set `true` to watch the browser |
| `SLOW_MO` | `0` | Milliseconds between actions (for visual debugging) |
| `OPENAI_MODEL` | `gpt-4o` | Override the model used for evaluation |

### 4. Run the tests

```bash
# Run all tests
pytest

# Run only public page tests (no login required)
pytest tests/test_public_pages.py -v

# Run admin page tests
pytest tests/test_admin_pages.py -v

# Run CRUD operation tests
pytest tests/test_crud_operations.py -v

# Run the example application coverage
pytest tests/test_example_apps.py -v

# Show browser window
HEADED=true pytest tests/test_public_pages.py

# Slow-motion mode for debugging
SLOW_MO=500 HEADED=true pytest tests/test_admin_pages.py::TestAdminClients -v
```

### 5. View the report

After a run, find the output in:

```
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
|---|---|
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

```
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

The test suite logs in as `admin@default.local` / `Admin123!` (seeded automatically by `TenantSeedingService` on first application start).

Browser session state is saved to `.auth/state.json` after the first login and reused for all subsequent authenticated tests in the same run. This significantly speeds up the suite. The `.auth/` directory is git-ignored.

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
A: Make sure `docker compose -f docker-compose.dev.yml up -d` is running and healthy. The `ignore_https_errors=True` Playwright setting handles self-signed certificates.

**Q: All tests skip with "Redirected to login — auth expired"?**  
A: Delete `.auth/state.json` to force a fresh login, or check that `ADMIN_PASSWORD` is correct.

**Q: LLM evaluation is skipped for all pages?**  
A: Set `OPENAI_API_KEY` in your `.env` file. Tests still pass without it — screenshots are captured but not evaluated.

**Q: How do I add a test for a new page?**  
A: Add a test class to the appropriate `test_*.py` file, create an instruction file in `instructions/`, and run `pytest tests/your_file.py::YourClass -v`.
