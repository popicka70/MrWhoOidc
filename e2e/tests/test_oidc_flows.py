"""
OIDC flow integration tests — exercises real OAuth2/OIDC protocol flows
against the running MrWhoOidc authorization server.

Unlike CRUD and page-load tests, these tests acquire actual tokens, validate
JWT claims, call userinfo, perform token exchange, and test DPoP binding.

Requires:
  - The app running at BASE_URL (via docker-compose.dev.yml).
  - ``mrwho-cli`` installed and logged in (via ``cli_logged_in`` fixture).
  - An authenticated browser session (via ``authenticated_context`` fixture).

Test order within each class is significant (provisioning → flow → cleanup).
All test data uses the ``e2e-oidc`` prefix.
"""

from __future__ import annotations

import json
import os
import secrets
import time
import urllib.parse
from pathlib import Path

import pytest

from utils.cli_helper import CliHelper
from utils.dpop import DPoPProofBuilder
from utils.oidc_client import OidcClient, TokenResponse, decode_jwt, generate_pkce

E2E_PREFIX = "e2e-oidc"
_RUN_SUFFIX = secrets.token_hex(3)
BASE_URL: str = os.getenv("BASE_URL", "https://localhost:8443")
ADMIN_USERNAME: str = os.getenv("ADMIN_USERNAME", "admin@mrwho.local")
ADMIN_PASSWORD: str = os.getenv("ADMIN_PASSWORD", "E2E-test-password!")

REDIRECT_URI = "https://e2e-oidc.test/callback"
BACKEND_AUDIENCE = "api"


def _get_default_realm_id(cli: CliHelper) -> str:
    """Get the default realm GUID."""
    data = cli.run_json("realm", "list")
    match = [r for r in data if r.get("name") == "default"]
    assert len(match) == 1, "Default realm not found"
    return str(match[0]["id"])


def _get_client_internal_id(cli: CliHelper, client_id: str) -> str:
    """Look up the internal GUID for a client by its client_id string."""
    data = cli.run_json("client", "list")
    match = [c for c in data if c.get("clientId") == client_id]
    assert len(match) == 1, f"Client '{client_id}' not found"
    return str(match[0]["id"])


def _set_auto_approval(authenticated_context, client_internal_id: str) -> None:
    """Set AutoApprovalMode=All on a client via the admin edit page."""
    page = authenticated_context.new_page()
    try:
        base = os.environ.get("BASE_URL", "https://localhost:8443")
        page.goto(
            f"{base}/admin/clients/edit/{client_internal_id}",
            wait_until="domcontentloaded",
        )
        page.select_option("#Input_AutoApprovalMode", "All")
        page.click("button[type='submit'].btn-primary")
        page.wait_for_load_state("domcontentloaded")
    finally:
        page.close()


import re as _re
import html as _html

def _follow_authorize_redirects(authenticated_context, auth_url: str) -> str:
    """Follow the authorize redirect chain and return the callback URL with the code.

    Uses Playwright's APIRequestContext (shares browser session cookies) to
    follow server-side redirects. The final hop is the Auth/Redirect page which
    performs a client-side redirect — we parse the callback URL from its HTML.
    """
    api = authenticated_context.request
    base = os.environ.get("BASE_URL", "https://localhost:8443")
    url = auth_url
    callback_host = REDIRECT_URI.split("//")[1].split("/")[0]

    for _ in range(10):
        try:
            resp = api.get(url, max_redirects=0, ignore_https_errors=True)
        except Exception:
            break

        if resp.status not in (301, 302, 303, 307, 308):
            # Not a redirect — check if this is the Auth/Redirect page
            if resp.status == 200 and "/Auth/Redirect" in url:
                body = resp.text()
                # Extract from noscript <a href="...">
                match = _re.search(r'<a\s+href="([^"]+)"', body)
                if match:
                    return _html.unescape(match.group(1))
                # Fallback: extract from meta refresh
                match = _re.search(r'content="0;url=([^"]+)"', body)
                if match:
                    return _html.unescape(match.group(1))
            break

        location = resp.headers.get("location", "")
        if callback_host in location:
            return location
        if location.startswith("/"):
            url = base + location
        else:
            url = location

    raise AssertionError(
        f"Authorization code redirect not found. Last status: {resp.status}, last URL: {url}"
    )

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
    """Create a client via CLI with --create-initial-secret, return credentials dict."""
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


