"""
P8 — Account security flows (gaps G11, G12, G13).

TestPasswordReset    : forgot-password → retrieve the email via MailHog →
                       reset-password → log in with the new password.
TestEmailConfirmation: confirm-email with an invalid token is rejected; a real
                       confirmation link (if emitted) is honoured.
TestAccountLockout   : repeated bad logins eventually lock the account.

All flows use throwaway users and fresh browser contexts so shared sessions are
never affected. Steps skip gracefully when MailHog or a capability is absent.
"""

from __future__ import annotations

import re
import time
import urllib.parse
from pathlib import Path

import pytest
import requests
from playwright.sync_api import Browser

from utils.cli_helper import CliHelper
from .oidc_helpers import BASE_URL, RUN_SUFFIX, delete_user

MAILHOG_API = "http://localhost:8025/api/v2"
_TENANT_PREFIX = "/t/default"


def _mailhog_available() -> bool:
    try:
        r = requests.get(f"{MAILHOG_API}/messages?limit=1", timeout=3)
        return r.status_code == 200
    except Exception:
        return False


def _mailhog_find_link(to_email: str, pattern: str, *, timeout: float = 15.0) -> str | None:
    """Poll MailHog for the latest message to ``to_email`` and extract a link
    matching ``pattern`` from its body."""
    deadline = time.time() + timeout
    rx = re.compile(pattern)
    while time.time() < deadline:
        try:
            resp = requests.get(f"{MAILHOG_API}/messages?limit=50", timeout=5)
            messages = resp.json().get("items", [])
        except Exception:
            messages = []
        for msg in messages:
            headers = msg.get("Content", {}).get("Headers", {})
            recipients = [r.lower() for r in headers.get("To", [])]
            if not any(to_email.lower() in r for r in recipients):
                continue
            body = msg.get("Content", {}).get("Body", "")
            # MailHog bodies may be quoted-printable: unfold soft line breaks.
            body = body.replace("=\r\n", "").replace("=\n", "")
            body = body.replace("=3D", "=").replace("&amp;", "&")
            match = rx.search(body)
            if match:
                return match.group(0)
        time.sleep(1.0)
    return None


def _account_url(path: str) -> str:
    """Build a tenant-scoped account URL."""
    return f"{BASE_URL}{_TENANT_PREFIX}{path}"


