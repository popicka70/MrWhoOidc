"""
Shared pytest fixtures for MrWhoOidc E2E tests.

Key fixtures:
  page              – unauthenticated page per test
  authenticated_page – admin-authenticated page per test (loads saved auth state)
  screenshot_mgr    – ScreenshotManager for the current run
  llm_evaluator     – LLMEvaluator (Ollama/OpenAI backend)
  record_evaluation – captures screenshot, runs LLM eval, records in report
  report_generator  – session-scoped report collector, writes HTML+JSON at end

Architecture note:
  Login is performed ONCE via a sync session fixture that calls asyncio.run().
  The resulting storage state is saved to .auth/state.json and reloaded per-test.
  This avoids the pytest-asyncio session-loop deadlock with Playwright.
  Each test owns its own Playwright instance + browser + context (function scope).
"""

from __future__ import annotations

import asyncio
import os
from datetime import datetime
from pathlib import Path
from typing import AsyncGenerator, Callable, Awaitable

import pytest
from dotenv import load_dotenv
from playwright.async_api import (
    BrowserContext,
    Page,
    async_playwright,
)

# Load .env from this directory (or fallback to repo root)
_ENV_PATH = Path(__file__).parent / ".env"
if not _ENV_PATH.exists():
    _ENV_PATH = Path(__file__).parent.parent / ".env"
load_dotenv(_ENV_PATH, override=False)

from utils.instruction_loader import InstructionLoader
from utils.llm_evaluator import EvaluationResult, LLMEvaluator
from utils.report_generator import ReportGenerator
from utils.screenshot_manager import ScreenshotManager

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

BASE_URL: str = os.getenv("BASE_URL", "https://localhost:8443")
ADMIN_USERNAME: str = os.getenv("ADMIN_USERNAME", "admin@mrwho.local")
ADMIN_PASSWORD: str = os.getenv("ADMIN_PASSWORD", "Admin123!")
_AUTH_STATE_FILE: Path = Path(__file__).parent / ".auth" / "state.json"
_RUN_ID: str = datetime.now().strftime("%Y%m%d_%H%M%S")


def _is_headed() -> bool:
    return os.getenv("HEADED", "false").lower() in ("true", "1")


def _slow_mo() -> int:
    return int(os.getenv("SLOW_MO", "300" if _is_headed() else "0"))


def _launch_args() -> dict:
    return dict(
        headless=not _is_headed(),
        slow_mo=_slow_mo(),
        args=["--ignore-certificate-errors"],
    )


# ---------------------------------------------------------------------------
# Session-scoped SYNC fixtures
# (sync avoids the pytest-asyncio/Playwright session-loop deadlock)
# ---------------------------------------------------------------------------


@pytest.fixture(scope="session")
def run_id() -> str:
    return _RUN_ID


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
    """Write the combined report after all tests have run."""
    yield
    json_path, html_path = report_generator.finalize()
    print(f"\n\n{'='*60}")
    print(f"  E2E Evaluation Report written:")
    print(f"    JSON : {json_path}")
    print(f"    HTML : {html_path}")
    print(f"{'='*60}\n")


@pytest.fixture(scope="session")
def auth_state_file() -> Path:
    """Logs in once and saves browser storage state to .auth/state.json.

    This is a SYNC fixture that calls asyncio.run() so it gets its own event
    loop, completely separate from pytest-asyncio's per-test loops.
    """
    _AUTH_STATE_FILE.parent.mkdir(parents=True, exist_ok=True)

    async def _login() -> None:
        async with async_playwright() as pw:
            # Always launch headless for the one-time setup login
            browser = await pw.chromium.launch(
                headless=not _is_headed(),
                slow_mo=_slow_mo(),
                args=["--ignore-certificate-errors"],
            )
            ctx = await browser.new_context(
                base_url=BASE_URL,
                viewport={"width": 1920, "height": 1080},
                ignore_https_errors=True,
            )
            page = await ctx.new_page()
            await page.goto(f"{BASE_URL}/login", wait_until="domcontentloaded")
            await page.locator("input#Username").fill(ADMIN_USERNAME)
            await page.locator("input#Password").fill(ADMIN_PASSWORD)
            await page.locator("button[type='submit']").click()
            await page.wait_for_url(
                lambda url: "/login" not in url and "/LoginTotp" not in url,
                timeout=30_000,
            )
            await ctx.storage_state(path=str(_AUTH_STATE_FILE))
            await ctx.close()
            await browser.close()

    asyncio.run(_login())
    return _AUTH_STATE_FILE


# ---------------------------------------------------------------------------
# Function-scoped async page fixtures
# Each test gets its own Playwright + browser + context, avoiding shared loops.
# ---------------------------------------------------------------------------


@pytest.fixture
async def page() -> AsyncGenerator[Page, None]:
    """Fresh unauthenticated page per test."""
    async with async_playwright() as pw:
        browser = await pw.chromium.launch(**_launch_args())
        ctx = await browser.new_context(
            base_url=BASE_URL,
            viewport={"width": 1920, "height": 1080},
            ignore_https_errors=True,
        )
        p = await ctx.new_page()
        yield p
        await ctx.close()
        await browser.close()


@pytest.fixture
async def authenticated_page(auth_state_file: Path) -> AsyncGenerator[Page, None]:
    """Function-scoped authenticated page (admin@mrwho.local).

    Loads the saved storage state — no re-login needed per test.
    """
    async with async_playwright() as pw:
        browser = await pw.chromium.launch(**_launch_args())
        ctx = await browser.new_context(
            base_url=BASE_URL,
            viewport={"width": 1920, "height": 1080},
            ignore_https_errors=True,
            storage_state=str(auth_state_file),
        )
        p = await ctx.new_page()
        yield p
        await ctx.close()
        await browser.close()


# ---------------------------------------------------------------------------
# High-level helper fixture: navigate → screenshot → LLM eval → record
# ---------------------------------------------------------------------------


@pytest.fixture
def record_evaluation(
    screenshot_mgr: ScreenshotManager,
    llm_evaluator: LLMEvaluator,
    instruction_loader: InstructionLoader,
    report_generator: ReportGenerator,
) -> Callable[[Page, str, str | None], Awaitable[EvaluationResult]]:
    """Returns an async callable: ``await record_evaluation(page, route)``."""

    async def _impl(page: Page, route: str, label: str | None = None) -> EvaluationResult:
        screenshot_path = await screenshot_mgr.capture(page, route, label=label)
        page_instructions = instruction_loader.load(route)
        instructions_content = page_instructions.content if page_instructions else None
        # Run synchronous LLM call in a thread so it doesn't block Playwright's loop
        loop = asyncio.get_running_loop()
        result = await loop.run_in_executor(
            None,
            lambda: llm_evaluator.evaluate(
                screenshot_path=screenshot_path,
                route=route,
                instructions_content=instructions_content,
            ),
        )
        report_generator.add(result)
        return result

    return _impl