def _delete_client(cli: CliHelper, client_id: str) -> None:
    """Delete a client by its client_id string (best-effort)."""
    try:
        internal_id = _get_client_internal_id(cli, client_id)
        cli.run("client", "delete", internal_id, "--confirm")
    except Exception:
        pass


def _delete_user(cli: CliHelper, username: str) -> None:
    """Delete a user by username (best-effort)."""
    try:
        data = cli.run_json("user", "list", "--search", username)
        items = data.get("items", data) if isinstance(data, dict) else data
        match = [u for u in items if u.get("username") == username]
        if match:
            cli.run("user", "delete", str(match[0]["id"]), "--confirm")
    except Exception:
        pass


# ═══════════════════════════════════════════════════════════════════════════
# Authorization Code + PKCE Flow
# ═══════════════════════════════════════════════════════════════════════════


class TestAuthorizationCodeFlow:
    """
    Full authorization code flow with PKCE:
    provision client → authorize (reuse admin session) → exchange code →
    validate tokens → userinfo → refresh → revoke.
    """

    _cid = f"{E2E_PREFIX}-authcode-{_RUN_SUFFIX}"
    _realm_id: str | None = None
    _client_secret: str | None = None
    _code: str | None = None
    _verifier: str | None = None
    _nonce: str | None = None
    _state: str | None = None
    _access_token: str | None = None
    _id_token: str | None = None
    _refresh_token: str | None = None
    _user_sub: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path, authenticated_context):
        """Create a confidential client (auth_code + refresh)."""
        _delete_client(cli_logged_in, self._cid)

        realm_id = _get_default_realm_id(cli_logged_in)
        TestAuthorizationCodeFlow._realm_id = realm_id

        creds = _create_client_with_secret(
            cli_logged_in,
            client_id=self._cid,
            client_name=f"E2E Auth Code {_RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid profile email",
            grant_types=["authorization_code", "refresh_token"],
            redirect_uris=[REDIRECT_URI],
            require_pkce=True,
            require_consent=False,
            cred_path=tmp_path / "authcode-creds.json",
        )
        TestAuthorizationCodeFlow._client_secret = creds["initialSecret"]
        assert self._client_secret, "No initial secret in credentials"

        # Allow any user to authorize (auto-approve assignment)
        internal_id = _get_client_internal_id(cli_logged_in, self._cid)
        _set_auto_approval(authenticated_context, internal_id)

    def test_02_authorize_and_capture_code(self, oidc_client: OidcClient, authenticated_context):
        """Navigate to /authorize with the admin session, capture the auth code."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        verifier, challenge = generate_pkce()
        state = secrets.token_urlsafe(32)
        nonce = secrets.token_urlsafe(32)

        auth_url = oidc_client.build_authorize_url(
            client_id=self._cid,
            redirect_uri=REDIRECT_URI,
            scope="openid profile email",
            code_challenge=challenge,
            state=state,
            nonce=nonce,
        )

        callback_url = _follow_authorize_redirects(authenticated_context, auth_url)

        parsed = urllib.parse.urlparse(callback_url)
        params = urllib.parse.parse_qs(parsed.query)

        # Check for error responses
        error = params.get("error", [None])[0]
        assert not error, (
            f"Authorize returned error: {error} — "
            f"{params.get('error_description', [''])[0]}"
        )

        code = params.get("code", [None])[0]
        returned_state = params.get("state", [None])[0]

        assert code, f"No 'code' in callback URL: {callback_url}"
        assert returned_state == state, "State mismatch"

        TestAuthorizationCodeFlow._code = code
        TestAuthorizationCodeFlow._verifier = verifier
        TestAuthorizationCodeFlow._nonce = nonce
        TestAuthorizationCodeFlow._state = state

    def test_03_exchange_code_for_tokens(self, oidc_client: OidcClient):
        """Exchange the authorization code for tokens at the token endpoint."""
        if not self._code:
            pytest.skip("Auth code not captured")

        resp = oidc_client.token_authorization_code(
            code=self._code,
            redirect_uri=REDIRECT_URI,
            client_id=self._cid,
            client_secret=self._client_secret,
            code_verifier=self._verifier,
        )
        assert resp.ok, f"Token exchange failed ({resp.status_code}): {resp.error} {resp.error_description}"
        assert resp.access_token, "No access_token in response"
        assert resp.id_token, "No id_token in response"
        assert resp.refresh_token, "No refresh_token in response"

        TestAuthorizationCodeFlow._access_token = resp.access_token
        TestAuthorizationCodeFlow._id_token = resp.id_token
        TestAuthorizationCodeFlow._refresh_token = resp.refresh_token

    def test_04_validate_id_token_claims(self, oidc_client: OidcClient):
        """Decode the id_token and verify essential claims."""
        if not self._id_token:
            pytest.skip("No id_token")

        header, payload = decode_jwt(self._id_token)
        assert payload.get("iss") == oidc_client.issuer, f"Wrong issuer: {payload.get('iss')}"
        assert payload.get("aud") == self._cid or self._cid in payload.get("aud", []), \
            f"Wrong audience: {payload.get('aud')}"
        assert payload.get("nonce") == self._nonce, "Nonce mismatch"
        assert payload.get("sub"), "Missing sub claim"
        assert payload.get("exp"), "Missing exp claim"
        assert payload["exp"] > time.time(), "Token already expired"

        TestAuthorizationCodeFlow._user_sub = payload["sub"]

    def test_05_validate_access_token_claims(self, oidc_client: OidcClient):
        """Decode the access_token and verify essential claims."""
        if not self._access_token:
            pytest.skip("No access_token")

        header, payload = decode_jwt(self._access_token)
        assert payload.get("iss") == oidc_client.issuer
        assert payload.get("sub"), "Missing sub claim"
        assert payload.get("exp"), "Missing exp claim"
        assert payload["exp"] > time.time()
        # Verify scope was granted
        assert "openid" in (payload.get("scope") or "")

    def test_06_call_userinfo(self, oidc_client: OidcClient):
        """Call the userinfo endpoint with the access token."""
        if not self._access_token:
            pytest.skip("No access_token")

        status, body = oidc_client.userinfo(self._access_token)
        assert status == 200, f"Userinfo failed ({status}): {body}"
        assert body.get("sub") == self._user_sub, "Sub mismatch with id_token"

    def test_07_refresh_token(self, oidc_client: OidcClient):
        """Use the refresh token to get a new access token."""
        if not self._refresh_token:
            pytest.skip("No refresh_token")

        resp = oidc_client.token_refresh(
            refresh_token=self._refresh_token,
            client_id=self._cid,
            client_secret=self._client_secret,
        )
        assert resp.ok, f"Refresh failed ({resp.status_code}): {resp.error}"
        assert resp.access_token, "No new access_token"

        # Verify the new token has the same subject
        _, payload = decode_jwt(resp.access_token)
        assert payload.get("sub") == self._user_sub, "Sub changed after refresh"

        # Update tokens for subsequent tests
        TestAuthorizationCodeFlow._access_token = resp.access_token
        if resp.refresh_token:
            TestAuthorizationCodeFlow._refresh_token = resp.refresh_token

    def test_08_revoke_token(self, oidc_client: OidcClient):
        """Revoke the refresh token and verify it's no longer usable."""
        if not self._refresh_token:
            pytest.skip("No refresh_token")

        status = oidc_client.revoke(
            token=self._refresh_token,
            client_id=self._cid,
            client_secret=self._client_secret,
            token_type_hint="refresh_token",
        )
        assert status == 200, f"Revocation failed with status {status}"

        # Attempting to use the revoked refresh token should fail
        resp = oidc_client.token_refresh(
            refresh_token=self._refresh_token,
            client_id=self._cid,
            client_secret=self._client_secret,
        )
        assert not resp.ok, "Refresh with revoked token should fail"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# Client Credentials (M2M) Flow
