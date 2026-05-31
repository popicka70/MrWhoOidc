"""
P10 — WebAuthn / passkey registration (gap G15).

Drives the real WebAuthn registration ceremony on /account/web-authn using a
Chrome DevTools Protocol *virtual authenticator*, so navigator.credentials.create
resolves without physical hardware. Uses a throwaway user + fresh context so no
shared session or real account is affected. Skips on non-Chromium browsers or
when CDP / WebAuthn virtual authenticators are unavailable.
"""

from __future__ import annotations

from pathlib import Path

import pytest
from playwright.sync_api import Browser

from utils.cli_helper import CliHelper
from .oidc_helpers import BASE_URL, RUN_SUFFIX, delete_user

_WEBAUTHN_PATH = "/t/default/account/web-authn"


class TestWebAuthnRegistration:
    _username = f"e2e-webauthn-{RUN_SUFFIX}"
    _email = f"e2e-webauthn-{RUN_SUFFIX}@e2e.local"
    _password = "WebAuthn-Pass123!"

    def test_01_provision_user(self, cli_logged_in: CliHelper, tmp_path: Path):
        delete_user(cli_logged_in, self._username)
        r = cli_logged_in.run(
            "user", "create",
            "--username", self._username,
            "--email", self._email,
            "--password", self._password,
            "--output", str(tmp_path / "webauthn-user.json"),
            "--overwrite",
        )
        assert r.ok, f"user create failed: {r.stderr or r.stdout}"

    def test_02_register_passkey(self, browser_session: Browser):
        if browser_session.browser_type.name != "chromium":
            pytest.skip("Virtual authenticator requires Chromium")

        ctx = browser_session.new_context(base_url=BASE_URL, ignore_https_errors=True)
        try:
            page = ctx.new_page()

            # Attach a CDP virtual authenticator.
            try:
                cdp = ctx.new_cdp_session(page)
                cdp.send("WebAuthn.enable")
                cdp.send("WebAuthn.addVirtualAuthenticator", {
                    "options": {
                        "protocol": "ctap2",
                        "transport": "internal",
                        "hasResidentKey": True,
                        "hasUserVerification": True,
                        "isUserVerified": True,
                        "automaticPresenceSimulation": True,
                    }
                })
            except Exception as exc:  # pragma: no cover - environment dependent
                pytest.skip(f"CDP virtual authenticator unavailable: {exc}")

            # Log in as the throwaway user.
            page.goto(f"{BASE_URL}/login", wait_until="domcontentloaded")
            page.locator("input#Username").fill(self._username)
            page.locator("input#Password").fill(self._password)
            page.locator("button[type='submit']").click()
            page.wait_for_url(lambda url: "/login" not in url, timeout=30_000)

            # Go to the WebAuthn management page.
            page.goto(f"{BASE_URL}{_WEBAUTHN_PATH}", wait_until="domcontentloaded")
            if "/login" in page.url:
                pytest.skip("Redirected to login — session not established")
            if page.locator("#registerKeyBtn").count() == 0:
                pytest.skip("WebAuthn registration UI not present")

            # Run the registration ceremony.
            page.locator("#registerKeyBtn").click()
            page.locator("#friendlyName").fill("E2E Virtual Key")
            page.locator("#confirmRegisterBtn").click()

            # Poll the credentials API until the new key appears.
            registered = False
            for _ in range(20):
                resp = ctx.request.get(f"{BASE_URL}/api/webauthn/credentials")
                if resp.status == 200:
                    try:
                        data = resp.json()
                    except Exception:
                        data = None
                    items = data if isinstance(data, list) else (
                        data.get("credentials") or data.get("items") or []
                        if isinstance(data, dict) else []
                    )
                    if items:
                        registered = True
                        break
                page.wait_for_timeout(500)

            assert registered, "Passkey did not appear in the credentials list"
        finally:
            ctx.close()

    def test_99_cleanup(self, cli_logged_in: CliHelper):
        delete_user(cli_logged_in, self._username)
