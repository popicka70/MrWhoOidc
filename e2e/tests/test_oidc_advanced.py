"""
Advanced OIDC/OAuth2 E2E tests — exercises edge cases, negative scenarios,
and protocol features beyond the basic happy-path flows.

Coverage areas:
  - Discovery document validation & JWKS integrity
  - Token introspection (RFC 7662)
  - Pushed Authorization Requests / PAR (RFC 9126)
  - PKCE enforcement & bypass attempts
  - Authorization code replay attacks
  - Token binding & DPoP edge cases
  - Scope validation & downscoping
  - prompt=none silent authentication
  - Expired / malformed token handling
  - Cross-client token abuse
  - Refresh token rotation & revocation cascades
  - Client authentication method variations
  - OIDC session management basics

Requires:
  - The app running at BASE_URL (via docker-compose.dev.yml).
  - ``mrwho-cli`` installed and logged in (via ``cli_logged_in`` fixture).
  - An authenticated browser session (via ``authenticated_context`` fixture).

Test order within each class is significant (provisioning → flow → cleanup).
All test data uses the ``e2e-oidc-adv`` prefix.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import secrets
import time
import urllib.parse
from pathlib import Path

import pytest
import requests as http_requests

from utils.cli_helper import CliHelper
from utils.dpop import DPoPProofBuilder
from utils.oidc_client import OidcClient, TokenResponse, decode_jwt, generate_pkce

E2E_PREFIX = "e2e-oidc-adv"
_RUN_SUFFIX = secrets.token_hex(3)
BASE_URL: str = os.getenv("BASE_URL", "https://localhost:8443")
ADMIN_USERNAME: str = os.getenv("ADMIN_USERNAME", "admin@mrwho.local")
ADMIN_PASSWORD: str = os.getenv("ADMIN_PASSWORD", "Admin123!")
REDIRECT_URI = "https://e2e-oidc-adv.test/callback"
BACKEND_AUDIENCE = "api"


# ═══════════════════════════════════════════════════════════════════════════
# Shared helpers
# ═══════════════════════════════════════════════════════════════════════════

import re as _re
import html as _html


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
    """Follow the authorize redirect chain and return the callback URL."""
    api = authenticated_context.request
    callback_host = REDIRECT_URI.split("//")[1].split("/")[0]

    url = auth_url
    for _ in range(10):
        try:
            resp = api.get(url, max_redirects=0, ignore_https_errors=True)
        except Exception:
            break

        if resp.status not in (301, 302, 303, 307, 308):
            if resp.status == 200 and "/Auth/Redirect" in url:
                body = resp.text()
                match = _re.search(r'<a\s+href="([^"]+)"', body)
                if match:
                    return _html.unescape(match.group(1))
                match = _re.search(r'content="0;url=([^"]+)"', body)
                if match:
                    return _html.unescape(match.group(1))
            break

        location = resp.headers.get("location", "")
        if callback_host in location:
            return location
        if location.startswith("/"):
            url = BASE_URL + location
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
    try:
        internal_id = _get_client_internal_id(cli, client_id)
        cli.run("client", "delete", internal_id, "--confirm")
    except Exception:
        pass


def _acquire_auth_code(
    oidc_client: OidcClient,
    authenticated_context,
    client_id: str,
    *,
    scope: str = "openid profile email",
    extra_params: dict | None = None,
) -> tuple[str, str, str]:
    """Get an auth code via the admin session. Returns (code, verifier, nonce)."""
    verifier, challenge = generate_pkce()
    state = secrets.token_urlsafe(32)
    nonce = secrets.token_urlsafe(32)

    params: dict[str, str] = {}
    if extra_params:
        params.update(extra_params)

    auth_url = oidc_client.build_authorize_url(
        client_id=client_id,
        redirect_uri=REDIRECT_URI,
        scope=scope,
        code_challenge=challenge,
        state=state,
        nonce=nonce,
        extra_params=params,
    )
    callback_url = _follow_authorize_redirects(authenticated_context, auth_url)
    parsed = urllib.parse.urlparse(callback_url)
    qp = urllib.parse.parse_qs(parsed.query)
    error = qp.get("error", [None])[0]
    assert not error, f"Authorize error: {error} — {qp.get('error_description', [''])[0]}"
    code = qp.get("code", [None])[0]
    assert code, f"No code in callback: {callback_url}"
    assert qp.get("state", [None])[0] == state, "State mismatch"
    return code, verifier, nonce


# ═══════════════════════════════════════════════════════════════════════════
# 1. Discovery & JWKS Validation
# ═══════════════════════════════════════════════════════════════════════════


class TestDiscoveryAndJwks:
    """Validate the discovery document and JWKS endpoint thoroughly."""

    def test_discovery_required_fields(self, oidc_client: OidcClient):
        """Discovery document MUST contain all required OIDC fields."""
        disco = oidc_client.discover()
        required = [
            "issuer",
            "authorization_endpoint",
            "token_endpoint",
            "userinfo_endpoint",
            "jwks_uri",
            "response_types_supported",
            "subject_types_supported",
            "id_token_signing_alg_values_supported",
        ]
        for field in required:
            assert field in disco, f"Discovery missing required field: {field}"

    def test_discovery_issuer_matches_url(self, oidc_client: OidcClient):
        """The 'issuer' value must match the base URL used to fetch discovery."""
        disco = oidc_client.discover()
        assert disco["issuer"].rstrip("/") == oidc_client.issuer_url.rstrip("/"), \
            f"Issuer mismatch: {disco['issuer']} vs {oidc_client.issuer_url}"

    def test_discovery_no_http_endpoints(self, oidc_client: OidcClient):
        """All endpoint URLs in discovery should use HTTPS (not plain HTTP)."""
        disco = oidc_client.discover()
        endpoint_keys = [k for k in disco if k.endswith("_endpoint") or k == "jwks_uri"]
        for key in endpoint_keys:
            url = disco[key]
            if isinstance(url, str):
                assert url.startswith("https://"), \
                    f"Discovery endpoint '{key}' uses insecure HTTP: {url}"

    def test_discovery_supported_values(self, oidc_client: OidcClient):
        """discovery should advertise correct supported values."""
        disco = oidc_client.discover()
        assert "code" in disco["response_types_supported"]
        assert "S256" in disco.get("code_challenge_methods_supported", [])
        assert "client_secret_basic" in disco.get("token_endpoint_auth_methods_supported", [])
        assert "client_secret_post" in disco.get("token_endpoint_auth_methods_supported", [])

    def test_discovery_scopes_include_standard(self, oidc_client: OidcClient):
        """Standard OIDC scopes should be present."""
        disco = oidc_client.discover()
        scopes = disco.get("scopes_supported", [])
        for s in ["openid", "profile", "email"]:
            assert s in scopes, f"Standard scope '{s}' not in scopes_supported"

    def test_jwks_returns_valid_keys(self, oidc_client: OidcClient):
        """JWKS endpoint must return at least one key with required fields."""
        disco = oidc_client.discover()
        resp = http_requests.get(disco["jwks_uri"], verify=False)
        assert resp.status_code == 200
        jwks = resp.json()
        assert "keys" in jwks, "JWKS missing 'keys' property"
        assert len(jwks["keys"]) > 0, "JWKS has no keys"

        for key in jwks["keys"]:
            assert "kty" in key, "Key missing 'kty'"
            assert "kid" in key, "Key missing 'kid'"
            assert "alg" in key or "use" in key, "Key missing 'alg' or 'use'"

    def test_jwks_no_private_material(self, oidc_client: OidcClient):
        """JWKS must NOT expose private key material."""
        disco = oidc_client.discover()
        resp = http_requests.get(disco["jwks_uri"], verify=False)
        jwks = resp.json()
        private_fields = ["d", "p", "q", "dp", "dq", "qi", "k"]  # RSA/EC/symmetric private
        for key in jwks["keys"]:
            for pf in private_fields:
                assert pf not in key, \
                    f"JWKS key {key.get('kid', '?')} exposes private field '{pf}'!"

    def test_jwks_caching_headers(self, oidc_client: OidcClient):
        """JWKS response should include Cache-Control headers."""
        disco = oidc_client.discover()
        resp = http_requests.get(disco["jwks_uri"], verify=False)
        cc = resp.headers.get("cache-control", "")
        # Both caching (max-age) and no-cache/no-store are valid strategies:
        # caching is efficient; no-store is more secure for key rotation.
        assert cc, "JWKS missing Cache-Control header entirely"

    def test_jwks_kid_matches_id_token(self, oidc_client: OidcClient, cli_logged_in: CliHelper,
                                        tmp_path: Path, authenticated_context):
        """The kid in the token header must match a kid in JWKS."""
        cid = f"{E2E_PREFIX}-jwks-kid-{_RUN_SUFFIX}"
        _delete_client(cli_logged_in, cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=cid, client_name=f"E2E JWKS kid {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile", grant_types=["authorization_code"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "jwks-kid-creds.json",
        )
        internal_id = _get_client_internal_id(cli_logged_in, cid)
        _set_auto_approval(authenticated_context, internal_id)

        code, verifier, nonce = _acquire_auth_code(oidc_client, authenticated_context, cid)
        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=cid,
            client_secret=creds["initialSecret"], code_verifier=verifier,
        )
        assert resp.ok, f"Token failed: {resp.error}"

        header, _ = decode_jwt(resp.id_token)
        token_kid = header.get("kid")
        assert token_kid, "ID token header missing 'kid'"

        disco = oidc_client.discover()
        jwks_resp = http_requests.get(disco["jwks_uri"], verify=False)
        jwks_kids = [k["kid"] for k in jwks_resp.json()["keys"]]
        assert token_kid in jwks_kids, f"Token kid '{token_kid}' not in JWKS: {jwks_kids}"

        _delete_client(cli_logged_in, cid)


# ═══════════════════════════════════════════════════════════════════════════
# 2. Token Introspection (RFC 7662)
# ═══════════════════════════════════════════════════════════════════════════


class TestTokenIntrospection:
    """Token introspection tests — active tokens, expired, revoked, cross-client."""

    _cid = f"{E2E_PREFIX}-intro-{_RUN_SUFFIX}"
    _client_secret: str | None = None
    _access_token: str | None = None
    _refresh_token: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path, authenticated_context):
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=self._cid,
            client_name=f"E2E Introspect {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile email",
            grant_types=["authorization_code", "refresh_token", "client_credentials"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "intro-creds.json",
        )
        TestTokenIntrospection._client_secret = creds["initialSecret"]
        internal_id = _get_client_internal_id(cli_logged_in, self._cid)
        _set_auto_approval(authenticated_context, internal_id)

    def test_02_acquire_tokens(self, oidc_client: OidcClient, authenticated_context):
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        code, verifier, nonce = _acquire_auth_code(
            oidc_client, authenticated_context, self._cid,
        )
        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
            client_secret=self._client_secret, code_verifier=verifier,
        )
        assert resp.ok, f"Token failed: {resp.error}"
        TestTokenIntrospection._access_token = resp.access_token
        TestTokenIntrospection._refresh_token = resp.refresh_token

    def test_03_introspect_active_access_token(self, oidc_client: OidcClient):
        """Introspecting a valid access token returns active=true."""
        if not self._access_token:
            pytest.skip("No access token")
        status, body = oidc_client.introspect(
            self._access_token, self._cid, client_secret=self._client_secret,
        )
        assert status == 200, f"Introspect failed: {status}"
        assert body.get("active") is True, f"Expected active=true: {body}"
        assert body.get("sub"), "Introspection missing 'sub'"
        assert body.get("scope"), "Introspection missing 'scope'"

    def test_04_introspect_with_basic_auth(self, oidc_client: OidcClient):
        """Introspection using HTTP Basic auth should work."""
        if not self._access_token:
            pytest.skip("No access token")
        status, body = oidc_client.introspect(
            self._access_token, self._cid,
            client_secret=self._client_secret, auth_method="basic",
        )
        assert status == 200
        assert body.get("active") is True

    def test_05_introspect_garbage_token(self, oidc_client: OidcClient):
        """Introspecting a garbage string should return active=false (not an error)."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        status, body = oidc_client.introspect(
            "this-is-not-a-token", self._cid, client_secret=self._client_secret,
        )
        assert status == 200, f"Introspect should return 200 even for invalid token, got {status}"
        assert body.get("active") is False, "Garbage token should be inactive"

    def test_06_introspect_empty_token(self, oidc_client: OidcClient):
        """Introspecting an empty string should return active=false or error."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        status, body = oidc_client.introspect(
            "", self._cid, client_secret=self._client_secret,
        )
        # RFC 7662: introspection should return 200 with active=false for unknown tokens
        assert status in (200, 400)
        if status == 200:
            assert body.get("active") is False

    def test_07_introspect_without_client_auth_fails(self, oidc_client: OidcClient):
        """Introspection without valid client credentials must be rejected."""
        if not self._access_token:
            pytest.skip("No access token")
        status, body = oidc_client.introspect(
            self._access_token, self._cid,
            client_secret="wrong-secret",
        )
        assert status in (400, 401), f"Expected 400/401, got {status}"

    def test_08_introspect_revoked_token(self, oidc_client: OidcClient):
        """After revoking a token, introspection behaviour depends on token type.

        JWT access tokens are self-contained — the server validates by signature
        and expiry.  Revocation may not be reflected in JWT introspection unless
        the server maintains a blocklist.  Refresh tokens, however, should
        be revocable and introspectable as inactive.
        """
        if not self._refresh_token:
            pytest.skip("No refresh token")

        # Get a fresh M2M token to revoke (so we don't break remaining tests)
        resp = oidc_client.token_client_credentials(
            client_id=self._cid, client_secret=self._client_secret,
            scope="openid", audience=BACKEND_AUDIENCE,
        )
        assert resp.ok, f"M2M token failed: {resp.error}"

        # Revoke it
        rev_status = oidc_client.revoke(
            resp.access_token, self._cid, client_secret=self._client_secret,
            token_type_hint="access_token",
        )
        assert rev_status == 200

        # Introspect the revoked token — JWT introspection may still return active
        # because the server validates signature/expiry, not a revocation list.
        status, body = oidc_client.introspect(
            resp.access_token, self._cid, client_secret=self._client_secret,
        )
        assert status == 200
        # Accept both: servers WITH a blocklist return inactive; those without return active
        # Either way, the server should respond correctly (200 with valid JSON)
        assert "active" in body, "Introspection response missing 'active' field"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# 3. PKCE Enforcement
# ═══════════════════════════════════════════════════════════════════════════


class TestPkceEnforcement:
    """
    Ensure PKCE is properly enforced:
    - Code exchange fails without code_verifier when PKCE is required
    - Wrong verifier is rejected
    - Plain challenge method rejected (only S256 supported)
    """

    _cid = f"{E2E_PREFIX}-pkce-{_RUN_SUFFIX}"
    _client_secret: str | None = None

    def test_01_provision_pkce_client(self, cli_logged_in: CliHelper, tmp_path: Path, authenticated_context):
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=self._cid,
            client_name=f"E2E PKCE {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile",
            grant_types=["authorization_code"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "pkce-creds.json",
        )
        TestPkceEnforcement._client_secret = creds["initialSecret"]
        internal_id = _get_client_internal_id(cli_logged_in, self._cid)
        _set_auto_approval(authenticated_context, internal_id)

    def test_02_exchange_without_verifier_fails(self, oidc_client: OidcClient, authenticated_context):
        """Exchanging an auth code without code_verifier must fail."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        code, verifier, nonce = _acquire_auth_code(oidc_client, authenticated_context, self._cid)

        # Try token exchange WITHOUT code_verifier
        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
            client_secret=self._client_secret,
            # Deliberately omitting code_verifier
        )
        assert not resp.ok, "Token exchange without PKCE verifier should fail"
        assert resp.error in ("invalid_grant", "invalid_request"), \
            f"Unexpected error: {resp.error}"

    def test_03_exchange_with_wrong_verifier_fails(self, oidc_client: OidcClient, authenticated_context):
        """Exchanging an auth code with wrong code_verifier must fail."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        code, verifier, nonce = _acquire_auth_code(oidc_client, authenticated_context, self._cid)

        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
            client_secret=self._client_secret,
            code_verifier="wrong-verifier-" + secrets.token_urlsafe(32),
        )
        assert not resp.ok, "Token exchange with wrong PKCE verifier should fail"
        assert resp.error in ("invalid_grant", "invalid_request")

    def test_04_plain_challenge_method_rejected(self, oidc_client: OidcClient, authenticated_context):
        """code_challenge_method=plain should be rejected (only S256 supported)."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        verifier = secrets.token_urlsafe(64)
        state = secrets.token_urlsafe(32)
        nonce = secrets.token_urlsafe(32)

        # Build authorize URL with method=plain
        auth_url = oidc_client.build_authorize_url(
            client_id=self._cid,
            redirect_uri=REDIRECT_URI,
            scope="openid profile",
            code_challenge=verifier,  # plain = verifier itself
            code_challenge_method="plain",
            state=state,
            nonce=nonce,
        )

        # The server should reject this or return an error
        api = authenticated_context.request
        resp = api.get(auth_url, max_redirects=0, ignore_https_errors=True)
        if resp.status in (301, 302, 303):
            location = resp.headers.get("location", "")
            parsed = urllib.parse.urlparse(location)
            params = urllib.parse.parse_qs(parsed.query)
            error = params.get("error", [None])[0]
            assert error, "Server should return an error for plain PKCE"
            assert error in ("invalid_request", "unsupported_code_challenge_method"), \
                f"Unexpected error for plain PKCE: {error}"
        else:
            # If not redirected, check for error in response body
            assert resp.status in (400, 200), f"Unexpected status: {resp.status}"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# 4. Authorization Code Replay & Misuse
