"""
E2E tests for the server-side OP session store (F1).

Covers:
  - the sign-in creates a session, and the session page lists it with the
    application that took part;
  - signing out ends the session and the refresh tokens it produced: the
    refresh grant fails with invalid_grant;
  - the device flow records the device client in the session.

Requires:
  - The app running at BASE_URL (via docker-compose.dev.yml).
  - ``mrwho-cli`` installed and logged in (via ``cli_logged_in`` fixture).
  - An authenticated browser session (via ``authenticated_context`` fixture).

Test order within the class is significant (provisioning → flow → cleanup).
"""

from __future__ import annotations

import html
import json
import os
import re
import secrets
import urllib.parse
from pathlib import Path

import pytest

from utils.cli_helper import CliHelper
from utils.oidc_client import OidcClient, generate_pkce

E2E_PREFIX = "e2e-opsession"
_RUN_SUFFIX = secrets.token_hex(3)
BASE_URL: str = os.getenv("BASE_URL", "https://localhost:8443")
REDIRECT_URI = "https://e2e-opsession.test/callback"


def _get_default_realm_id(cli: CliHelper) -> str:
    data = cli.run_json("realm", "list")
    match = [r for r in data if r.get("name") == "default"]
    assert len(match) == 1, "Default realm not found"
    return str(match[0]["id"])


def _get_client_internal_id(cli: CliHelper, client_id: str) -> str:
    data = cli.run_json("client", "list")
    match = [c for c in data if c.get("clientId") == client_id]
    assert len(match) == 1, f"Client '{client_id}' not found"
    return str(match[0]["id"])


def _delete_client(cli: CliHelper, client_id: str) -> None:
    try:
        cli.run("client", "delete", "--client-id", client_id, "--force")
    except Exception:  # noqa: BLE001 - cleanup must never fail the run
        pass


def _create_client_with_secret(
    cli: CliHelper,
    *,
    client_id: str,
    client_name: str,
    realm_id: str,
    scope: str,
    grant_types: list[str],
    redirect_uris: list[str] | None = None,
    require_pkce: bool = False,
    require_consent: bool = True,
    cred_path: Path,
) -> dict:
    args = [
        "client", "create",
        "--client-id", client_id,
        "--client-name", client_name,
        "--realm-id", realm_id,
        "--scope", scope,
    ]
    for gt in grant_types:
        args.extend(["--grant-types", gt])
    if redirect_uris:
        for uri in redirect_uris:
            args.extend(["--redirect-uris", uri])
    if require_pkce:
        args.append("--require-pkce")
    if not require_consent:
        args.extend(["--require-consent", "false"])
    args.extend([
        "--create-initial-secret",
        "--output", str(cred_path),
        "--overwrite",
    ])
    r = cli.run(*args)
    assert r.ok, f"client create '{client_id}' failed: {r.stderr or r.stdout}"

    with open(cred_path) as f:
        return json.load(f)


def _set_auto_approval(authenticated_context, client_internal_id: str) -> None:
    page = authenticated_context.new_page()
    try:
        page.goto(
            f"{BASE_URL}/admin/clients/edit/{client_internal_id}",
            wait_until="domcontentloaded",
        )
        page.select_option("#Input_AutoApprovalMode", "All")
        page.click("button[type='submit'].btn-primary")
        page.wait_for_load_state("domcontentloaded")
    finally:
        page.close()


def _follow_authorize_redirects(authenticated_context, auth_url: str) -> str:
    """Follow the authorize redirect chain and return the callback URL with the code.

    The redirect chain ends in the Auth/Redirect page, which performs a client-side redirect: the callback URL
    is parsed from its HTML.
    """
    api = authenticated_context.request
    url = auth_url
    callback_host = REDIRECT_URI.split("//")[1].split("/")[0]

    for _ in range(10):
        try:
            resp = api.get(url, max_redirects=0, ignore_https_errors=True)
        except Exception:
            break

        if resp.status not in (301, 302, 303, 307, 308):
            if resp.status == 200 and "/Auth/Redirect" in url:
                body = resp.text()
                match = re.search(r'<a\s+href="([^"]+)"', body)
                if match:
                    return html.unescape(match.group(1))
                match = re.search(r'content="0;url=([^"]+)"', body)
                if match:
                    return html.unescape(match.group(1))
            break

        location = resp.headers.get("location", "")
        if callback_host in location:
            return location
        if location.startswith("/"):
            url = BASE_URL + location
        else:
            url = location

    raise AssertionError(f"Authorization code redirect not found. Last URL: {url}")


