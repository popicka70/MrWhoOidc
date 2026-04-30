"""
Shared pytest fixtures for MrWhoOidc E2E tests (sync Playwright).

Architecture:
  - One Playwright browser launched for the whole session (no per-test launch).
  - Login done ONCE; storage state saved to .auth/state.json.
  - One authenticated BrowserContext shared across all authenticated tests.
  - Each test gets a new Page (tab) from that shared context -- fast.
  - Unauthenticated tests get a fresh BrowserContext+Page, closed after the test.
  - LLM evaluation is fully synchronous; no asyncio needed.
"""

from __future__ import annotations

import os
import ssl
import subprocess
import time
import urllib.request
from datetime import datetime
from pathlib import Path
from typing import Callable, Generator

import pytest
from dotenv import load_dotenv
from playwright.sync_api import Browser, BrowserContext, Page, sync_playwright

_ENV_PATH = Path(__file__).parent / ".env"
if not _ENV_PATH.exists():
    _ENV_PATH = Path(__file__).parent.parent / ".env"
load_dotenv(_ENV_PATH, override=False)

from utils.cli_helper import CliHelper
from utils.instruction_loader import InstructionLoader
from utils.llm_evaluator import EvaluationResult, LLMEvaluator
from utils.oidc_client import OidcClient
from utils.report_generator import ReportGenerator
from utils.screenshot_manager import ScreenshotManager

BASE_URL: str = os.getenv("BASE_URL", "https://localhost:8443")
PORTAL_BASE_URL: str = os.getenv("PORTAL_BASE_URL", "http://localhost:8088")
LICENSING_ADMIN_URL: str = os.getenv("LICENSING_ADMIN_URL", "https://localhost:7443")
EXAMPLE_RAZORCLIENT_URL: str = os.getenv(
    "EXAMPLE_RAZORCLIENT_URL", "https://localhost:5003"
)
EXAMPLE_TESTAPI_URL: str = os.getenv("EXAMPLE_TESTAPI_URL", "https://localhost:7149")
EXAMPLE_OIDCDEMO_URL: str = os.getenv("EXAMPLE_OIDCDEMO_URL", "https://localhost:5001")
EXAMPLE_REACTCLIENT_URL: str = os.getenv(
    "EXAMPLE_REACTCLIENT_URL", "http://localhost:5173"
)
ADMIN_USERNAME: str = os.getenv("ADMIN_USERNAME", "admin@mrwho.local")
ADMIN_PASSWORD: str = os.getenv("ADMIN_PASSWORD", "E2E-test-password!")
# Must match ADMIN_PASSWORD so the auto-seed creates an admin with this password.
# Also set in reset_database() via subprocess env so docker compose picks it up.
SEED_ADMIN_PASSWORD: str = os.getenv("SEED_ADMIN_PASSWORD", ADMIN_PASSWORD)
_AUTH_STATE_FILE: Path = Path(__file__).parent / ".auth" / "state.json"
_RUN_ID: str = datetime.now().strftime("%Y%m%d_%H%M%S")


def _is_headed() -> bool:
    return os.getenv("HEADED", "false").lower() in ("true", "1")


def _slow_mo() -> int:
    return int(os.getenv("SLOW_MO", "0"))


def _wait_for_url(url: str, *, timeout_seconds: int, insecure: bool = False) -> None:
    for _ in range(timeout_seconds):
        context = ssl._create_unverified_context() if insecure else None
        try:
            with urllib.request.urlopen(url, timeout=5, context=context) as response:
                if response.status < 500:
                    return
        except Exception:
            pass
        time.sleep(1)
    raise RuntimeError(
        f"Endpoint did not become ready within {timeout_seconds} s: {url}"
    )


# ---------------------------------------------------------------------------
# Session-scoped fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="session")
def run_id() -> str:
    return _RUN_ID


@pytest.fixture(scope="session")
def base_url() -> str:
    return BASE_URL


@pytest.fixture(scope="session")
def example_razorclient_url() -> str:
    return EXAMPLE_RAZORCLIENT_URL


@pytest.fixture(scope="session")
def example_testapi_url() -> str:
    return EXAMPLE_TESTAPI_URL


@pytest.fixture(scope="session")
def example_oidcdemo_url() -> str:
    return EXAMPLE_OIDCDEMO_URL


@pytest.fixture(scope="session")
def example_reactclient_url() -> str:
    return EXAMPLE_REACTCLIENT_URL


@pytest.fixture(scope="session")
def screenshot_mgr(run_id: str) -> ScreenshotManager:
    return ScreenshotManager(run_id=run_id)