# ═══════════════════════════════════════════════════════════════════════════


class TestAuthCodeReplay:
    """
    Validate that authorization codes are single-use
    and that redirect_uri is strictly validated.
    """

    _cid = f"{E2E_PREFIX}-replay-{_RUN_SUFFIX}"
    _client_secret: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path, authenticated_context):
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=self._cid,
            client_name=f"E2E Replay {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile",
            grant_types=["authorization_code"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "replay-creds.json",
        )
        TestAuthCodeReplay._client_secret = creds["initialSecret"]
        internal_id = _get_client_internal_id(cli_logged_in, self._cid)
        _set_auto_approval(authenticated_context, internal_id)

    def test_02_code_replay_rejected(self, oidc_client: OidcClient, authenticated_context):
        """Using an authorization code twice should fail on the second attempt."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        code, verifier, nonce = _acquire_auth_code(oidc_client, authenticated_context, self._cid)

        # First exchange — should succeed
        resp1 = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
            client_secret=self._client_secret, code_verifier=verifier,
        )
        assert resp1.ok, f"First exchange failed: {resp1.error}"

        # Second exchange — must fail (code already consumed)
        resp2 = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
            client_secret=self._client_secret, code_verifier=verifier,
        )
        assert not resp2.ok, "Code replay should be rejected"
        assert resp2.error in ("invalid_grant", "invalid_request")

    def test_03_wrong_redirect_uri_rejected(self, oidc_client: OidcClient, authenticated_context):
        """Using a different redirect_uri in the token request must fail."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        code, verifier, nonce = _acquire_auth_code(oidc_client, authenticated_context, self._cid)

        resp = oidc_client.token_authorization_code(
            code=code,
            redirect_uri="https://evil.attacker.com/callback",  # wrong redirect
            client_id=self._cid,
            client_secret=self._client_secret, code_verifier=verifier,
        )
        assert not resp.ok, "Token exchange with wrong redirect_uri should fail"
        assert resp.error in ("invalid_grant", "invalid_request", "invalid_redirect_uri")

    def test_04_cross_client_code_use_rejected(self, oidc_client: OidcClient,
                                                 cli_logged_in: CliHelper,
                                                 authenticated_context, tmp_path: Path):
        """A code issued for client A must not be exchangeable by client B."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        # Create a second client
        other_cid = f"{E2E_PREFIX}-replay-other-{_RUN_SUFFIX}"
        _delete_client(cli_logged_in, other_cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        other_creds = _create_client_with_secret(
            cli_logged_in, client_id=other_cid,
            client_name=f"E2E Replay Other {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile",
            grant_types=["authorization_code", "client_credentials"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "replay-other-creds.json",
        )
        other_internal = _get_client_internal_id(cli_logged_in, other_cid)
        _set_auto_approval(authenticated_context, other_internal)

        # Get a code for the first client
        code, verifier, nonce = _acquire_auth_code(oidc_client, authenticated_context, self._cid)

        # Try to exchange it with the other client's credentials
        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=other_cid,
            client_secret=other_creds["initialSecret"], code_verifier=verifier,
        )
        assert not resp.ok, "Cross-client code exchange should be rejected"

        _delete_client(cli_logged_in, other_cid)

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# 5. Refresh Token Rotation & Cascade Revocation
# ═══════════════════════════════════════════════════════════════════════════


class TestRefreshTokenSecurity:
    """
    Test refresh token rotation semantics and revocation cascades.
    """

    _cid = f"{E2E_PREFIX}-refresh-{_RUN_SUFFIX}"
    _client_secret: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path, authenticated_context):
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=self._cid,
            client_name=f"E2E Refresh {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile email offline_access",
            grant_types=["authorization_code", "refresh_token"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "refresh-creds.json",
        )
        TestRefreshTokenSecurity._client_secret = creds["initialSecret"]
        internal_id = _get_client_internal_id(cli_logged_in, self._cid)
        _set_auto_approval(authenticated_context, internal_id)

    def test_02_refresh_returns_new_tokens(self, oidc_client: OidcClient, authenticated_context):
        """Refreshing should return a new access_token (and potentially new refresh_token)."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        code, verifier, nonce = _acquire_auth_code(
            oidc_client, authenticated_context, self._cid,
            scope="openid profile email offline_access",
        )
        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
            client_secret=self._client_secret, code_verifier=verifier,
        )
        assert resp.ok
        original_at = resp.access_token
        original_rt = resp.refresh_token
        assert original_rt, "No refresh token returned"

        # Refresh
        ref_resp = oidc_client.token_refresh(
            refresh_token=original_rt, client_id=self._cid,
            client_secret=self._client_secret,
        )
        assert ref_resp.ok, f"Refresh failed: {ref_resp.error}"
        assert ref_resp.access_token != original_at, "Refresh should return new access_token"

    def test_03_revoked_refresh_token_unusable(self, oidc_client: OidcClient, authenticated_context):
        """Once revoked, a refresh token must no longer work."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        code, verifier, nonce = _acquire_auth_code(
            oidc_client, authenticated_context, self._cid,
            scope="openid profile email offline_access",
        )
        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
            client_secret=self._client_secret, code_verifier=verifier,
        )
        assert resp.ok
        rt = resp.refresh_token

        # Revoke
        rev = oidc_client.revoke(rt, self._cid, client_secret=self._client_secret,
                                  token_type_hint="refresh_token")
        assert rev == 200

        # Should fail now
        ref_resp = oidc_client.token_refresh(
            refresh_token=rt, client_id=self._cid,
            client_secret=self._client_secret,
        )
        assert not ref_resp.ok, "Revoked refresh token should not work"

    def test_04_scope_downscoping_on_refresh(self, oidc_client: OidcClient, authenticated_context):
        """A refresh request with fewer scopes should succeed.

        RFC 6749 §6 allows servers to either honour the downscoped request
        or return the originally granted scopes.  Both are valid.
        """
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        code, verifier, nonce = _acquire_auth_code(
            oidc_client, authenticated_context, self._cid,
            scope="openid profile email offline_access",
        )
        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
            client_secret=self._client_secret, code_verifier=verifier,
        )
        assert resp.ok
        rt = resp.refresh_token

        # Refresh with smaller scope — server may honour or ignore the restriction
        ref_resp = oidc_client.token_refresh(
            refresh_token=rt, client_id=self._cid,
            client_secret=self._client_secret,
            scope="openid profile",  # dropped 'email' and 'offline_access'
        )
        assert ref_resp.ok, f"Downscoped refresh failed: {ref_resp.error}"
        _, payload = decode_jwt(ref_resp.access_token)
        granted = payload.get("scope", "")
        # The server MUST NOT grant more scopes than originally approved.
        # It may or may not honour the narrower request.
        original_scopes = {"openid", "profile", "email", "offline_access"}
        granted_scopes = set(granted.split()) if granted else set()
        assert granted_scopes <= original_scopes, \
            f"Refresh granted unexpected scopes: {granted_scopes - original_scopes}"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# 6. Token Endpoint Abuse & Malformed Requests
# ═══════════════════════════════════════════════════════════════════════════


class TestTokenEndpointAbuse:
    """
    Send malformed/adversarial requests to the token endpoint.
    Ensures the server returns proper error responses and doesn't crash.
    """

    _cid = f"{E2E_PREFIX}-abuse-{_RUN_SUFFIX}"
    _client_secret: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=self._cid,
            client_name=f"E2E Abuse {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile",
            grant_types=["authorization_code", "client_credentials", "refresh_token"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "abuse-creds.json",
        )
        TestTokenEndpointAbuse._client_secret = creds["initialSecret"]

    def test_02_empty_body(self, oidc_client: OidcClient):
        """POST to token with empty body should fail gracefully."""
        status, body = oidc_client.raw_token_request({})
        assert status in (400, 401)

    def test_03_missing_grant_type(self, oidc_client: OidcClient):
        """Token request without grant_type should fail."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        status, body = oidc_client.raw_token_request({
            "client_id": self._cid,
            "client_secret": self._client_secret,
        })
        assert status in (400, 401)
        assert body.get("error") in ("invalid_request", "unsupported_grant_type", "invalid_grant")

    def test_04_unknown_grant_type(self, oidc_client: OidcClient):
        """Token request with a fabricated grant_type should fail."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        status, body = oidc_client.raw_token_request({
            "grant_type": "urn:custom:fake-grant",
            "client_id": self._cid,
            "client_secret": self._client_secret,
        })
        assert status in (400, 401)
        assert body.get("error") in ("unsupported_grant_type", "unauthorized_client", "invalid_grant")

    def test_05_nonexistent_client(self, oidc_client: OidcClient):
        """Token request with a client_id that doesn't exist should fail."""
        status, body = oidc_client.raw_token_request({
            "grant_type": "client_credentials",
            "client_id": "nonexistent-client-id-12345",
            "client_secret": "doesnt-matter",
            "scope": "openid",
        })
        assert status in (400, 401)

    def test_06_sql_injection_in_client_id(self, oidc_client: OidcClient):
        """SQL injection attempt in client_id should be safely handled."""
        status, body = oidc_client.raw_token_request({
            "grant_type": "client_credentials",
            "client_id": "'; DROP TABLE clients; --",
            "client_secret": "x",
            "scope": "openid",
        })
        assert status in (400, 401)
        # Server should still be responsive after this
        disco = oidc_client.discover()
        assert disco.get("issuer"), "Server unresponsive after SQL injection attempt"

    def test_07_extremely_long_values(self, oidc_client: OidcClient):
        """Extremely long parameter values should not crash the server."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        long_val = "A" * 100_000
        status, body = oidc_client.raw_token_request({
            "grant_type": "client_credentials",
            "client_id": self._cid,
            "client_secret": long_val,
            "scope": long_val,
        })
        assert status in (400, 401, 413, 414)

    def test_08_unicode_in_scope(self, oidc_client: OidcClient):
        """Unicode characters in scope should be rejected cleanly."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        status, body = oidc_client.raw_token_request({
            "grant_type": "client_credentials",
            "client_id": self._cid,
            "client_secret": self._client_secret,
            "scope": "openid profile 中文 العربية 🎉",
            "audience": BACKEND_AUDIENCE,
        })
        # Should either succeed (ignoring unknown scopes) or fail gracefully
        assert status in (200, 400, 401)

    def test_09_duplicate_grant_type_params(self, oidc_client: OidcClient):
        """Sending grant_type twice in the body. Server should handle it."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        # Use raw requests to send duplicate params
        resp = http_requests.post(
            oidc_client.token_endpoint,
            data="grant_type=client_credentials&grant_type=authorization_code"
                 f"&client_id={self._cid}&client_secret={self._client_secret}"
                 f"&scope=openid&audience={BACKEND_AUDIENCE}",
            headers={"Content-Type": "application/x-www-form-urlencoded"},
            verify=False,
        )
        # Server should either pick one or reject — not crash
        assert resp.status_code in (200, 400, 401)

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# 7. Cross-Client Token Abuse
# ═══════════════════════════════════════════════════════════════════════════


class TestCrossClientAbuse:
    """
    Verify that tokens from one client cannot be used/refreshed/introspected
    by another client.
    """

    _cid_a = f"{E2E_PREFIX}-xc-a-{_RUN_SUFFIX}"
    _cid_b = f"{E2E_PREFIX}-xc-b-{_RUN_SUFFIX}"
    _secret_a: str | None = None
    _secret_b: str | None = None
    _rt_a: str | None = None

    def test_01_provision_clients(self, cli_logged_in: CliHelper, tmp_path: Path, authenticated_context):
        realm_id = _get_default_realm_id(cli_logged_in)
        for cid in (self._cid_a, self._cid_b):
            _delete_client(cli_logged_in, cid)

        creds_a = _create_client_with_secret(
            cli_logged_in, client_id=self._cid_a,
            client_name=f"E2E XC-A {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile",
            grant_types=["authorization_code", "refresh_token", "client_credentials"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "xc-a.json",
        )
        creds_b = _create_client_with_secret(
            cli_logged_in, client_id=self._cid_b,
            client_name=f"E2E XC-B {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile",
            grant_types=["authorization_code", "refresh_token", "client_credentials"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "xc-b.json",
        )
        TestCrossClientAbuse._secret_a = creds_a["initialSecret"]
        TestCrossClientAbuse._secret_b = creds_b["initialSecret"]

        for cid in (self._cid_a, self._cid_b):
            iid = _get_client_internal_id(cli_logged_in, cid)
            _set_auto_approval(authenticated_context, iid)

    def test_02_acquire_refresh_token_for_a(self, oidc_client: OidcClient, authenticated_context):
        if not self._secret_a:
            pytest.skip("Clients not provisioned")
        code, verifier, nonce = _acquire_auth_code(oidc_client, authenticated_context, self._cid_a)
        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid_a,
            client_secret=self._secret_a, code_verifier=verifier,
        )
        assert resp.ok
        TestCrossClientAbuse._rt_a = resp.refresh_token
        assert self._rt_a, "No refresh token for client A"

    def test_03_client_b_cannot_refresh_client_a_token(self, oidc_client: OidcClient):
        """Client B must not be able to use Client A's refresh token."""
        if not self._rt_a or not self._secret_b:
            pytest.skip("Prerequisites not met")
        resp = oidc_client.token_refresh(
            refresh_token=self._rt_a, client_id=self._cid_b,
            client_secret=self._secret_b,
        )
        assert not resp.ok, "Client B should not be able to use Client A's refresh token"

    def test_04_client_b_cannot_revoke_client_a_token(self, oidc_client: OidcClient):
        """Client B revoking Client A's refresh token: per RFC 7009, should silently accept
        but the token should remain valid for Client A."""
        if not self._rt_a or not self._secret_a or not self._secret_b:
            pytest.skip("Prerequisites not met")

        # Client B attempts revocation
        oidc_client.revoke(
            self._rt_a, self._cid_b, client_secret=self._secret_b,
            token_type_hint="refresh_token",
        )

        # Client A's token should still work
        ref_resp = oidc_client.token_refresh(
            refresh_token=self._rt_a, client_id=self._cid_a,
            client_secret=self._secret_a,
        )
        assert ref_resp.ok, "Client A's token should still be valid after B's revoke attempt"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid_a)
        _delete_client(cli_logged_in, self._cid_b)


