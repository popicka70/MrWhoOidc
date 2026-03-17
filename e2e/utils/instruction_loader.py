"""
Loads per-page test instructions from Markdown files in e2e/instructions/.

Instruction file naming convention follows the page route:
  /                        → home.md
  /login                   → login.md
  /admin/clients           → admin-clients.md
  /admin/users/123/emails  → admin-users-emails.md  (strips IDs)

Usage:
    loader = InstructionLoader()
    instructions = loader.load("/admin/clients")
    if instructions:
        print(instructions.content)
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path


@dataclass
class PageInstructions:
    page_name: str
    route: str
    content: str
    file_path: Path


class InstructionLoader:
    """Resolves instruction Markdown files by page route."""

    def __init__(self, instructions_dir: Path | None = None) -> None:
        if instructions_dir is None:
            # Resolve relative to this file: e2e/utils/ → e2e/instructions/
            instructions_dir = Path(__file__).parent.parent / "instructions"
        self.instructions_dir = instructions_dir

    def load(self, route: str) -> PageInstructions | None:
        """Return PageInstructions for *route*, or None if no file found."""
        candidates = self._candidate_filenames(route)
        for filename in candidates:
            path = self.instructions_dir / filename
            if path.exists():
                content = path.read_text(encoding="utf-8")
                page_name = self._extract_page_name(content, route)
                return PageInstructions(
                    page_name=page_name,
                    route=route,
                    content=content,
                    file_path=path,
                )
        return None

    # ------------------------------------------------------------------
    # Private helpers
    # ------------------------------------------------------------------

    def _candidate_filenames(self, route: str) -> list[str]:
        """Return ordered list of filenames to try for *route*."""
        # Normalise: lower-case, strip trailing slash
        normalised = route.strip("/").lower()

        # Strip dynamic segments (UUID / numeric IDs / base64-ish tokens)
        without_ids = re.sub(
            r"/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            "",
            normalised,
            flags=re.IGNORECASE,
        )
        without_ids = re.sub(r"/\d+", "", without_ids)
        # Further strip last path segment if it looks like a slug after other removals
        without_last = "/".join(without_ids.split("/")[:-1]) if "/" in without_ids else without_ids

        candidates: list[str] = []
        for base in [without_ids, without_last, normalised]:
            if not base:
                filename = "home.md"
            else:
                filename = base.replace("/", "-") + ".md"
            if filename not in candidates:
                candidates.append(filename)

        # Always try bare "home.md" for root
        if route in ("/", ""):
            candidates.insert(0, "home.md")

        return candidates

    def _extract_page_name(self, content: str, route: str) -> str:
        """Parse '# Page: ...' from content, fall back to route."""
        match = re.search(r"^#\s*Page:\s*(.+)$", content, re.MULTILINE)
        if match:
            return match.group(1).strip()
        return route
