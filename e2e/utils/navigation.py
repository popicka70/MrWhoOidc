"""
Navigation retry helpers for pages loaded under parallel-worker load.

Under 3-worker pytest-xdist load, the dev stack occasionally holds a request for tens of
seconds without responding (observed: the request is accepted and the tenant middleware
runs, but the endpoint never executes; zero error logs; the app recovers within seconds).
Chromium then surfaces a blank white error page while ``domcontentloaded`` still completes,
so ``page.goto`` looks successful to the caller while the DOM is empty. The same stall can
hit a login POST (see the login-retry logic in conftest). Navigations that time out or end
on a blank render are therefore retried with a short backoff before the outcome is handed
to the caller — the same philosophy as ``goto_and_read_body`` in conftest.
"""

from __future__ import annotations

import time
from typing import Any

from playwright.sync_api import Error as PlaywrightError, Page

#: Default per-attempt navigation timeout.
DEFAULT_TIMEOUT_MS = 25_000

#: Elements every rendered application page has at least one of. A DOM without any of them
#: is a blank error render, not a real page.
_STRUCTURAL_SELECTOR = "h1, h2, h3, form, table, nav, main, button, a"


def is_blank_render(page: Page) -> bool:
    """True when the page shows no meaningful content (a blank Chromium error page)."""
    try:
        if page.inner_text("body").strip():
            return False
    except PlaywrightError:
        return True
    return page.locator(_STRUCTURAL_SELECTOR).first.count() == 0


def make_retrying_goto(
    page: Page,
    *,
    timeout_ms: int = DEFAULT_TIMEOUT_MS,
    retries: int = 2,
    backoff_s: float = 2.0,
) -> None:
    """Replace ``page.goto`` on *page* with a version that retries transient stalls.

    A navigation is retried when it raises a Playwright error (timeout, connection reset)
    or when it "succeeds" into a blank error page. After the last attempt the original
    Playwright error is re-raised; a page that still renders blank is left as-is so the
    caller's own assertions (or the LLM evaluator) report the broken state instead of
    the retry logic masking it.
    """
    original_goto = page.goto

    def _retrying_goto(url: str, **kwargs: Any) -> Any:
        kwargs.setdefault("wait_until", "domcontentloaded")
        kwargs.setdefault("timeout", timeout_ms)
        last_error: Exception | None = None
        for attempt in range(1, retries + 2):
            try:
                response = original_goto(url, **kwargs)
                if not is_blank_render(page):
                    return response
                last_error = RuntimeError(f"navigation to {url} ended on a blank page")
            except PlaywrightError as exc:
                last_error = exc
            if attempt <= retries:
                time.sleep(backoff_s)
        if isinstance(last_error, PlaywrightError):
            raise last_error
        return None

    page.goto = _retrying_goto  # type: ignore[method-assign]