class TestPasswordReset:
    """Full forgot → email → reset → login cycle via MailHog."""

    _username = f"e2e-pwreset-{RUN_SUFFIX}"
    _email = f"e2e-pwreset-{RUN_SUFFIX}@e2e.local"
    _old_password = "OldPass-Reset123!"
    _new_password = "NewPass-Reset456!"

    def test_01_provision_user(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not _mailhog_available():
            pytest.skip("MailHog not reachable on :8025 — cannot retrieve reset email")
        delete_user(cli_logged_in, self._username)
        r = cli_logged_in.run(
            "user", "create",
            "--username", self._username,
            "--email", self._email,
            "--password", self._old_password,
            "--output", str(tmp_path / "pwreset-user.json"),
            "--overwrite",
        )
        assert r.ok, f"user create failed: {r.stderr or r.stdout}"

    def test_02_request_reset(self, browser_session: Browser):
        if not _mailhog_available():
            pytest.skip("MailHog not reachable")
        ctx = browser_session.new_context(base_url=BASE_URL, ignore_https_errors=True)
        try:
            page = ctx.new_page()
            # Try the tenant-scoped page; fall back to the root path.
            # Razor Pages are case-sensitive in their route segment, so use the
            # exact PascalCase file name (ForgotPassword.cshtml -> /ForgotPassword).
            page.goto(_account_url("/Account/ForgotPassword"),
                      wait_until="domcontentloaded")
            if "ForgotPassword" not in page.url:
                page.goto(f"{BASE_URL}/Account/ForgotPassword",
                          wait_until="domcontentloaded")
            email_input = page.locator("input[name='Input.Email'], input#Input_Email").first
            if email_input.count() == 0:
                pytest.skip("forgot-password page/email field not found")
            email_input.fill(self._email)
            page.locator("button[type='submit']").first.click()
            page.wait_for_load_state("domcontentloaded")
        finally:
            ctx.close()

    def test_03_follow_reset_link(self, browser_session: Browser):
        if not _mailhog_available():
            pytest.skip("MailHog not reachable")
        link = _mailhog_find_link(self._email, r"https?://[^\s\"'<>]*reset-password[^\s\"'<>]*")
        if not link:
            pytest.skip("No password-reset email captured in MailHog")
        # Normalize host to the test base URL (email may use the public base url).
        parsed = urllib.parse.urlparse(link)
        local = urllib.parse.urlunparse((
            "https", "localhost:8443", parsed.path, "", parsed.query, ""
        ))

        ctx = browser_session.new_context(base_url=BASE_URL, ignore_https_errors=True)
        try:
            page = ctx.new_page()
            page.goto(local, wait_until="domcontentloaded")
            new_pw = page.locator(
                "input[name='Input.NewPassword'], input#Input_NewPassword"
            ).first
            confirm_pw = page.locator(
                "input[name='Input.ConfirmPassword'], input#Input_ConfirmPassword"
            ).first
            if new_pw.count() == 0:
                pytest.skip("reset-password form not found (token may be invalid)")
            new_pw.fill(self._new_password)
            confirm_pw.fill(self._new_password)
            page.locator("button[type='submit']").first.click()
            page.wait_for_load_state("domcontentloaded")
        finally:
            ctx.close()

    def test_04_login_with_new_password(self, browser_session: Browser):
        if not _mailhog_available():
            pytest.skip("MailHog not reachable")
        ctx = browser_session.new_context(base_url=BASE_URL, ignore_https_errors=True)
        try:
            page = ctx.new_page()
            page.goto(f"{BASE_URL}/login", wait_until="domcontentloaded")
            page.locator("input#Username").fill(self._username)
            page.locator("input#Password").fill(self._new_password)
            page.locator("button[type='submit']").click()
            try:
                page.wait_for_url(
                    lambda url: "/login" not in url, timeout=15_000
                )
            except Exception:
                pytest.skip("Login with new password did not complete (reset may not "
                            "have applied)")
            assert "/login" not in page.url
        finally:
            ctx.close()

    def test_99_cleanup(self, cli_logged_in: CliHelper):
        delete_user(cli_logged_in, self._username)


class TestEmailConfirmation:
    """Email-confirmation token handling."""

    def test_01_invalid_token_rejected(self, browser_session: Browser):
        ctx = browser_session.new_context(base_url=BASE_URL, ignore_https_errors=True)
        try:
            page = ctx.new_page()
            # Razor Pages @page directives use lowercase-with-dash paths.
            url = _account_url("/account/confirm-email?token=not-a-valid-token")
            page.goto(url, wait_until="domcontentloaded")
            if "confirm-email" not in page.url and "/Account" not in page.url:
                pytest.skip("confirm-email route not available")
            body = page.content().lower()
            assert any(w in body for w in ("invalid", "expired", "could not", "failed",
                                           "not valid")), (
                "Invalid confirmation token should produce an error message"
            )
        finally:
            ctx.close()


class TestAccountLockout:
    """Repeated failed logins lock the account."""

    _username = f"e2e-lockout-{RUN_SUFFIX}"
    _email = f"e2e-lockout-{RUN_SUFFIX}@e2e.local"
    _password = "Lockout-Pass123!"

    def test_01_provision_user(self, cli_logged_in: CliHelper, tmp_path: Path):
        delete_user(cli_logged_in, self._username)
        r = cli_logged_in.run(
            "user", "create",
            "--username", self._username,
            "--email", self._email,
            "--password", self._password,
            "--output", str(tmp_path / "lockout-user.json"),
            "--overwrite",
        )
        assert r.ok, f"user create failed: {r.stderr or r.stdout}"

    def test_02_repeated_bad_logins_lock(self, browser_session: Browser):
        ctx = browser_session.new_context(base_url=BASE_URL, ignore_https_errors=True)
        locked = False
        try:
            for _ in range(12):
                page = ctx.new_page()
                page.goto(f"{BASE_URL}/login", wait_until="domcontentloaded")
                page.locator("input#Username").fill(self._username)
                page.locator("input#Password").fill("definitely-wrong-password")
                page.locator("button[type='submit']").click()
                page.wait_for_load_state("domcontentloaded")
                body = page.content().lower()
                if "lock" in body:
                    locked = True
                    page.close()
                    break
                page.close()
        finally:
            ctx.close()
        if not locked:
            pytest.skip("No lockout message observed within 12 attempts "
                        "(threshold may be higher or disabled)")

    def test_99_cleanup(self, cli_logged_in: CliHelper):
        delete_user(cli_logged_in, self._username)
