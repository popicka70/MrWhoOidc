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
import subprocess
import time
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

from utils.instruction_loader import InstructionLoader
from utils.llm_evaluator import EvaluationResult, LLMEvaluator
from utils.report_generator import ReportGenerator
from utils.screenshot_manager import ScreenshotManager

BASE_URL: str = os.getenv("BASE_URL", "https://localhost:8443")
ADMIN_USERNAME: str = os.getenv("ADMIN_USERNAME", "admin@mrwho.local")
ADMIN_PASSWORD: str = os.getenv("ADMIN_PASSWORD", "Admin123!")
_AUTH_STATE_FILE: Path = Path(__file__).parent / ".auth" / "state.json"
_RUN_ID: str = datetime.now().strftime("%Y%m%d_%H%M%S")


def _is_headed() -> bool:
    return os.getenv("HEADED", "false").lower() in ("true", "1")


def _slow_mo() -> int:
    return int(os.getenv("SLOW_MO", "0"))


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
    print(f"\n\n{'='*60}")
    print(f"  E2E Evaluation Report written:")
    print(f"    JSON : {json_path}")
    print(f"    HTML : {html_path}")
    print(f"    Plan : {plan_path}")
    print(f"{'='*60}\n")


@pytest.fixture(scope="session", autouse=True)
def reset_database() -> None:
    """Drop and recreate the postgres container + volume before every test session."""
    compose_file = str(Path(__file__).parent.parent / "docker-compose.dev.yml")
    project = "mrwhooidc"
    base_cmd = ["docker", "compose", "-f", compose_file, "-p", project]

    # Stop and remove webauth and postgres (leave redis/mailhog running)
    subprocess.run([*base_cmd, "rm", "-sf", "webauth", "postgres"], check=False)

    # Remove the postgres data volume so the DB is fresh
    subprocess.run(["docker", "volume", "rm", f"{project}_postgres-data"], check=False)

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

    # Start webauth — it runs EF migrations on startup
    subprocess.run([*base_cmd, "up", "-d", "webauth"], check=True)

    # Wait up to 120 s for webauth to respond (migrations on a fresh DB can take a while).
    # We use curl -k so any HTTP response (including 4xx/redirects) counts as "up".
    # urllib.urlopen raises on non-2xx and would loop the full timeout on a 404.
    ready_url = f"{BASE_URL}/t/default/.well-known/openid-configuration"
    for _ in range(60):
        result = subprocess.run(
            ["curl", "-k", "-s", "-o", "/dev/null", "-w", "%{http_code}",
             "--connect-timeout", "3", "--max-time", "5", ready_url],
            capture_output=True, text=True,
        )
        if result.returncode == 0 and result.stdout.strip() != "":
            break
        time.sleep(2)
    else:
        raise RuntimeError("WebAuth did not become ready within 120 s")

    # Clear any stale auth state so login is performed against the fresh DB
    if _AUTH_STATE_FILE.exists():
        _AUTH_STATE_FILE.unlink()


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
def authenticated_page(authenticated_context: BrowserContext) -> Generator[Page, None, None]:
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