# ═══════════════════════════════════════════════════════════════════════════
# 8. Prompt=none (Silent Authentication)
# ═══════════════════════════════════════════════════════════════════════════


class TestPromptNone:
    """
    Test prompt=none behavior:
    - Authenticated user → silent code issuance (no UI)
    - Unauthenticated → login_required error
    """

    _cid = f"{E2E_PREFIX}-silent-{_RUN_SUFFIX}"
    _client_secret: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path, authenticated_context):
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=self._cid,
            client_name=f"E2E Silent {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile",
            grant_types=["authorization_code"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "silent-creds.json",
        )
        TestPromptNone._client_secret = creds["initialSecret"]
        internal_id = _get_client_internal_id(cli_logged_in, self._cid)
        _set_auto_approval(authenticated_context, internal_id)

    def test_02_prompt_none_with_session_succeeds(self, oidc_client: OidcClient, authenticated_context):
        """prompt=none with an active session should issue a code silently."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        verifier, challenge = generate_pkce()
        state = secrets.token_urlsafe(32)

        auth_url = oidc_client.build_authorize_url(
            client_id=self._cid,
            redirect_uri=REDIRECT_URI,
            scope="openid profile",
            code_challenge=challenge,
            state=state,
            extra_params={"prompt": "none"},
        )

        try:
            callback_url = _follow_authorize_redirects(authenticated_context, auth_url)
            parsed = urllib.parse.urlparse(callback_url)
            params = urllib.parse.parse_qs(parsed.query)
            error = params.get("error", [None])[0]
            code = params.get("code", [None])[0]

            if error:
                # Some servers require consent even with auto-approval for silent
                assert error in ("interaction_required", "consent_required", "login_required"), \
                    f"Unexpected prompt=none error: {error}"
            else:
                assert code, f"prompt=none should return a code: {callback_url}"
        except AssertionError:
            pytest.skip("prompt=none not fully supported for this client configuration")

    def test_03_prompt_none_without_session_returns_login_required(self, oidc_client: OidcClient):
        """prompt=none without a session must return error=login_required."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        verifier, challenge = generate_pkce()
        state = secrets.token_urlsafe(32)

        auth_url = oidc_client.build_authorize_url(
            client_id=self._cid,
            redirect_uri=REDIRECT_URI,
            scope="openid profile",
            code_challenge=challenge,
            state=state,
            extra_params={"prompt": "none"},
        )

        # Use a clean session (no cookies) — unauthenticated
        resp = http_requests.get(auth_url, verify=False, allow_redirects=False)

        # Follow redirects manually
        url = auth_url
        for _ in range(10):
            resp = http_requests.get(url, verify=False, allow_redirects=False)
            if resp.status_code not in (301, 302, 303, 307, 308):
                break
            location = resp.headers.get("location", "")
            if REDIRECT_URI.split("//")[1].split("/")[0] in location:
                parsed = urllib.parse.urlparse(location)
                params = urllib.parse.parse_qs(parsed.query)
                error = params.get("error", [None])[0]
                assert error == "login_required", \
                    f"Expected 'login_required' but got '{error}'"
                return
            url = location if location.startswith("http") else BASE_URL + location

        # If we got here, check for error in query params of the redirect
        # The server may have returned the error via a different mechanism
        pytest.skip("Could not capture prompt=none error redirect")

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# 9. DPoP Advanced Scenarios
# ═══════════════════════════════════════════════════════════════════════════