@pytest.fixture(scope="session")
def llm_evaluator() -> LLMEvaluator:
    return LLMEvaluator()


@pytest.fixture(scope="session")
def instruction_loader() -> InstructionLoader:
    return InstructionLoader()


@pytest.fixture(scope="session")
def report_generator(run_id: str) -> ReportGenerator:
    return ReportGenerator(run_id=run_id)


@pytest.fixture(scope="session", autouse=True)
def finalize_report(report_generator: ReportGenerator):
    yield
    json_path, html_path, plan_path = report_generator.finalize()
    print(f"\n\n{'=' * 60}")
    print(f"  E2E Evaluation Report written:")
    print(f"    JSON : {json_path}")
    print(f"    HTML : {html_path}")
    print(f"    Plan : {plan_path}")
    print(f"{'=' * 60}\n")


@pytest.fixture(scope="session", autouse=True)
def reset_database() -> None:
    """Drop and recreate the postgres container + volume before every test session."""
    workspace_root = Path(__file__).parent.parent
    compose_file = str(workspace_root / "docker-compose.dev.yml")
    overlay_compose_file = str(
        workspace_root / "docker-compose.licensing-portal.dev.yml"
    )
    project = "mrwhooidc"
    base_cmd = [
        "docker",
        "compose",
        "-f",
        compose_file,
        "-f",
        overlay_compose_file,
        "-p",
        project,
    ]

    # Stop and remove app containers that depend on the seeded database.
    subprocess.run(
        [
            *base_cmd,
            "rm",
            "-sf",
            "portalweb",
            "licensingapi",
            "reactclient",
            "oidcdemo",
            "razorclient",
            "testapi",
            "webauth",
            "postgres",
        ],
        check=False,
    )

    # Remove the data volumes so both stacks start from a clean state.
    subprocess.run(["docker", "volume", "rm", f"{project}_postgres-data"], check=False)
    subprocess.run(["docker", "volume", "rm", f"{project}_licensing-data"], check=False)

    # Start a fresh postgres
    subprocess.run([*base_cmd, "up", "-d", "postgres"], check=True)

    # Wait up to 60 s for postgres to become healthy
    container = f"{project}-postgres-1"
    for _ in range(60):
        health = subprocess.run(
            ["docker", "inspect", "--format={{.State.Health.Status}}", container],
            capture_output=True,
            text=True,
        )
        if health.stdout.strip() == "healthy":
            break
        time.sleep(1)
    else:
        raise RuntimeError("Postgres did not become healthy within 60 s")

    # Start webauth from the current source — it runs EF migrations on startup.
    # Pass SEED_ADMIN_PASSWORD so the auto-seed creates a known admin password.
    webauth_env = os.environ.copy()
    webauth_env["SEED_ADMIN_PASSWORD"] = SEED_ADMIN_PASSWORD
    subprocess.run(
        [*base_cmd, "up", "-d", "--build", "webauth"], check=True, env=webauth_env
    )

    ready_url = f"{BASE_URL}/t/default/.well-known/openid-configuration"
    _wait_for_url(ready_url, timeout_seconds=120, insecure=True)

    # Start the portal and licensing overlay from the current source.
    subprocess.run(
        [*base_cmd, "up", "-d", "--build", "licensingapi", "portalweb"], check=True
    )
    _wait_for_url(f"{LICENSING_ADMIN_URL}/health", timeout_seconds=120, insecure=True)
    _wait_for_url(f"{PORTAL_BASE_URL}/portal.html", timeout_seconds=90, insecure=False)

    # Start the example applications from the current source.
    subprocess.run(
        [
            *base_cmd,
            "up",
            "-d",
            "--build",
            "testapi",
            "razorclient",
            "oidcdemo",
            "reactclient",
        ],
        check=True,
    )
    _wait_for_url(f"{EXAMPLE_TESTAPI_URL}/health", timeout_seconds=90, insecure=True)
    _wait_for_url(
        f"{EXAMPLE_RAZORCLIENT_URL}/health", timeout_seconds=90, insecure=True
    )
    _wait_for_url(f"{EXAMPLE_OIDCDEMO_URL}/health", timeout_seconds=90, insecure=True)
    _wait_for_url(
        f"{EXAMPLE_REACTCLIENT_URL}/health", timeout_seconds=90, insecure=True
    )

    # Clear any stale auth state so login is performed against the fresh DB
    if _AUTH_STATE_FILE.exists():
        _AUTH_STATE_FILE.unlink()