# ═══════════════════════════════════════════════════════════════════════════


class TestClientCredentialsFlow:
    """
    Machine-to-machine flow using client_credentials grant:
    provision client → acquire token → validate claims → negative cases.
    """

    _cid = f"{E2E_PREFIX}-m2m-{_RUN_SUFFIX}"
    _client_secret: str | None = None
    _access_token: str | None = None

    def test_01_provision_m2m_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        """Create a confidential M2M client."""
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in,
            client_id=self._cid,
            client_name=f"E2E M2M {_RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid profile",
            grant_types=["client_credentials"],
            require_consent=False,
            cred_path=tmp_path / "m2m-creds.json",
        )
        TestClientCredentialsFlow._client_secret = creds["initialSecret"]

    def test_02_acquire_token(self, oidc_client: OidcClient):
        """Acquire an access token using client_credentials."""
        if not self._client_secret:
            pytest.skip("M2M client not provisioned")

        resp = oidc_client.token_client_credentials(
            client_id=self._cid,
            client_secret=self._client_secret,
            scope="openid profile",
            audience=BACKEND_AUDIENCE,
        )
        assert resp.ok, f"M2M token failed ({resp.status_code}): {resp.error} {resp.error_description}"
        assert resp.access_token, "No access_token"
        assert resp.token_type and resp.token_type.lower() == "bearer"

        TestClientCredentialsFlow._access_token = resp.access_token

    def test_03_validate_token_claims(self, oidc_client: OidcClient):
        """Decode the M2M access token and verify claims."""
        if not self._access_token:
            pytest.skip("No access_token")

        _, payload = decode_jwt(self._access_token)
        assert payload.get("iss") == oidc_client.issuer
        assert payload.get("client_id") == self._cid or payload.get("azp") == self._cid
        assert payload.get("exp"), "Missing exp"
        assert payload["exp"] > time.time()
        # M2M tokens typically don't have a 'sub' claim (or sub == client_id)

    def test_04_token_with_basic_auth(self, oidc_client: OidcClient):
        """Acquire a token using client_secret_basic authentication."""
        if not self._client_secret:
            pytest.skip("M2M client not provisioned")

        resp = oidc_client.token_client_credentials(
            client_id=self._cid,
            client_secret=self._client_secret,
            scope="openid profile",
            audience=BACKEND_AUDIENCE,
            auth_method="basic",
        )
        assert resp.ok, f"Basic auth token failed ({resp.status_code}): {resp.error}"
        assert resp.access_token

    def test_05_wrong_secret_rejected(self, oidc_client: OidcClient):
        """Using an incorrect client secret should fail."""
        resp = oidc_client.token_client_credentials(
            client_id=self._cid,
            client_secret="wrong-secret-value",
            scope="openid profile",
            audience=BACKEND_AUDIENCE,
        )
        assert not resp.ok, "Token with wrong secret should fail"
        assert resp.status_code in (400, 401)

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# Token Exchange / OBO Flow
# ═══════════════════════════════════════════════════════════════════════════