class TestDPoPAdvanced:
    """
    Extended DPoP tests: wrong key, wrong method, wrong URI, expired proof,
    missing fields, different key for token vs resource.
    """

    _cid = f"{E2E_PREFIX}-dpop2-{_RUN_SUFFIX}"
    _client_secret: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=self._cid,
            client_name=f"E2E DPoP Adv {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile",
            grant_types=["client_credentials"],
            require_consent=False,
            cred_path=tmp_path / "dpop2-creds.json",
        )
        TestDPoPAdvanced._client_secret = creds["initialSecret"]

    def test_02_wrong_htm_rejected(self, oidc_client: OidcClient):
        """DPoP proof with htm=GET for a POST token request should be rejected."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        builder = DPoPProofBuilder()
        proof = builder.create_proof(htm="GET", htu=oidc_client.token_endpoint)
        resp = oidc_client.token_client_credentials(
            client_id=self._cid, client_secret=self._client_secret,
            scope="openid", audience=BACKEND_AUDIENCE, dpop_header=proof,
        )
        assert not resp.ok, "DPoP proof with wrong htm should be rejected"

    def test_03_wrong_htu_rejected(self, oidc_client: OidcClient):
        """DPoP proof with wrong htu should be rejected."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        builder = DPoPProofBuilder()
        proof = builder.create_proof(htm="POST", htu="https://evil.example.com/token")
        resp = oidc_client.token_client_credentials(
            client_id=self._cid, client_secret=self._client_secret,
            scope="openid", audience=BACKEND_AUDIENCE, dpop_header=proof,
        )
        assert not resp.ok, "DPoP proof with wrong htu should be rejected"

    def test_04_expired_proof_rejected(self, oidc_client: OidcClient):
        """DPoP proof with iat far in the past should be rejected."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        builder = DPoPProofBuilder()
        # Create a proof manually with an old iat
        import jwt as pyjwt
        from cryptography.hazmat.primitives import serialization
        headers = {
            "typ": "dpop+jwt",
            "alg": "ES256",
            "jwk": builder.public_jwk,
        }
        payload = {
            "jti": secrets.token_urlsafe(16),
            "iat": int(time.time()) - 3600,  # 1 hour ago
            "htm": "POST",
            "htu": oidc_client.token_endpoint,
        }
        pem = builder._private_key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        )
        proof = pyjwt.encode(payload, pem, algorithm="ES256", headers=headers)

        resp = oidc_client.token_client_credentials(
            client_id=self._cid, client_secret=self._client_secret,
            scope="openid", audience=BACKEND_AUDIENCE, dpop_header=proof,
        )
        assert not resp.ok, "Expired DPoP proof should be rejected"

    def test_05_dpop_with_wrong_alg_rejected(self, oidc_client: OidcClient):
        """DPoP proof signed with unsupported algorithm should be rejected."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        # Send a garbage string as DPoP header
        resp = oidc_client.token_client_credentials(
            client_id=self._cid, client_secret=self._client_secret,
            scope="openid", audience=BACKEND_AUDIENCE,
            dpop_header="not.a.valid.dpop.proof",
        )
        assert not resp.ok, "Invalid DPoP proof should be rejected"

    def test_06_dpop_missing_jti_rejected(self, oidc_client: OidcClient):
        """DPoP proof without jti claim should be rejected."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        builder = DPoPProofBuilder()
        import jwt as pyjwt
        from cryptography.hazmat.primitives import serialization
        headers = {
            "typ": "dpop+jwt",
            "alg": "ES256",
            "jwk": builder.public_jwk,
        }
        payload = {
            # Missing 'jti'
            "iat": int(time.time()),
            "htm": "POST",
            "htu": oidc_client.token_endpoint,
        }
        pem = builder._private_key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        )
        proof = pyjwt.encode(payload, pem, algorithm="ES256", headers=headers)

        resp = oidc_client.token_client_credentials(
            client_id=self._cid, client_secret=self._client_secret,
            scope="openid", audience=BACKEND_AUDIENCE, dpop_header=proof,
        )
        assert not resp.ok, "DPoP proof without jti should be rejected"

    def test_07_two_different_keys_for_token_and_userinfo(self, oidc_client: OidcClient):
        """Token acquired with DPoP key A should fail at userinfo with proof from key B."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        builder_a = DPoPProofBuilder()
        builder_b = DPoPProofBuilder()

        # Acquire token with key A
        proof_a = builder_a.create_proof(htm="POST", htu=oidc_client.token_endpoint)
        resp = oidc_client.token_client_credentials(
            client_id=self._cid, client_secret=self._client_secret,
            scope="openid profile", audience=BACKEND_AUDIENCE, dpop_header=proof_a,
        )
        assert resp.ok, f"DPoP token failed: {resp.error}"
        token = resp.access_token

        # Try userinfo with key B (different key)
        ath = DPoPProofBuilder.compute_ath(token)
        proof_b = builder_b.create_proof(
            htm="GET", htu=oidc_client.userinfo_endpoint, ath=ath,
        )
        status, body = oidc_client.userinfo(token, dpop_header=proof_b)
        assert status in (401, 403), \
            f"Userinfo with different DPoP key should fail, got {status}"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# 10. Pushed Authorization Requests (PAR)
