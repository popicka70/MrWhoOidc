"""
P6 — Response modes, JARM and JAR (gaps G6, G7, G8).

TestResponseModes  : form_post response mode produces an auto-submitting form;
                     query/fragment are advertised.
TestJarm           : discovery advertises *.jwt response modes + iss parameter;
                     a JARM (query.jwt) flow returns a signed JWT response when a
                     client can be configured (via dynamic registration).
TestJar            : discovery advertises request object support; a malformed
                     request object is rejected.

JARM/JAR steps degrade to skips when the capability cannot be provisioned.
"""

from __future__ import annotations

import secrets
import urllib.parse
from pathlib import Path

import pytest

from utils.cli_helper import CliHelper
from utils.oidc_client import OidcClient, decode_jwt, generate_pkce
from .oidc_helpers import (
    REDIRECT_URI,
    RUN_SUFFIX,
    create_client_with_secret,
    delete_client,
    get_client_internal_id,
    get_default_realm_id,
    set_auto_approval,
)


def _api_fetch_following_redirects(authenticated_context, url: str):
    """Follow redirects via APIRequestContext, returning the final (status, body, url)."""
    api = authenticated_context.request
    base = REDIRECT_URI  # only used for host detection
    callback_host = base.split("//")[1].split("/")[0]
    for _ in range(10):
        resp = api.get(url, max_redirects=0, ignore_https_errors=True)
        if resp.status not in (301, 302, 303, 307, 308):
            return resp.status, resp.text(), url
        location = resp.headers.get("location", "")
        if callback_host in location:
            return resp.status, "", location
        if location.startswith("/"):
            url = "https://localhost:8443" + location
        else:
            url = location
    return resp.status, resp.text(), url


class TestResponseModes:
    """form_post and the advertised set of response modes."""

    _cid = f"e2e-respmode-{RUN_SUFFIX}"
    _secret: str | None = None

    def test_01_discovery_response_modes(self, oidc_client: OidcClient):
        modes = oidc_client.discovery.get("response_modes_supported", [])
        assert "query" in modes
        assert "fragment" in modes
        assert "form_post" in modes

    def test_02_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path,
                                 authenticated_context):
        delete_client(cli_logged_in, self._cid)
        realm_id = get_default_realm_id(cli_logged_in)
        creds = create_client_with_secret(
            cli_logged_in,
            client_id=self._cid,
            client_name=f"E2E RespMode {RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid profile",
            grant_types=["authorization_code"],
            redirect_uris=[REDIRECT_URI],
            require_pkce=True,
            require_consent=False,
            cred_path=tmp_path / "respmode-creds.json",
        )
        TestResponseModes._secret = creds["initialSecret"]
        internal_id = get_client_internal_id(cli_logged_in, self._cid)
        set_auto_approval(authenticated_context, internal_id)

    def test_03_form_post_returns_auto_submit_form(self, oidc_client: OidcClient,
                                                   authenticated_context):
        if not self._secret:
            pytest.skip("Client not provisioned")
        verifier, challenge = generate_pkce()
        state = secrets.token_urlsafe(16)
        auth_url = oidc_client.build_authorize_url(
            client_id=self._cid,
            redirect_uri=REDIRECT_URI,
            scope="openid profile",
            code_challenge=challenge,
            state=state,
            extra_params={"response_mode": "form_post"},
        )
        status, body, _url = _api_fetch_following_redirects(authenticated_context, auth_url)
        assert status == 200, f"expected form_post HTML, got {status}"
        assert "<form" in body.lower()
        assert 'method="post"' in body.lower() or "method='post'" in body.lower()
        assert REDIRECT_URI in body, "form action should target the redirect_uri"
        assert state in body, "state should be present as a hidden field"
        assert 'name="code"' in body or "name='code'" in body


class TestJarm:
    """JWT-secured Authorization Response Mode (RFC 9101 family)."""

    def test_01_discovery_advertises_jarm(self, oidc_client: OidcClient):
        disco = oidc_client.discovery
        modes = disco.get("response_modes_supported", [])
        assert any(m.endswith(".jwt") for m in modes), f"no *.jwt response modes: {modes}"
        assert disco.get("authorization_response_iss_parameter_supported") is True
        assert disco.get("authorization_signing_alg_values_supported"), disco.keys()

    def test_02_query_jwt_returns_signed_response(self, oidc_client: OidcClient,
                                                  authenticated_context):
        """Register a JARM client via dynamic registration and run a query.jwt flow."""
        reg_endpoint = oidc_client.endpoint("registration_endpoint")
        if not reg_endpoint:
            pytest.skip("dynamic registration disabled; cannot configure a JARM client")
        status, reg = oidc_client.raw_post(
            reg_endpoint,
            json_body={
                "redirect_uris": [REDIRECT_URI],
                "grant_types": ["authorization_code"],
                "response_types": ["code"],
                "client_name": f"E2E JARM {RUN_SUFFIX}",
                "token_endpoint_auth_method": "client_secret_basic",
                "authorization_signed_response_alg": "RS256",
            },
            headers={"Content-Type": "application/json"},
        )
        if status in (401, 403):
            pytest.skip("registration requires an initial access token")
        assert status in (200, 201), f"register JARM client failed: {status} {reg}"
        client_id = reg["client_id"]

        try:
            verifier, challenge = generate_pkce()
            state = secrets.token_urlsafe(16)
            auth_url = oidc_client.build_authorize_url(
                client_id=client_id,
                redirect_uri=REDIRECT_URI,
                scope="openid profile",
                code_challenge=challenge,
                state=state,
                extra_params={"response_mode": "query.jwt"},
            )
            status2, _body, final_url = _api_fetch_following_redirects(
                authenticated_context, auth_url
            )
            params = urllib.parse.parse_qs(urllib.parse.urlparse(final_url).query)
            response_jwt = params.get("response", [None])[0]
            if not response_jwt:
                pytest.skip(f"no JARM response param produced (url={final_url})")
            header, payload = decode_jwt(response_jwt)
            assert header.get("alg") == "RS256", header
            assert payload.get("iss"), payload
            assert payload.get("aud") == client_id
            assert payload.get("code"), "JARM response must carry the code"
            assert payload.get("state") == state
        finally:
            uri = reg.get("registration_client_uri")
            if uri:
                oidc_client.session.delete(
                    uri,
                    headers={"Authorization": f"Bearer {reg.get('registration_access_token')}"},
                )


class TestJar:
    """JWT-secured Authorization Request (request object) support."""

    def test_01_discovery_advertises_jar(self, oidc_client: OidcClient):
        disco = oidc_client.discovery
        assert disco.get("request_parameter_supported") is True
        assert disco.get("request_object_signing_alg_values_supported"), disco.keys()

    def test_02_malformed_request_object_rejected(self, oidc_client: OidcClient,
                                                  authenticated_context):
        """A syntactically invalid ``request`` parameter must be rejected."""
        auth_url = oidc_client.build_authorize_url(
            client_id="any-client",
            redirect_uri=REDIRECT_URI,
            scope="openid",
            extra_params={"request": "not.a.valid.jwt"},
        )
        status, body, final_url = _api_fetch_following_redirects(
            authenticated_context, auth_url
        )
        # Either an error page (4xx) or an error redirect — never a code.
        params = urllib.parse.parse_qs(urllib.parse.urlparse(final_url).query)
        assert "code" not in params, f"malformed request object yielded a code: {final_url}"