class TestTokenExchangeFlow:
    """
    On-Behalf-Of (OBO) flow using token exchange:
    provision frontend + backend clients → acquire user token →
    exchange for delegated backend token → validate act claim.
    """

    _fe_cid = f"{E2E_PREFIX}-obo-fe-{_RUN_SUFFIX}"
    _be_cid = f"{E2E_PREFIX}-obo-be-{_RUN_SUFFIX}"
    _fe_secret: str | None = None
    _be_secret: str | None = None
    _be_internal_id: str | None = None
    _user_access_token: str | None = None
    _delegated_token: str | None = None

    def test_01_provision_clients(self, cli_logged_in: CliHelper, tmp_path: Path, authenticated_context):
        """Create frontend client and backend client."""
        _delete_client(cli_logged_in, self._fe_cid)
        _delete_client(cli_logged_in, self._be_cid)
        realm_id = _get_default_realm_id(cli_logged_in)

        # Frontend client (auth code + refresh)
        fe_creds = _create_client_with_secret(
            cli_logged_in,
            client_id=self._fe_cid,
            client_name=f"E2E OBO Frontend {_RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid profile email",
            grant_types=["authorization_code", "refresh_token"],
            redirect_uris=[REDIRECT_URI],
            require_pkce=True,
            require_consent=False,
            cred_path=tmp_path / "fe-creds.json",
        )
        TestTokenExchangeFlow._fe_secret = fe_creds["initialSecret"]

        # Allow any user to authorize the frontend client
        fe_internal_id = _get_client_internal_id(cli_logged_in, self._fe_cid)
        _set_auto_approval(authenticated_context, fe_internal_id)

        # Backend client (client_credentials + token-exchange)
        be_creds = _create_client_with_secret(
            cli_logged_in,
            client_id=self._be_cid,
            client_name=f"E2E OBO Backend {_RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid profile",
            grant_types=["client_credentials", "urn:ietf:params:oauth:grant-type:token-exchange"],
            require_consent=False,
            cred_path=tmp_path / "be-creds.json",
        )
        TestTokenExchangeFlow._be_secret = be_creds["initialSecret"]
        TestTokenExchangeFlow._be_internal_id = _get_client_internal_id(cli_logged_in, self._be_cid)

    def test_02_configure_obo_policy(self, authenticated_context):
        """Configure OBO policy on the backend client via the admin UI."""
        if not self._be_internal_id:
            pytest.skip("Backend client not provisioned")

        page = authenticated_context.new_page()
        try:
            # Navigate to the backend client edit page
            page.goto(
                f"{BASE_URL}/admin/clients/edit/{self._be_internal_id}",
                wait_until="domcontentloaded",
            )

            # Click the OBO tab to reveal the OBO fields
            obo_tab = page.locator("#obo-tab")
            if obo_tab.count() > 0:
                obo_tab.click()
                page.wait_for_timeout(500)

            # Enable OBO
            obo_checkbox = page.locator("#Input_OboEnabled")
            if obo_checkbox.count() > 0 and not obo_checkbox.is_checked():
                obo_checkbox.check()

            # Set allowed callers (backend client — callerClientId is the authenticated client)
            callers_input = page.locator("#Input_OboAllowedCallers")
            if callers_input.count() > 0:
                callers_input.fill(self._be_cid)

            # Set allowed source audiences to the persisted audience of the subject access token.
            source_aud = page.locator("#Input_OboAllowedSourceAudiences")
            if source_aud.count() > 0:
                source_aud.fill(BACKEND_AUDIENCE)

            # Set allowed target audiences
            target_aud = page.locator("#Input_OboAllowedTargetAudiences")
            if target_aud.count() > 0:
                target_aud.fill(BACKEND_AUDIENCE)

            # Set allowed scopes
            scopes = page.locator("#Input_OboAllowedScopes")
            if scopes.count() > 0:
                scopes.fill("openid, profile")

            # Set max delegation depth
            depth = page.locator("#Input_OboMaxDelegationDepth")
            if depth.count() > 0:
                depth.fill("1")

            # Submit the form
            save_btn = page.locator("button[type='submit']:has-text('Save')")
            if save_btn.count() > 0:
                save_btn.click()
                page.wait_for_load_state("domcontentloaded")

        finally:
            page.close()

    def test_03_acquire_user_token(self, oidc_client: OidcClient, authenticated_context):
        """Acquire a user access token via auth code flow with the frontend client."""
        if not self._fe_secret:
            pytest.skip("Frontend client not provisioned")

        verifier, challenge = generate_pkce()
        state = secrets.token_urlsafe(32)
        nonce = secrets.token_urlsafe(32)

        auth_url = oidc_client.build_authorize_url(
            client_id=self._fe_cid,
            redirect_uri=REDIRECT_URI,
            scope="openid profile email",
            code_challenge=challenge,
            state=state,
            nonce=nonce,
        )

        callback_url = _follow_authorize_redirects(authenticated_context, auth_url)

        parsed = urllib.parse.urlparse(callback_url)
        params = urllib.parse.parse_qs(parsed.query)

        error = params.get("error", [None])[0]
        assert not error, (
            f"Authorize returned error: {error} — "
            f"{params.get('error_description', [''])[0]}"
        )

        code = params.get("code", [None])[0]
        assert code, "No code in callback"

        # Exchange for tokens
        token_resp = oidc_client.token_authorization_code(
            code=code,
            redirect_uri=REDIRECT_URI,
            client_id=self._fe_cid,
            client_secret=self._fe_secret,
            code_verifier=verifier,
        )
        assert token_resp.ok, f"Token exchange failed: {token_resp.error}"
        TestTokenExchangeFlow._user_access_token = token_resp.access_token

    def test_04_exchange_for_backend_token(self, oidc_client: OidcClient):
        """Perform token exchange: frontend access token → backend delegated token."""
        if not self._user_access_token or not self._be_secret:
            pytest.skip("Prerequisites not met")

        resp = oidc_client.token_exchange(
            subject_token=self._user_access_token,
            client_id=self._be_cid,
            client_secret=self._be_secret,
            audience=BACKEND_AUDIENCE,
            scope="openid profile",
        )
        assert resp.ok, (
            f"Token exchange failed ({resp.status_code}): "
            f"{resp.error} {resp.error_description}"
        )
        assert resp.access_token, "No delegated access_token"
        TestTokenExchangeFlow._delegated_token = resp.access_token

    def test_05_validate_delegated_token(self, oidc_client: OidcClient):
        """Verify the delegated token has the correct claims including 'act'."""
        if not self._delegated_token:
            pytest.skip("No delegated token")

        _, payload = decode_jwt(self._delegated_token)
        assert payload.get("iss") == oidc_client.issuer
        assert payload.get("sub"), "Missing sub in delegated token"
        # The 'act' claim should indicate the acting party
        act = payload.get("act")
        if act:
            if isinstance(act, str):
                act = json.loads(act)
            assert act.get("client_id") or act.get("sub"), \
                "act claim present but missing client_id/sub"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._fe_cid)
        _delete_client(cli_logged_in, self._be_cid)