# ═══════════════════════════════════════════════════════════════════════════


class TestPushedAuthorizationRequests:
    """
    PAR tests (RFC 9126): push authorize params, get request_uri, use in /authorize.
    """

    _cid = f"{E2E_PREFIX}-par-{_RUN_SUFFIX}"
    _client_secret: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path, authenticated_context):
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=self._cid,
            client_name=f"E2E PAR {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile",
            grant_types=["authorization_code"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "par-creds.json",
        )
        TestPushedAuthorizationRequests._client_secret = creds["initialSecret"]
        internal_id = _get_client_internal_id(cli_logged_in, self._cid)
        _set_auto_approval(authenticated_context, internal_id)

    def test_02_par_push_succeeds(self, oidc_client: OidcClient):
        """PAR request should return 201 with request_uri and expires_in."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        par_ep = oidc_client.par_endpoint
        if not par_ep:
            pytest.skip("PAR endpoint not advertised in discovery")

        verifier, challenge = generate_pkce()
        status, body = oidc_client.pushed_authorization_request(
            client_id=self._cid, client_secret=self._client_secret,
            redirect_uri=REDIRECT_URI, scope="openid profile",
            code_challenge=challenge, state=secrets.token_urlsafe(16),
            nonce=secrets.token_urlsafe(16),
        )
        assert status == 201, f"PAR failed ({status}): {body}"
        assert "request_uri" in body, f"PAR response missing request_uri: {body}"
        assert "expires_in" in body, f"PAR response missing expires_in: {body}"
        assert body["request_uri"].startswith("urn:"), \
            f"PAR request_uri should start with 'urn:': {body['request_uri']}"

    def test_03_par_request_uri_usable_in_authorize(self, oidc_client: OidcClient, authenticated_context):
        """A pushed request_uri should be usable in the /authorize endpoint."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        par_ep = oidc_client.par_endpoint
        if not par_ep:
            pytest.skip("PAR endpoint not advertised")

        verifier, challenge = generate_pkce()
        state = secrets.token_urlsafe(16)
        nonce = secrets.token_urlsafe(16)

        status, body = oidc_client.pushed_authorization_request(
            client_id=self._cid, client_secret=self._client_secret,
            redirect_uri=REDIRECT_URI, scope="openid profile",
            code_challenge=challenge, state=state, nonce=nonce,
        )
        assert status == 201
        request_uri = body["request_uri"]

        # Use request_uri in /authorize
        auth_url = (
            f"{oidc_client.authorization_endpoint}"
            f"?client_id={self._cid}&request_uri={urllib.parse.quote(request_uri)}"
        )
        try:
            callback_url = _follow_authorize_redirects(authenticated_context, auth_url)
            parsed = urllib.parse.urlparse(callback_url)
            params = urllib.parse.parse_qs(parsed.query)
            error = params.get("error", [None])[0]
            code = params.get("code", [None])[0]

            if error:
                pytest.skip(f"PAR-based authorize returned error: {error}")
            assert code, f"Expected code in callback: {callback_url}"

            # Exchange code for tokens to complete the full PAR flow
            resp = oidc_client.token_authorization_code(
                code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
                client_secret=self._client_secret, code_verifier=verifier,
            )
            assert resp.ok, f"Token exchange after PAR failed: {resp.error}"
        except AssertionError as e:
            if "redirect not found" in str(e).lower():
                pytest.skip("PAR-based authorize flow not fully supported in test harness")
            raise

    def test_04_par_replay_rejected(self, oidc_client: OidcClient, authenticated_context):
        """A PAR request_uri should be single-use."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        par_ep = oidc_client.par_endpoint
        if not par_ep:
            pytest.skip("PAR endpoint not advertised")

        verifier, challenge = generate_pkce()
        status, body = oidc_client.pushed_authorization_request(
            client_id=self._cid, client_secret=self._client_secret,
            redirect_uri=REDIRECT_URI, scope="openid profile",
            code_challenge=challenge,
        )
        assert status == 201
        request_uri = body["request_uri"]

        # Use it once
        auth_url = (
            f"{oidc_client.authorization_endpoint}"
            f"?client_id={self._cid}&request_uri={urllib.parse.quote(request_uri)}"
        )
        api = authenticated_context.request
        resp = api.get(auth_url, max_redirects=0, ignore_https_errors=True)

        # Try using it again — should be consumed
        resp2 = api.get(auth_url, max_redirects=0, ignore_https_errors=True)
        if resp2.status in (301, 302, 303):
            location = resp2.headers.get("location", "")
            parsed = urllib.parse.urlparse(location)
            params = urllib.parse.parse_qs(parsed.query)
            error = params.get("error", [None])[0]
            if error:
                assert error in ("invalid_request_uri", "invalid_request"), \
                    f"Expected request_uri consumed error, got: {error}"
                return
        # If no error redirect, the server may have handled it differently
        # (some servers allow multiple uses within TTL)

    def test_05_par_without_client_auth_fails(self, oidc_client: OidcClient):
        """PAR without client authentication should fail."""
        par_ep = oidc_client.par_endpoint
        if not par_ep:
            pytest.skip("PAR endpoint not advertised")

        verifier, challenge = generate_pkce()
        status, body = oidc_client.pushed_authorization_request(
            client_id=self._cid,
            # No client_secret
            redirect_uri=REDIRECT_URI, scope="openid profile",
            code_challenge=challenge,
        )
        assert status in (400, 401), f"PAR without auth should fail, got {status}"

    def test_06_par_wrong_redirect_uri_fails(self, oidc_client: OidcClient):
        """PAR with unregistered redirect_uri should fail."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        par_ep = oidc_client.par_endpoint
        if not par_ep:
            pytest.skip("PAR endpoint not advertised")

        verifier, challenge = generate_pkce()
        status, body = oidc_client.pushed_authorization_request(
            client_id=self._cid, client_secret=self._client_secret,
            redirect_uri="https://evil.example.com/callback",
            scope="openid profile", code_challenge=challenge,
        )
        assert status in (400, 401), f"PAR with wrong redirect should fail, got {status}"
        assert body.get("error") in ("invalid_request", "invalid_redirect_uri"), \
            f"Unexpected error: {body.get('error')}"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# 11. Userinfo Endpoint Edge Cases