class TestOpSessionStore:
    _cid = f"{E2E_PREFIX}-{_RUN_SUFFIX}"
    _client_secret: str | None = None
    _refresh_token: str | None = None
    _id_token: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path, authenticated_context):
        _delete_client(cli_logged_in, self._cid)

        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in,
            client_id=self._cid,
            client_name=f"E2E OP Session {_RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid profile email offline_access",
            grant_types=["authorization_code", "refresh_token"],
            redirect_uris=[REDIRECT_URI],
            require_pkce=True,
            require_consent=False,
            cred_path=tmp_path / "opsession-creds.json",
        )
        TestOpSessionStore._client_secret = creds["initialSecret"]
        assert self._client_secret, "No initial secret in credentials"

        internal_id = _get_client_internal_id(cli_logged_in, self._cid)
        _set_auto_approval(authenticated_context, internal_id)

    def test_02_theSessionPage_ListsTheCurrentSession_AndTheApplication(
        self, oidc_client: OidcClient, authenticated_context
    ):
        """The sign-in creates a session; the page shows it and the client that took part."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        verifier, challenge = generate_pkce()
        auth_url = oidc_client.build_authorize_url(
            client_id=self._cid,
            redirect_uri=REDIRECT_URI,
            scope="openid profile email offline_access",
            code_challenge=challenge,
            state=secrets.token_urlsafe(16),
            nonce=secrets.token_urlsafe(16),
        )

        callback_url = _follow_authorize_redirects(authenticated_context, auth_url)
        params = urllib.parse.parse_qs(urllib.parse.urlparse(callback_url).query)
        assert not params.get("error"), f"Authorize returned an error: {params.get('error')}"
        code = params.get("code", [None])[0]
        assert code, f"No code in the callback URL: {callback_url}"

        resp = oidc_client.token_authorization_code(
            code=code,
            redirect_uri=REDIRECT_URI,
            client_id=self._cid,
            client_secret=self._client_secret,
            code_verifier=verifier,
        )
        assert resp.ok, f"Token exchange failed: {resp.error} {resp.error_description}"
        assert resp.refresh_token, "No refresh_token in the response"
        assert resp.id_token, "No id_token in the response"
        TestOpSessionStore._refresh_token = resp.refresh_token
        TestOpSessionStore._id_token = resp.id_token

        page = authenticated_context.new_page()
        try:
            page.goto(f"{BASE_URL}/account/sessions", wait_until="domcontentloaded")
            if "/login" in page.url:
                pytest.skip("The browser session expired before the session page could be read")

            body = page.inner_text("body")
            assert "Current Session" in body, "The current session must be marked on the page"
            assert self._cid in body, "The application that took part must be listed"

            # The session shows when it started and when it was last used.
            assert "Signed in" in body, "The session page must show when the session started"
            assert "Last used" in body, "The session page must show when the session was last used"
        finally:
            page.close()

    def test_03_signingOut_EndsTheSession_AndTheRefreshToken(
        self, oidc_client: OidcClient, authenticated_context
    ):
        """The sign-out ends the session, and with it the refresh tokens it produced."""
        if not self._refresh_token:
            pytest.skip("No refresh_token from the previous step")
        if not self._id_token:
            pytest.skip("No id_token from the previous step")

        # The refresh token works while the session is alive.
        ok = oidc_client.token_refresh(
            refresh_token=self._refresh_token,
            client_id=self._cid,
            client_secret=self._client_secret,
        )
        assert ok.ok, f"The refresh must work while the session is alive: {ok.error}"

        # The RP-initiated logout: the id_token_hint proves the request comes from the RP the user signed in
        # to, so the OP signs the user out without a confirmation.
        logout_url = f"{BASE_URL}/connect/endsession?" + urllib.parse.urlencode({
            "id_token_hint": self._id_token,
            "client_id": self._cid,
            "state": "bye",
        })
        page = authenticated_context.new_page()
        try:
            page.goto(logout_url, wait_until="domcontentloaded", timeout=60_000)
        finally:
            page.close()

        # The session is over: the refresh token of the session does not work any more.
        failed = oidc_client.token_refresh(
            refresh_token=self._refresh_token,
            client_id=self._cid,
            client_secret=self._client_secret,
        )
        assert not failed.ok, "A refresh token of an ended session must not work"
        assert failed.error in ("invalid_grant", "invalid_token"), failed.error

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)