@pytest.fixture(scope="session")
def portal_base_url() -> str:
    return PORTAL_BASE_URL


@pytest.fixture(scope="session")
def licensing_admin_url() -> str:
    return LICENSING_ADMIN_URL


@pytest.fixture(scope="session")
def browser_session() -> Generator[Browser, None, None]:
    """Single Playwright browser for the whole session."""
    with sync_playwright() as pw:
        browser = pw.chromium.launch(
            headless=not _is_headed(),
            slow_mo=_slow_mo(),
            args=["--ignore-certificate-errors"],
        )
        yield browser
        browser.close()


@pytest.fixture(scope="session")
def auth_state_file(browser_session: Browser, reset_database: None) -> Path:
    """Log in once and save storage state."""
    _AUTH_STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
    ctx = browser_session.new_context(
        base_url=BASE_URL,
        viewport={"width": 1920, "height": 1080},
        ignore_https_errors=True,
    )
    p = ctx.new_page()
    p.goto(f"{BASE_URL}/login", wait_until="domcontentloaded")
    p.locator("input#Username").fill(ADMIN_USERNAME)
    p.locator("input#Password").fill(ADMIN_PASSWORD)
    p.locator("button[type='submit']").click()
    p.wait_for_url(
        lambda url: "/login" not in url and "/LoginTotp" not in url,
        timeout=30_000,
    )
    ctx.storage_state(path=str(_AUTH_STATE_FILE))
    ctx.close()
    return _AUTH_STATE_FILE


@pytest.fixture(scope="session")
def authenticated_context(
    browser_session: Browser, auth_state_file: Path
) -> Generator[BrowserContext, None, None]:
    """Shared authenticated context for the whole session."""
    ctx = browser_session.new_context(
        base_url=BASE_URL,
        viewport={"width": 1920, "height": 1080},
        ignore_https_errors=True,
        storage_state=str(auth_state_file),
    )
    yield ctx
    ctx.close()


# ---------------------------------------------------------------------------
# Function-scoped page fixtures
# ---------------------------------------------------------------------------


@pytest.fixture
def page(browser_session: Browser) -> Generator[Page, None, None]:
    """Fresh unauthenticated context+page per test."""
    ctx = browser_session.new_context(
        base_url=BASE_URL,
        viewport={"width": 1920, "height": 1080},
        ignore_https_errors=True,
    )
    p = ctx.new_page()
    yield p
    ctx.close()


@pytest.fixture
def authenticated_page(
    authenticated_context: BrowserContext,
) -> Generator[Page, None, None]:
    """New tab in the shared authenticated context (fast -- no browser re-launch)."""
    p = authenticated_context.new_page()
    yield p
    p.close()


# ---------------------------------------------------------------------------
# Evaluation helper
# ---------------------------------------------------------------------------


@pytest.fixture
def record_evaluation(
    screenshot_mgr: ScreenshotManager,
    llm_evaluator: LLMEvaluator,
    instruction_loader: InstructionLoader,
    report_generator: ReportGenerator,
) -> Callable[..., EvaluationResult]:
    """Returns a sync callable: ``record_evaluation(page, route[, label])``."""

    def _impl(page: Page, route: str, label: str | None = None) -> EvaluationResult:
        screenshot_path = screenshot_mgr.capture(page, route, label=label)
        page_instructions = instruction_loader.load(route)
        instructions_content = page_instructions.content if page_instructions else None
        result = llm_evaluator.evaluate(
            screenshot_path=screenshot_path,
            route=route,
            instructions_content=instructions_content,
        )
        report_generator.add(result)
        return result

    return _impl


# ---------------------------------------------------------------------------
# CLI fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="session")
def cli_helper() -> CliHelper:
    """Session-scoped CliHelper pointing at the running WebAuth instance."""
    return CliHelper(server_url=f"{BASE_URL}/t/default")