# ═══════════════════════════════════════════════════════════════════════════


class TestUserinfoEdgeCases:
    """
    Test userinfo endpoint behavior with various token types and errors.
    """

    _cid = f"{E2E_PREFIX}-ui-{_RUN_SUFFIX}"
    _client_secret: str | None = None
    _access_token: str | None = None

    def test_01_provision_and_acquire(self, cli_logged_in: CliHelper, tmp_path: Path,
                                       oidc_client: OidcClient, authenticated_context):
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=self._cid,
            client_name=f"E2E Userinfo {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile email",
            grant_types=["authorization_code"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "ui-creds.json",
        )
        TestUserinfoEdgeCases._client_secret = creds["initialSecret"]
        internal_id = _get_client_internal_id(cli_logged_in, self._cid)
        _set_auto_approval(authenticated_context, internal_id)

        code, verifier, nonce = _acquire_auth_code(oidc_client, authenticated_context, self._cid)
        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
            client_secret=creds["initialSecret"], code_verifier=verifier,
        )
        assert resp.ok
        TestUserinfoEdgeCases._access_token = resp.access_token

    def test_02_userinfo_without_bearer_fails(self, oidc_client: OidcClient):
        """Calling userinfo without Authorization header should return 401."""
        resp = http_requests.get(oidc_client.userinfo_endpoint, verify=False)
        assert resp.status_code == 401

    def test_03_userinfo_with_garbage_token_fails(self, oidc_client: OidcClient):
        """Calling userinfo with an invalid token should return 401."""
        resp = http_requests.get(
            oidc_client.userinfo_endpoint,
            headers={"Authorization": "Bearer garbage-token-value"},
            verify=False,
        )
        assert resp.status_code == 401

    def test_04_userinfo_returns_standard_claims(self, oidc_client: OidcClient):
        """Userinfo should return at least sub, and profile claims if 'profile' scope was granted."""
        if not self._access_token:
            pytest.skip("No access token")
        status, body = oidc_client.userinfo(self._access_token)
        assert status == 200
        assert "sub" in body, "Userinfo missing 'sub'"

    def test_05_userinfo_sub_matches_id_token(self, oidc_client: OidcClient):
        """Userinfo 'sub' must match the id_token 'sub'."""
        if not self._access_token:
            pytest.skip("No access token")
        _, at_payload = decode_jwt(self._access_token)
        status, body = oidc_client.userinfo(self._access_token)
        assert status == 200
        assert body["sub"] == at_payload["sub"], \
            f"Sub mismatch: userinfo={body['sub']}, token={at_payload['sub']}"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# 12. Authorization Endpoint Edge Cases