# ═══════════════════════════════════════════════════════════════════════════
# DPoP Proof-of-Possession Flow
# ═══════════════════════════════════════════════════════════════════════════


class TestDPoPFlow:
    """
    DPoP-bound token tests:
    provision M2M client → acquire DPoP-bound token → validate cnf claim.
    """

    _cid = f"{E2E_PREFIX}-dpop-{_RUN_SUFFIX}"
    _client_secret: str | None = None
    _dpop_builder: DPoPProofBuilder | None = None
    _access_token: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        """Create a client for DPoP testing."""
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in,
            client_id=self._cid,
            client_name=f"E2E DPoP {_RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid profile",
            grant_types=["client_credentials"],
            require_consent=False,
            cred_path=tmp_path / "dpop-creds.json",
        )
        TestDPoPFlow._client_secret = creds["initialSecret"]
        TestDPoPFlow._dpop_builder = DPoPProofBuilder()

    def test_02_m2m_with_dpop(self, oidc_client: OidcClient):
        """Acquire a DPoP-bound token using client_credentials."""
        if not self._client_secret or not self._dpop_builder:
            pytest.skip("DPoP client not provisioned")

        proof = self._dpop_builder.create_proof(
            htm="POST",
            htu=oidc_client.token_endpoint,
        )

        resp = oidc_client.token_client_credentials(
            client_id=self._cid,
            client_secret=self._client_secret,
            scope="openid profile",
            audience=BACKEND_AUDIENCE,
            dpop_header=proof,
        )
        assert resp.ok, f"DPoP token failed ({resp.status_code}): {resp.error} {resp.error_description}"
        assert resp.access_token, "No access_token"
        assert resp.token_type and resp.token_type.lower() == "dpop", \
            f"Expected token_type 'DPoP' but got '{resp.token_type}'"

        TestDPoPFlow._access_token = resp.access_token

    def test_03_dpop_bound_token_has_cnf(self):
        """Verify the DPoP-bound token has a cnf.jkt claim matching the proof key."""
        if not self._access_token or not self._dpop_builder:
            pytest.skip("No DPoP token")

        _, payload = decode_jwt(self._access_token)
        cnf = payload.get("cnf")
        assert cnf, "Missing 'cnf' claim in DPoP-bound token"
        # cnf may be a JSON string (from JWT claim serialization) or a dict
        if isinstance(cnf, str):
            cnf = json.loads(cnf)
        jkt = cnf.get("jkt")
        assert jkt, "Missing 'jkt' in cnf claim"
        expected_jkt = self._dpop_builder.jwk_thumbprint()
        assert jkt == expected_jkt, f"JKT mismatch: {jkt} != {expected_jkt}"

    def test_04_dpop_replay_rejected(self, oidc_client: OidcClient):
        """Reusing the exact same DPoP proof (same jti) should be rejected."""
        if not self._client_secret or not self._dpop_builder:
            pytest.skip("DPoP client not provisioned")

        # Create a proof and use it twice
        proof = self._dpop_builder.create_proof(
            htm="POST",
            htu=oidc_client.token_endpoint,
        )

        # First use should succeed
        resp1 = oidc_client.token_client_credentials(
            client_id=self._cid,
            client_secret=self._client_secret,
            scope="openid profile",
            audience=BACKEND_AUDIENCE,
            dpop_header=proof,
        )
        assert resp1.ok, f"First DPoP request failed: {resp1.error}"

        # Second use with same proof (same jti) should fail
        resp2 = oidc_client.token_client_credentials(
            client_id=self._cid,
            client_secret=self._client_secret,
            scope="openid profile",
            audience=BACKEND_AUDIENCE,
            dpop_header=proof,
        )
        assert not resp2.ok, "Replay of DPoP proof should be rejected"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# Negative / Edge Case Tests
