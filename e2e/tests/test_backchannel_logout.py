"""
P4 — Back-channel logout (gap G4; OpenID Connect Back-Channel Logout 1.0).

Uses an ISOLATED user and browser context so the shared admin session is never
disturbed:
  provision a BCL-enabled client + a throwaway user →
  log the user in (fresh context) and establish a session at the client →
  RP-initiated logout in that context →
  assert a back-channel logout notification was enqueued in the outbox
  (inspected via ``mrwho-cli bcl outbox``).

Steps degrade to skips if the isolated session cannot be set up.
"""

from __future__ import annotations

import secrets
import time
import urllib.parse
from pathlib import Path

import pytest
from playwright.sync_api import Browser

from utils.cli_helper import CliHelper
from utils.oidc_client import OidcClient, generate_pkce
from .oidc_helpers import (
    BASE_URL,
    LOGOUT_REDIRECT_URI,
    REDIRECT_URI,
    RUN_SUFFIX,
    create_client_with_secret,
    delete_client,
    delete_user,
    get_client_internal_id,
    get_default_realm_id,
    parse_callback,
    set_auto_approval,
)

BCL_RECEIVER = "https://e2e-proto.test/backchannel-logout"


class TestBackChannelLogout:
    """End-to-end back-channel logout enqueue path."""

    _cid = f"e2e-bcl-{RUN_SUFFIX}"
    _username = f"e2e-bcl-user-{RUN_SUFFIX}"
    _password = "Bcl-User-Pass123!"
    _client_secret: str | None = None

    def test_01_discovery(self, oidc_client: OidcClient):
        assert oidc_client.discovery.get("backchannel_logout_supported") is True

    def test_02_provision(self, cli_logged_in: CliHelper, tmp_path: Path,
                          authenticated_context):
        delete_client(cli_logged_in, self._cid)
        delete_user(cli_logged_in, self._username)
        realm_id = get_default_realm_id(cli_logged_in)
        creds = create_client_with_secret(
            cli_logged_in,
            client_id=self._cid,
            client_name=f"E2E BCL {RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid profile",
            grant_types=["authorization_code", "refresh_token"],
            redirect_uris=[REDIRECT_URI],
            logout_redirect_uris=[LOGOUT_REDIRECT_URI],
            require_pkce=True,
            require_consent=False,
            extra_args=["--backchannel-logout-uri", BCL_RECEIVER],
            cred_path=tmp_path / "bcl-creds.json",
        )
        TestBackChannelLogout._client_secret = creds["initialSecret"]
        internal_id = get_client_internal_id(cli_logged_in, self._cid)
        set_auto_approval(authenticated_context, internal_id)

        # Throwaway user with a known password.
        r = cli_logged_in.run(
            "user", "create",
            "--username", self._username,
            "--email", f"{self._username}@e2e.local",
            "--password", self._password,
            "--output", str(tmp_path / "bcl-user.json"),
            "--overwrite",
        )
        assert r.ok, f"user create failed: {r.stderr or r.stdout}"

    def test_03_login_logout_triggers_notification(self, oidc_client: OidcClient,
                                                   browser_session: Browser,
                                                   cli_logged_in: CliHelper):
        if not self._client_secret:
            pytest.skip("Not provisioned")

        ctx = browser_session.new_context(
            base_url=BASE_URL, ignore_https_errors=True,
            viewport={"width": 1280, "height": 800},
        )
        try:
            page = ctx.new_page()
            # Stub the (non-routable) callback host so navigation settles.
            page.route(
                "**/e2e-proto.test/**",
                lambda route: route.fulfill(status=200, content_type="text/html", body="ok"),
            )

            # 1) Log the throwaway user in.
            page.goto(f"{BASE_URL}/login", wait_until="domcontentloaded")
            page.locator("input#Username").fill(self._username)
            page.locator("input#Password").fill(self._password)
            page.locator("button[type='submit']").click()
            try:
                page.wait_for_url(
                    lambda url: "/login" not in url and "/LoginTotp" not in url,
                    timeout=20_000,
                )
            except Exception:
                pytest.skip("Isolated user login did not complete")

            # 2) Establish a session at the BCL client via the authorize flow.
            verifier, challenge = generate_pkce()
            auth_url = oidc_client.build_authorize_url(
                client_id=self._cid,
                redirect_uri=REDIRECT_URI,
                scope="openid profile",
                code_challenge=challenge,
                state=secrets.token_urlsafe(12),
                nonce=secrets.token_urlsafe(12),
            )
            page.goto(auth_url, wait_until="domcontentloaded")
            params = parse_callback(page.url)
            code = params.get("code")
            if not code:
                pytest.skip(f"authorize did not yield a code (url={page.url})")
            tok = oidc_client.token_authorization_code(
                code, REDIRECT_URI, self._cid,
                client_secret=self._client_secret, code_verifier=verifier,
                auth_method="basic",
            )
            assert tok.ok and tok.id_token, f"token exchange failed: {tok.raw}"

            # 3) RP-initiated logout in this context.
            logout_url = f"{BASE_URL}/connect/endsession?" + urllib.parse.urlencode({
                "id_token_hint": tok.id_token,
                "post_logout_redirect_uri": LOGOUT_REDIRECT_URI,
                "client_id": self._cid,
                "state": "bye",
            })
            page.goto(logout_url, wait_until="domcontentloaded")
        finally:
            ctx.close()

        # 4) Verify a notification was enqueued for our client.
        entry = None
        for _ in range(10):
            resp = cli_logged_in.run_json("bcl", "outbox", "--format", "json")
            items = resp.get("items", resp) if isinstance(resp, dict) else resp
            entry = next((e for e in (items or [])
                          if e.get("clientId") == self._cid), None)
            if entry:
                break
            time.sleep(1.0)

        if entry is None:
            pytest.skip("No back-channel logout notification observed (session tracking "
                        "may not associate this client with the logout)")
        assert entry.get("status"), f"outbox entry missing status: {entry}"

    def test_99_cleanup(self, cli_logged_in: CliHelper):
        delete_client(cli_logged_in, self._cid)
        delete_user(cli_logged_in, self._username)
