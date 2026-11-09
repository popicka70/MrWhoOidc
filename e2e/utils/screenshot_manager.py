"""
Captures full-page screenshots and organises them in a timestamped directory.

Usage:
    manager = ScreenshotManager()
    path = manager.capture(page, "/admin/clients")
"""

from __future__ import annotations

import re
from datetime import datetime
from pathlib import Path

from playwright.sync_api import Page


class ScreenshotManager:
    """Saves full-page screenshots to e2e/screenshots/{run_id}/{slug}.png."""

    def __init__(self, run_id: str | None = None, output_dir: Path | None = None, worker: str | None = None) -> None:
        if output_dir is None:
            output_dir = Path(__file__).parent.parent / "screenshots"
        if run_id is None:
            run_id = datetime.now().strftime("%Y%m%d_%H%M%S")
        self.run_id = run_id
        self.run_dir = output_dir / run_id
        self.run_dir.mkdir(parents=True, exist_ok=True)
        self._counter: dict[str, int] = {}
        # pytest-xdist workers write into the same run dir; without a per-worker suffix two workers
        # capturing the same route would overwrite each other's screenshot.
        self._worker_suffix = f".{worker}" if worker else ""

    def capture(self, page: Page, route: str, label: str | None = None) -> Path:
        """Take a full-page screenshot; return the saved file path."""
        slug = self._route_to_slug(route)
        if label:
            slug = f"{slug}--{self._route_to_slug(label)}"

        count = self._counter.get(slug, 0)
        self._counter[slug] = count + 1
        if count > 0:
            slug = f"{slug}-{count}"

        self._wait_for_transitions(page)

        file_path = self.run_dir / f"{slug}{self._worker_suffix}.png"
        page.screenshot(path=str(file_path), full_page=True)
        return file_path

    @staticmethod
    def _wait_for_transitions(page: Page, timeout_ms: int = 2000) -> None:
        """Wait until the page has actually rendered before screenshotting.

        Cold-start captures (the first page loads on a freshly rebuilt stack) previously
        fired right after domcontentloaded but before content fully painted: the
        screenshot showed a blank page or a page missing buttons/icons, and the LLM
        evaluator then scored a page state that does not exist. We now wait for, in
        order:
          1. the 'load' event — all resources fetched (8s cap; animations need this);
          2. web fonts to finish loading — icons/text painted with their real faces
             rather than fallbacks (blank icon squares were read as "icon missing");
          3. the document to have visible text content — guards against capturing a
             mid-render blank frame (only 'networkidle' hangs on WebSocket/SSE pages,
             but load alone does not guarantee the body painted);
          4. CSS transitions/animations to finish — avoid half-faded frames.
        Every wait is best-effort: a timeout falls through to the screenshot instead of
        failing the test, so a legitimately sparse page can still be captured and
        judged on its actual content.
        """
        try:
            # 'load' fires after all resources are fetched; safe for admin pages
            # (only 'networkidle' hangs on WebSocket/SSE pages).
            page.wait_for_load_state("load", timeout=8000)
        except Exception:
            pass

        try:
            page.wait_for_function(
                "async () => { await document.fonts.ready; return true; }",
                timeout=5000,
            )
        except Exception:
            pass

        try:
            page.wait_for_function(
                "() => document.body && document.body.innerText.trim().length > 40",
                timeout=10_000,
            )
        except Exception:
            # Fall through: some pages are legitimately sparse; capture what is there.
            pass

        try:
            page.wait_for_function(
                """() => {
                    const elements = document.querySelectorAll('*');
                    for (const el of elements) {
                        const animations = el.getAnimations ? el.getAnimations() : [];
                        for (const anim of animations) {
                            if (anim.playState === 'running') return false;
                        }
                    }
                    return true;
                }""",
                timeout=timeout_ms,
            )
        except Exception:
            # If check times out or fails, proceed anyway
            pass

    @staticmethod
    def _route_to_slug(route: str) -> str:
        """Convert a URL route or label to a safe filename slug."""
        slug = route.strip("/").lower()
        slug = re.sub(
            r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            "id",
            slug,
            flags=re.IGNORECASE,
        )
        slug = re.sub(r"\b\d+\b", "id", slug)
        slug = re.sub(r"[^a-z0-9]+", "-", slug).strip("-")
        return slug or "home"
