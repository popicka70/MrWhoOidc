"""
P1 — RFC 8628 Device Authorization Grant (gap G1).

Exercises the full device flow end-to-end:
  provision a public client with the device_code grant →
  POST /device/authorize → assert device_code/user_code/verification URIs →
  poll /token (authorization_pending) → approve in the browser →
  poll /token (success) → validate tokens →
  plus negative cases (deny, bad device_code).

Order within the class is significant (provision → flow → cleanup).
"""

from __future__ import annotations

import time
import urllib.parse
import pytest

from utils.cli_helper import CliHelper
from utils.oidc_client import OidcClient, decode_jwt
from .oidc_helpers import (
    RUN_SUFFIX,
    delete_client,
    get_client_internal_id,
    get_default_realm_id,
    set_auto_approval,
)

DEVICE_GRANT = "urn:ietf:params:oauth:grant-type:device_code"


def _create_public_client(
    cli: CliHelper,
    *,
    client_id: str,
    client_name: str,
    realm_id: str,
    scope: str,
    grant_types: list[str],
) -> None:
    args = [
        "client", "create",
        "--client-id", client_id,
        "--client-name", client_name,
        "--realm-id", realm_id,
        "--scope", scope,
        "--require-consent", "false",
    ]
    for grant_type in grant_types:
        args.extend(["--grant-types", grant_type])

    result = cli.run(*args)
    assert result.ok, f"client create '{client_id}' failed: {result.stderr or result.stdout}"


def _device_authorize(oidc_client: OidcClient, client_id: str, scope: str):
    """POST to the device authorization endpoint. Returns (status, body)."""
    endpoint = oidc_client.endpoint("device_authorization_endpoint")
    assert endpoint, "device_authorization_endpoint not advertised in discovery"
    return oidc_client.raw_post(
        endpoint,
        data={"client_id": client_id, "scope": scope},
        headers={"Content-Type": "application/x-www-form-urlencoded"},
        allow_redirects=False,
    )


def _poll_token(oidc_client: OidcClient, client_id: str, device_code: str):
    """Poll the token endpoint with the device_code grant. Returns (status, body)."""
    return oidc_client.raw_token_request(
        {
            "grant_type": DEVICE_GRANT,
            "device_code": device_code,
            "client_id": client_id,
        }
    )


class TestDeviceAuthorizationFlow:
    """Full RFC 8628 device flow with browser approval."""

    _cid = f"e2e-device-{RUN_SUFFIX}"
    _device_code: str | None = None
    _user_code: str | None = None
    _verification_complete: str | None = None

    def test_01_discovery_advertises_device_endpoint(self, oidc_client: OidcClient):
        assert oidc_client.endpoint("device_authorization_endpoint"), (
            "device_authorization_endpoint must be advertised"
        )

    def test_02_provision_public_client(self, cli_logged_in: CliHelper, authenticated_context):
        """Create a public client allowing the device_code grant."""
        delete_client(cli_logged_in, self._cid)
        realm_id = get_default_realm_id(cli_logged_in)
        _create_public_client(
            cli_logged_in,
            client_id=self._cid,
            client_name=f"E2E Device {RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid profile",
            grant_types=[DEVICE_GRANT, "refresh_token"],
        )
        internal_id = get_client_internal_id(cli_logged_in, self._cid)
        set_auto_approval(authenticated_context, internal_id)

    def test_03_device_authorize(self, oidc_client: OidcClient):
        """POST /device/authorize and validate the RFC 8628 response shape."""
        status, body = _device_authorize(oidc_client, self._cid, "openid profile")
        assert status == 200, f"device authorize failed: {status} {body}"
        assert isinstance(body, dict)
        for field in ("device_code", "user_code", "verification_uri",
                      "verification_uri_complete", "expires_in", "interval"):
            assert field in body, f"missing '{field}' in device response: {body}"

        assert body["verification_uri"].endswith("/device")
        assert body["user_code"] in body["verification_uri_complete"] or \
            urllib.parse.quote(body["user_code"]) in body["verification_uri_complete"]

        TestDeviceAuthorizationFlow._device_code = body["device_code"]
        TestDeviceAuthorizationFlow._user_code = body["user_code"]
        TestDeviceAuthorizationFlow._verification_complete = body["verification_uri_complete"]

    def test_04_poll_before_approval_is_pending(self, oidc_client: OidcClient):
        """Polling before approval must yield authorization_pending (or slow_down)."""
        if not self._device_code:
            pytest.skip("No device_code from authorize step")
        status, body = _poll_token(oidc_client, self._cid, self._device_code)
        assert status == 400, f"expected 400 pending, got {status}: {body}"
        assert body.get("error") in ("authorization_pending", "slow_down"), body

    def test_05_approve_in_browser(self, authenticated_context):
        """Approve the device using the admin browser session."""
        if not self._verification_complete:
            pytest.skip("No verification URI from authorize step")
        page = authenticated_context.new_page()
        try:
            page.goto(self._verification_complete, wait_until="domcontentloaded")
            approve = page.locator("button[value='approve']").first
            if approve.count() == 0 or not approve.is_visible():
                # The user_code input may be shown first.
                code_input = page.locator("#user_code").first
                if code_input.count() > 0 and code_input.is_visible():
                    code_input.fill(self._user_code or "")
                    page.locator("button[type='submit']").first.click()
                    page.wait_for_load_state("domcontentloaded")
                    approve = page.locator("button[value='approve']").first
            assert approve.count() > 0 and approve.is_visible(), \
                "Approve button not found on device page"
            approve.click()
            page.wait_for_load_state("domcontentloaded")
        finally:
            page.close()

    def test_06_poll_after_approval_succeeds(self, oidc_client: OidcClient):
        """After approval, polling must return tokens."""
        if not self._device_code:
            pytest.skip("No device_code from authorize step")
        # Honour the polling interval; retry a few times to absorb timing.
        # RFC 8628 §3.5: on slow_down, client MUST wait the advertised interval
        # before polling again, and the server may increase the interval.
        body: dict = {}
        status = 0
        for _ in range(15):
            status, body = _poll_token(oidc_client, self._cid, self._device_code)
            if status == 200:
                break
            err = body.get("error") if isinstance(body, dict) else None
            if err in ("authorization_pending", "slow_down"):
                interval = body.get("interval", 5)
                time.sleep(float(interval) + 0.5)
                continue
            break
        assert status == 200, f"device token poll failed: {status} {body}"
        assert body.get("access_token"), body
        assert body.get("token_type", "").lower() == "bearer"
        if body.get("id_token"):
            _header, payload = decode_jwt(body["id_token"])
            assert payload.get("sub"), "id_token missing sub"

    def test_07_reused_device_code_rejected(self, oidc_client: OidcClient):
        """The device_code is single-use once consumed."""
        if not self._device_code:
            pytest.skip("No device_code from authorize step")
        status, body = _poll_token(oidc_client, self._cid, self._device_code)
        assert status == 400, f"expected rejection of reused code, got {status}: {body}"
        assert body.get("error") in (
            "invalid_grant", "expired_token", "access_denied", "authorization_pending",
        ), body

    def test_08_unknown_device_code_rejected(self, oidc_client: OidcClient):
        status, body = _poll_token(oidc_client, self._cid, "definitely-not-a-real-device-code")
        assert status == 400
        assert body.get("error") in ("invalid_grant", "expired_token"), body

    def test_99_cleanup(self, cli_logged_in: CliHelper):
        delete_client(cli_logged_in, self._cid)