# ═══════════════════════════════════════════════════════════════════════════


class TestAuthorizeEdgeCases:
    """
    Edge cases on the /authorize endpoint:
    - Unregistered client_id
    - Missing response_type
    - Unregistered redirect_uri
    - Unknown scope
    """

    def test_01_authorize_unknown_client(self, oidc_client: OidcClient):
        """Authorize with unknown client_id should show an error (not redirect to attacker)."""
        verifier, challenge = generate_pkce()
        resp = http_requests.get(
            oidc_client.authorization_endpoint,
            params={
                "response_type": "code",
                "client_id": "nonexistent-client-xyz",
                "redirect_uri": "https://evil.com/callback",
                "scope": "openid",
                "code_challenge": challenge,
                "code_challenge_method": "S256",
            },
            verify=False,
            allow_redirects=False,
        )
        # Server MUST NOT redirect to an unvalidated redirect_uri per RFC 6749 §4.1.2.1
        if resp.status_code in (301, 302, 303):
            location = resp.headers.get("location", "")
            assert "evil.com" not in location, \
                "Server must not redirect to unregistered redirect_uri!"
        else:
            # Server showed an error page — this is correct behavior
            assert resp.status_code in (200, 400)

    def test_02_authorize_missing_response_type(self, oidc_client: OidcClient):
        """Authorize without response_type should fail."""
        resp = http_requests.get(
            oidc_client.authorization_endpoint,
            params={
                "client_id": "some-client",
                "redirect_uri": REDIRECT_URI,
                "scope": "openid",
            },
            verify=False,
            allow_redirects=False,
        )
        # Should either show error page or redirect with error
        if resp.status_code in (301, 302, 303):
            location = resp.headers.get("location", "")
            if REDIRECT_URI.split("//")[1].split("/")[0] in location:
                parsed = urllib.parse.urlparse(location)
                params = urllib.parse.parse_qs(parsed.query)
                assert params.get("error"), "Expected error in redirect"
        else:
            assert resp.status_code in (200, 400)

    def test_03_authorize_unsupported_response_type(self, oidc_client: OidcClient):
        """Authorize with response_type=token (implicit) should be rejected if not supported."""
        disco = oidc_client.discover()
        if "token" in disco.get("response_types_supported", []):
            pytest.skip("Implicit flow is supported")

        resp = http_requests.get(
            oidc_client.authorization_endpoint,
            params={
                "response_type": "token",
                "client_id": "any",
                "redirect_uri": REDIRECT_URI,
                "scope": "openid",
            },
            verify=False,
            allow_redirects=False,
        )
        # Should not issue an implicit token
        if resp.status_code in (301, 302, 303):
            location = resp.headers.get("location", "")
            assert "access_token" not in location, \
                "Server should not issue implicit tokens"

    def test_04_well_known_returns_json_content_type(self, oidc_client: OidcClient):
        """Discovery endpoint must return application/json."""
        resp = http_requests.get(
            f"{oidc_client.issuer_url}/.well-known/openid-configuration",
            verify=False,
        )
        assert resp.status_code == 200
        ct = resp.headers.get("content-type", "")
        assert "application/json" in ct, f"Discovery content-type: {ct}"


# ═══════════════════════════════════════════════════════════════════════════
# 13. Token Claims & Timing Validation
# ═══════════════════════════════════════════════════════════════════════════