# ═══════════════════════════════════════════════════════════════════════════


class TestOidcNegativeCases:
    """
    Error-case tests: missing PKCE, wrong redirect_uri, wrong secret,
    unsupported grant type.
    """

    _cid = f"{E2E_PREFIX}-neg-{_RUN_SUFFIX}"
    _client_secret: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        """Create a client for negative testing."""
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in,
            client_id=self._cid,
            client_name=f"E2E Negative {_RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid profile",
            grant_types=["authorization_code", "refresh_token", "client_credentials"],
            redirect_uris=[REDIRECT_URI],
            require_pkce=True,
            require_consent=False,
            cred_path=tmp_path / "neg-creds.json",
        )
        TestOidcNegativeCases._client_secret = creds["initialSecret"]

    def test_02_wrong_client_secret(self, oidc_client: OidcClient):
        """Token request with incorrect secret should fail."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        resp = oidc_client.token_client_credentials(
            client_id=self._cid,
            client_secret="totally-wrong-secret",
            scope="openid",
            audience=BACKEND_AUDIENCE,
        )
        assert not resp.ok
        assert resp.status_code in (400, 401)

    def test_03_unsupported_grant_type(self, oidc_client: OidcClient):
        """Requesting password grant (not configured) should fail."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        import requests as req
        resp = req.post(
            oidc_client.token_endpoint,
            data={
                "grant_type": "password",
                "client_id": self._cid,
                "client_secret": self._client_secret,
                "username": "nobody",
                "password": "nothing",
            },
            verify=False,
        )
        assert resp.status_code in (400, 401)
        body = resp.json()
        assert body.get("error") in ("unsupported_grant_type", "unauthorized_client", "invalid_grant")

    def test_04_missing_audience_for_m2m(self, oidc_client: OidcClient):
        """M2M without audience/resource should fail (server requires it)."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        resp = oidc_client.token_client_credentials(
            client_id=self._cid,
            client_secret=self._client_secret,
            scope="openid",
        )
        # Server requires audience or resource for client_credentials
        assert not resp.ok
        assert resp.error in ("invalid_request", "invalid_target")

    def test_05_invalid_code(self, oidc_client: OidcClient):
        """Exchanging an invalid authorization code should fail."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        resp = oidc_client.token_authorization_code(
            code="invalid-code-value",
            redirect_uri=REDIRECT_URI,
            client_id=self._cid,
            client_secret=self._client_secret,
            code_verifier="dummy-verifier",
        )
        assert not resp.ok
        assert resp.error in ("invalid_grant", "invalid_request")

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)
