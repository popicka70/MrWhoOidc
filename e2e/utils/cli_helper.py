"""
CLI helper for running mrwho-cli commands in E2E tests.

Wraps subprocess calls to the globally-installed ``mrwho-cli`` tool and
provides convenience methods for login (involving browser-based device-code
approval), read operations, and CRUD mutations.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


@dataclass
class CliResult:
    """Outcome of a single mrwho-cli invocation."""

    exit_code: int
    stdout: str
    stderr: str

    @property
    def ok(self) -> bool:
        return self.exit_code == 0

    def json(self) -> Any:
        """Parse stdout as JSON (caller ensures --format Json was used)."""
        return json.loads(self.stdout)


class CliHelper:
    """Thin wrapper around the ``mrwho-cli`` binary."""

    # Minimum seconds between consecutive CLI invocations to avoid 429s.
    REQUEST_INTERVAL: float = 2.5
    # Number of retries on HTTP 429 before giving up.
    MAX_RETRIES: int = 5
    # Backoff multiplier: wait RETRY_BACKOFF * attempt seconds after a 429.
    RETRY_BACKOFF: float = 3.0

    def __init__(self, server_url: str, *, timeout: int = 60) -> None:
        self.server_url = server_url.rstrip("/")
        self.timeout = timeout
        self._last_call: float = 0.0
        self._cli_bin = shutil.which("mrwho-cli")
        if not self._cli_bin:
            raise FileNotFoundError(
                "mrwho-cli is not installed or not on PATH. "
                "Run `bash deploy-mrwho-cli.sh` from the repo root."
            )
        # Wipe any existing CLI config so tests start clean
        self._config_dir = Path.home() / ".mrwhooidc"
        config_file = self._config_dir / "config.json"
        if config_file.exists():
            config_file.unlink()

    # ------------------------------------------------------------------
    # Low-level runner
    # ------------------------------------------------------------------

    def _throttle(self) -> None:
        """Ensure at least REQUEST_INTERVAL seconds between calls."""
        now = time.monotonic()
        elapsed = now - self._last_call
        if elapsed < self.REQUEST_INTERVAL:
            time.sleep(self.REQUEST_INTERVAL - elapsed)
        self._last_call = time.monotonic()

    def run(self, *args: str, timeout: int | None = None) -> CliResult:
        """Run ``mrwho-cli <args>`` synchronously, retrying on HTTP 429."""
        cmd = [self._cli_bin, *args]
        for attempt in range(1, self.MAX_RETRIES + 1):
            self._throttle()
            proc = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                timeout=timeout or self.timeout,
                env={**os.environ, "DOTNET_NOLOGO": "1", "NO_COLOR": "1"},
            )
            result = CliResult(
                exit_code=proc.returncode,
                stdout=proc.stdout,
                stderr=proc.stderr,
            )
            # Retry on rate-limit (429)
            if "429" in result.stdout or "429" in result.stderr:
                if attempt < self.MAX_RETRIES:
                    time.sleep(self.RETRY_BACKOFF * attempt)
                    continue
            return result
        return result  # last attempt

    def run_json(self, *args: str, timeout: int | None = None) -> Any:
        """Run a command with ``--format Json`` and return parsed JSON."""
        result = self.run(*args, "--format", "Json", timeout=timeout)
        if not result.ok:
            raise RuntimeError(
                f"mrwho-cli {' '.join(args)} failed (exit {result.exit_code}):\n"
                f"{result.stderr or result.stdout}"
            )
        return result.json()

    # ------------------------------------------------------------------
    # Device-code login (needs browser interaction)
    # ------------------------------------------------------------------

    def start_login(self) -> subprocess.Popen:
        """
        Start ``mrwho-cli login`` as a background process.

        Returns the Popen handle.  The caller must read stdout to find the
        verification URI and user code, approve in a browser, then wait for
        the process to finish.
        """
        cmd = [self._cli_bin, "login", "--server", self.server_url]
        return subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            env={**os.environ, "DOTNET_NOLOGO": "1", "NO_COLOR": "1"},
        )

    @staticmethod
    def parse_device_login_output(
        proc: subprocess.Popen, *, read_timeout: float = 30
    ) -> tuple[str | None, str | None]:
        """
        Read the login process output for the verification URL and user code.

        Returns ``(verification_url, user_code)`` or ``(None, None)`` if
        parsing fails within *read_timeout* seconds.
        """
        output_lines: list[str] = []
        verification_url: str | None = None
        user_code: str | None = None
        deadline = time.monotonic() + read_timeout

        def _read_lines() -> None:
            nonlocal verification_url, user_code
            assert proc.stdout is not None
            for line in proc.stdout:
                output_lines.append(line)
                # Look for verification_uri_complete (full URL with embedded code)
                url_match = re.search(r"(https?://\S+/device\S*)", line)
                if url_match:
                    verification_url = url_match.group(1)
                # Look for user code (e.g. "ABCD-EFGH" or "User code: XXXX-YYYY")
                code_match = re.search(r"[A-Z]{4}-[A-Z]{4}", line)
                if code_match:
                    user_code = code_match.group(0)
                if verification_url and user_code:
                    break

        reader = threading.Thread(target=_read_lines, daemon=True)
        reader.start()
        reader.join(timeout=read_timeout)

        return verification_url, user_code
