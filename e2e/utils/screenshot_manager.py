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

    def __init__(self, run_id: str | None = None, output_dir: Path | None = None) -> None:
        if output_dir is None:
            output_dir = Path(__file__).parent.parent / "screenshots"
        if run_id is None:
            run_id = datetime.now().strftime("%Y%m%d_%H%M%S")
        self.run_id = run_id
        self.run_dir = output_dir / run_id
        self.run_dir.mkdir(parents=True, exist_ok=True)
        self._counter: dict[str, int] = {}

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

        file_path = self.run_dir / f"{slug}.png"
        page.screenshot(path=str(file_path), full_page=True)
        return file_path

    @staticmethod
    def _wait_for_transitions(page: Page, timeout_ms: int = 2000) -> None:
        """Wait until all CSS transitions and animations have finished."""
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