@pytest.fixture(scope="session")
def cli_logged_in(
    cli_helper: CliHelper,
    authenticated_context: BrowserContext,
    auth_state_file: Path,
) -> CliHelper:
    """
    Enable CLI access for the default tenant, perform device-code login via
    browser approval, and return the ready-to-use CliHelper.

    This fixture is session-scoped so the login happens only once.
    """
    import logging

    log = logging.getLogger("cli_login")

    # Step 1: Enable CLI access via the admin settings page
    page = authenticated_context.new_page()
    try:
        page.goto(f"{BASE_URL}/admin/settings", wait_until="domcontentloaded")
        cli_checkbox = page.locator("#cliAccessEnabled")
        if cli_checkbox.count() > 0 and cli_checkbox.is_visible():
            if not cli_checkbox.is_checked():
                cli_checkbox.check()
                # Submit the settings form
                btn = page.locator("button.btn-primary[type='submit']").first
                if btn.count() > 0 and btn.is_visible():
                    btn.click()
                    page.wait_for_load_state("domcontentloaded")
                    log.info("CLI access enabled via admin settings")
            else:
                log.info("CLI access already enabled")
        else:
            log.warning("CLI access checkbox not found on settings page")
    finally:
        page.close()

    # Step 2: Start the CLI login process (device-code flow)
    proc = cli_helper.start_login()
    verification_url, user_code = CliHelper.parse_device_login_output(
        proc, read_timeout=30
    )

    if not verification_url:
        # Dump what we got for debugging
        remaining = proc.stdout.read() if proc.stdout else ""
        proc.kill()
        raise RuntimeError(
            f"Failed to parse device login output. "
            f"URL={verification_url}, code={user_code}, remaining={remaining}"
        )

    log.info("Device code flow: URL=%s, code=%s", verification_url, user_code)

    # Step 3: Approve the device in the browser
    approval_page = authenticated_context.new_page()
    try:
        approval_page.goto(verification_url, wait_until="domcontentloaded")

        # If we landed on the confirmation page, click Authorize
        approve_btn = approval_page.locator("button[value='approve']").first
        if approve_btn.count() > 0 and approve_btn.is_visible():
            approve_btn.click()
            approval_page.wait_for_load_state("domcontentloaded")
            log.info("Device authorization approved")
        else:
            # Maybe user_code input is shown — enter it manually
            code_input = approval_page.locator("#user_code").first
            if code_input.count() > 0 and code_input.is_visible():
                code_input.fill(user_code or "")
                approval_page.locator("button[type='submit']").first.click()
                approval_page.wait_for_load_state("domcontentloaded")
                # Now the confirmation page should appear
                approve_btn = approval_page.locator("button[value='approve']").first
                if approve_btn.count() > 0 and approve_btn.is_visible():
                    approve_btn.click()
                    approval_page.wait_for_load_state("domcontentloaded")
                    log.info("Device authorization approved (after code entry)")
    finally:
        approval_page.close()

    # Step 4: Wait for the CLI login to complete
    try:
        proc.wait(timeout=30)
    except subprocess.TimeoutExpired:
        proc.kill()
        raise RuntimeError("CLI login did not complete within 30 s after approval")

    if proc.returncode != 0:
        raise RuntimeError(f"CLI login failed with exit code {proc.returncode}")

    # Verify login succeeded
    result = cli_helper.run("profile", "show")
    if not result.ok:
        raise RuntimeError(f"CLI profile show failed after login: {result.stderr}")

    log.info("CLI login successful")
    return cli_helper


@pytest.fixture(scope="session")
def oidc_client(reset_database: None) -> OidcClient:
    """Session-scoped OidcClient pointing at the default tenant, with discovery cached."""
    client = OidcClient(f"{BASE_URL}/t/default")
    client.discover()
    return client


@pytest.fixture(scope="session")
def install_enterprise_license(
    authenticated_context: BrowserContext,
) -> str:
    """
    Generate an Enterprise+ tenant-scoped license and install it via the admin API.

    Returns the signed JWT string.  Opt-in fixture — only tests that need
    license-gated features should request it.
    """
    import logging

    from utils.license_generator import LicenseGenerator, ALL_FEATURES

    log = logging.getLogger("license_install")

    # Use tenant scope so the tenant-admin can install it.
    # Exclude multi_tenancy which is a platform-only feature.
    tenant_features = [f for f in ALL_FEATURES if f != "multi_tenancy"]

    generator = LicenseGenerator()
    license_jwt = generator.generate(
        tier="enterprise+",
        organization="E2E Test Organization",
        scope="tenant",
        deployment_mode="",
        features=tenant_features,
        valid_seconds=86400,  # 24h
    )
    log.info("Generated Enterprise+ tenant-scoped test license")

    # Use Playwright's API request context which shares the authenticated cookies.
    api = authenticated_context.request
    response = api.post(
        f"{BASE_URL}/admin/api/license",
        data={"licenseKey": license_jwt, "notes": "E2E test license"},
        headers={"Content-Type": "application/json"},
    )

    if response.status != 200:
        body = response.text()
        raise RuntimeError(f"License install failed ({response.status}): {body}")

    log.info("Enterprise+ license installed successfully")
    return license_jwt