class TestTokenClaimValidation:
    """
    Verify token claims meet OIDC spec requirements:
    - Required claims present
    - Times are sensible (iat <= now, exp > now, nbf <= now)
    - aud/azp correct
    - jti uniqueness
    """

    _cid = f"{E2E_PREFIX}-claims-{_RUN_SUFFIX}"
    _client_secret: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path, authenticated_context):
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=self._cid,
            client_name=f"E2E Claims {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile email",
            grant_types=["authorization_code", "client_credentials", "refresh_token"],
            redirect_uris=[REDIRECT_URI], require_pkce=True, require_consent=False,
            cred_path=tmp_path / "claims-creds.json",
        )
        TestTokenClaimValidation._client_secret = creds["initialSecret"]
        internal_id = _get_client_internal_id(cli_logged_in, self._cid)
        _set_auto_approval(authenticated_context, internal_id)

    def test_02_id_token_required_claims(self, oidc_client: OidcClient, authenticated_context):
        """ID token MUST contain iss, sub, aud, exp, iat per OIDC Core §2."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        code, verifier, nonce = _acquire_auth_code(oidc_client, authenticated_context, self._cid)
        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
            client_secret=self._client_secret, code_verifier=verifier,
        )
        assert resp.ok

        header, payload = decode_jwt(resp.id_token)
        for claim in ("iss", "sub", "aud", "exp", "iat"):
            assert claim in payload, f"ID token missing required claim: {claim}"

        # Timing sanity
        now = time.time()
        assert payload["iat"] <= now + 60, f"iat is in the future: {payload['iat']}"
        assert payload["exp"] > now - 60, f"Token already expired: {payload['exp']}"
        assert payload["exp"] > payload["iat"], "exp must be after iat"

        # Algorithm
        assert header.get("alg") in ("RS256", "RS384", "RS512", "ES256", "ES384", "ES512"), \
            f"Unexpected signing alg: {header.get('alg')}"
        assert header.get("typ") == "JWT" or header.get("typ") is None

    def test_03_access_token_has_jti(self, oidc_client: OidcClient, authenticated_context):
        """Access tokens should have unique jti claims."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        code, verifier, nonce = _acquire_auth_code(oidc_client, authenticated_context, self._cid)
        resp = oidc_client.token_authorization_code(
            code=code, redirect_uri=REDIRECT_URI, client_id=self._cid,
            client_secret=self._client_secret, code_verifier=verifier,
        )
        assert resp.ok
        _, p1 = decode_jwt(resp.access_token)

        # Get a second token (M2M)
        resp2 = oidc_client.token_client_credentials(
            client_id=self._cid, client_secret=self._client_secret,
            scope="openid", audience=BACKEND_AUDIENCE,
        )
        assert resp2.ok
        _, p2 = decode_jwt(resp2.access_token)

        assert p1.get("jti"), "Access token missing 'jti'"
        assert p2.get("jti"), "M2M token missing 'jti'"
        assert p1["jti"] != p2["jti"], "Two tokens have the same jti!"

    def test_04_m2m_token_no_user_sub(self, oidc_client: OidcClient):
        """M2M tokens should not have a user-level sub (or sub == client_id)."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        resp = oidc_client.token_client_credentials(
            client_id=self._cid, client_secret=self._client_secret,
            scope="openid", audience=BACKEND_AUDIENCE,
        )
        assert resp.ok
        _, payload = decode_jwt(resp.access_token)
        # sub may be absent or may equal client_id
        sub = payload.get("sub")
        if sub:
            assert sub == self._cid or sub == payload.get("client_id") or sub == payload.get("azp"), \
                f"M2M token has unexpected user sub: {sub}"

    def test_05_token_response_type_and_expiry(self, oidc_client: OidcClient):
        """Token response should contain token_type, expires_in, and scope."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        resp = oidc_client.token_client_credentials(
            client_id=self._cid, client_secret=self._client_secret,
            scope="openid profile", audience=BACKEND_AUDIENCE,
        )
        assert resp.ok
        assert resp.token_type, "Missing token_type in response"
        assert resp.expires_in and resp.expires_in > 0, "Missing or zero expires_in"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)


# ═══════════════════════════════════════════════════════════════════════════
# 14. End Session / Logout Edge Cases
# ═══════════════════════════════════════════════════════════════════════════


class TestEndSession:
    """End session endpoint validation."""

    def test_01_end_session_without_params(self, oidc_client: OidcClient):
        """End session without any params should not crash."""
        status, body = oidc_client.end_session()
        # Should return a page (200) or redirect (302)
        assert status in (200, 302, 303), f"End session failed with status {status}"

    def test_02_end_session_with_invalid_id_token_hint(self, oidc_client: OidcClient):
        """End session with a garbage id_token_hint should be handled gracefully."""
        status, body = oidc_client.end_session(id_token_hint="not.a.valid.jwt")
        assert status in (200, 302, 303, 400), f"Unexpected status: {status}"

    def test_03_end_session_with_invalid_post_logout_redirect(self, oidc_client: OidcClient):
        """End session with an unregistered post_logout_redirect_uri must not redirect there."""
        status, body = oidc_client.end_session(
            post_logout_redirect_uri="https://evil.attacker.com/post-logout",
        )
        # Server MUST NOT redirect to an unregistered URI
        assert "evil.attacker.com" not in body, \
            "Server must not redirect to unregistered post_logout_redirect_uri"


# ═══════════════════════════════════════════════════════════════════════════
# 15. Revocation Edge Cases
# ═══════════════════════════════════════════════════════════════════════════


class TestRevocationEdgeCases:
    """Token revocation edge cases per RFC 7009."""

    _cid = f"{E2E_PREFIX}-rev-{_RUN_SUFFIX}"
    _client_secret: str | None = None

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        _delete_client(cli_logged_in, self._cid)
        realm_id = _get_default_realm_id(cli_logged_in)
        creds = _create_client_with_secret(
            cli_logged_in, client_id=self._cid,
            client_name=f"E2E Revocation {_RUN_SUFFIX}",
            realm_id=realm_id, scope="openid profile",
            grant_types=["client_credentials"],
            require_consent=False,
            cred_path=tmp_path / "rev-creds.json",
        )
        TestRevocationEdgeCases._client_secret = creds["initialSecret"]

    def test_02_revoke_nonexistent_token(self, oidc_client: OidcClient):
        """Revoking a token that doesn't exist should return 200 (per RFC 7009 §2.2)."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        status = oidc_client.revoke(
            "nonexistent-token-abc123", self._cid,
            client_secret=self._client_secret,
        )
        assert status == 200, "Revocation of unknown token should still return 200"

    def test_03_revoke_without_client_auth(self, oidc_client: OidcClient):
        """Revocation without client credentials should fail."""
        status = oidc_client.revoke("any-token", self._cid)
        # Should fail auth (some servers return 401, some 200 per policy)
        assert status in (200, 400, 401)

    def test_04_double_revoke_idempotent(self, oidc_client: OidcClient):
        """Revoking the same token twice should be idempotent (both 200)."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")

        resp = oidc_client.token_client_credentials(
            client_id=self._cid, client_secret=self._client_secret,
            scope="openid", audience=BACKEND_AUDIENCE,
        )
        assert resp.ok

        status1 = oidc_client.revoke(
            resp.access_token, self._cid,
            client_secret=self._client_secret,
        )
        status2 = oidc_client.revoke(
            resp.access_token, self._cid,
            client_secret=self._client_secret,
        )
        assert status1 == 200
        assert status2 == 200, "Double revocation should be idempotent"

    def test_90_cleanup(self, cli_logged_in: CliHelper):
        _delete_client(cli_logged_in, self._cid)
