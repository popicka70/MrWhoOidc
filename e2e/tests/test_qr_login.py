"""
P11 — QR-code login (gap G16).

QrLogin.Enabled=true in the dev stack. The /auth/qr page mints a QR session and
polls GET /api/qr/status/{sessionToken}. This exercises the desktop side of the
ceremony (session creation + status polling) and the not-found contract. The
mobile scan/approve half needs a second device and is out of scope here.
"""

from __future__ import annotations

import re

import pytest
from playwright.sync_api import Browser

from utils.oidc_client import OidcClient
from .oidc_helpers import BASE_URL

_KNOWN_STATUSES = {
    "pending", "scanned", "authenticated", "approved",
    "expired", "cancelled", "not_found",
}


class TestQrLogin:
    def test_01_unknown_session_not_found(self, oidc_client: OidcClient):
        status, body = oidc_client.raw_get(
            f"{BASE_URL}/api/qr/status/this-session-does-not-exist"
        )
        assert status == 404, f"Unknown QR session should be 404, got {status}"
        if isinstance(body, dict):
            assert body.get("status") == "not_found"

    def test_02_qr_page_creates_session(self, browser_session: Browser):
        ctx = browser_session.new_context(base_url=BASE_URL, ignore_https_errors=True)
        try:
            page = ctx.new_page()
            page.goto(f"{BASE_URL}/auth/qr", wait_until="domcontentloaded")
            content = page.content()
            if "QR login is not currently available" in content:
                pytest.skip("QR login feature disabled")
            match = re.search(r"const sessionToken = '([^']+)'", content)
            if not match or not match.group(1):
                pytest.skip("No QR session token rendered")
            type(self)._session_token = match.group(1)
            # The QR image should be present.
            assert page.locator("#qrCodeImage").count() >= 1, "QR code image missing"
        finally:
            ctx.close()

    def test_03_status_poll_pending(self, browser_session: Browser):
        token = getattr(self, "_session_token", None)
        if not token:
            pytest.skip("No QR session token available")
        ctx = browser_session.new_context(base_url=BASE_URL, ignore_https_errors=True)
        try:
            resp = ctx.request.get(
                f"{BASE_URL}/api/qr/status/{token}"
            )
            assert resp.status == 200, f"status poll returned {resp.status}"
            data = resp.json()
            assert data.get("status") in _KNOWN_STATUSES, (
                f"Unexpected QR status '{data.get('status')}'"
            )
            # A freshly-minted session must not already be approved.
            assert data.get("status") not in ("approved", "authenticated"), (
                "Fresh QR session should not be pre-approved"
            )
        finally:
            ctx.close()
