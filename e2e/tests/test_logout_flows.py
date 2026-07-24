"""
P7 — End-session / RP-initiated logout (gaps G9, G10).

Covers:
  end_session with a registered post_logout_redirect_uri → 302 redirect (state echoed) →
  end_session with an UNREGISTERED redirect uri → no redirect (HTML page) →
  end_session with no params → logout confirmation page →
  discovery advertises end_session_endpoint and check_session_iframe.

An id_token is obtained via a real authorization-code login so id_token_hint is
valid.
"""

from __future__ import annotations

import secrets
import re
import urllib.parse
from pathlib import Path

import pytest
import requests

from utils.cli_helper import CliHelper
from utils.oidc_client import OidcClient, generate_pkce
from .oidc_helpers import (
    BASE_URL,
    LOGOUT_REDIRECT_URI,
    REDIRECT_URI,
    RUN_SUFFIX,
    create_client_with_secret,
    delete_client,
    follow_authorize_redirects,
    get_client_internal_id,
    get_default_realm_id,
    parse_callback,
    set_auto_approval,
)


def _resolve_end_session(session: requests.Session, end_session_endpoint: str, params: dict) -> str:
    """Resolve the end-session chain without following external redirects blindly.

    The OP may return either:
      * an HTML front-channel page (HTTP 200) that links to ``/logout/final?ref=...``,
        or
      * an immediate 302/303 redirect to ``/logout/final?ref=...``.

    The final response from ``/logout/final`` is a 302/303 to the registered
    ``post_logout_redirect_uri`` (possibly external). This helper returns the
    final ``Location`` header value as a string, leaving any follow-up RP
    navigation to the caller.
    """
    resp = session.get(end_session_endpoint, params=params, allow_redirects=False)
    assert resp.status_code in (200, 302, 303), (
        f"unexpected end_session response {resp.status_code}: {resp.text[:200]}"
    )

    location = ""
    if resp.status_code in (302, 303):
        location = resp.headers.get("location", "")
        # An immediate redirect straight to the registered post_logout_redirect_uri
        # is the simplest chain (no intermediate /logout/final).
        if LOGOUT_REDIRECT_URI.split("//", 1)[1].split("/")[0] in location:
            return location
        # Otherwise the 302 likely points at the local /logout/final endpoint.
        final_url = urllib.parse.urljoin(BASE_URL + "/", location)
        final_resp = session.get(final_url, allow_redirects=False)
    else:
        # HTML front-channel page: extract the /logout/final?ref=... link and
        # request it once locally.
        match = re.search(r"/logout/final\?ref=([^'\"]+)", resp.text)
        assert match, (
            f"expected intermediate logout page to reference /logout/final, "
            f"got: {resp.text[:200]}"
        )
        final_resp = session.get(
            f"{BASE_URL}/logout/final",
            params={"ref": match.group(1)},
            allow_redirects=False,
        )

    assert final_resp.status_code in (302, 303), (
        f"expected final redirect from /logout/final, got {final_resp.status_code}: "
        f"{final_resp.text[:200]}"
    )
    return final_resp.headers.get("location", "")


class TestEndSessionHappyPath:
    """RP-initiated logout with registered post_logout_redirect_uri."""

    _cid = f"e2e-logout-{RUN_SUFFIX}"
    _client_secret: str | None = None
    _id_token: str | None = None

    def test_01_discovery(self, oidc_client: OidcClient):
        assert oidc_client.endpoint("end_session_endpoint"), "end_session_endpoint missing"

    def test_02_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path,
                                 authenticated_context):
        delete_client(cli_logged_in, self._cid)
        realm_id = get_default_realm_id(cli_logged_in)
        creds = create_client_with_secret(
            cli_logged_in,
            client_id=self._cid,
            client_name=f"E2E Logout {RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid profile",
            grant_types=["authorization_code", "refresh_token"],
            redirect_uris=[REDIRECT_URI],
            logout_redirect_uris=[LOGOUT_REDIRECT_URI],
            require_pkce=True,
            require_consent=False,
            cred_path=tmp_path / "logout-creds.json",
        )
        TestEndSessionHappyPath._client_secret = creds["initialSecret"]
        internal_id = get_client_internal_id(cli_logged_in, self._cid)
        set_auto_approval(authenticated_context, internal_id)

    def test_03_obtain_id_token(self, oidc_client: OidcClient, authenticated_context):
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        verifier, challenge = generate_pkce()
        auth_url = oidc_client.build_authorize_url(
            client_id=self._cid,
            redirect_uri=REDIRECT_URI,
            scope="openid profile",
            code_challenge=challenge,
            state=secrets.token_urlsafe(16),
            nonce=secrets.token_urlsafe(16),
        )
        callback = follow_authorize_redirects(authenticated_context, auth_url)
        params = parse_callback(callback)
        assert params.get("code"), f"no code in {callback}"
        tok = oidc_client.token_authorization_code(
            params["code"], REDIRECT_URI, self._cid,
            client_secret=self._client_secret, code_verifier=verifier,
            auth_method="basic",
        )
        assert tok.ok, f"token exchange failed: {tok.status_code} {tok.raw}"
        assert tok.id_token, "no id_token returned"
        TestEndSessionHappyPath._id_token = tok.id_token

    def test_04_logout_redirects_to_registered_uri(self, oidc_client: OidcClient):
        if not self._id_token:
            pytest.skip("No id_token")
        state = secrets.token_urlsafe(12)
        status, body = oidc_client.end_session(
            id_token_hint=self._id_token,
            post_logout_redirect_uri=LOGOUT_REDIRECT_URI,
            state=state,
            client_id=self._cid,
        )
        assert status in (200, 302, 303), f"unexpected end_session response {status}: {body[:200]}"
        location = _resolve_end_session(
            oidc_client.session,
            oidc_client.end_session_endpoint,
            {
                "id_token_hint": self._id_token,
                "post_logout_redirect_uri": LOGOUT_REDIRECT_URI,
                "state": state,
                "client_id": self._cid,
            },
        )

        assert LOGOUT_REDIRECT_URI.split("//")[1].split("/")[0] in location, (
            f"expected final logout redirect to {LOGOUT_REDIRECT_URI}, got {location}"
        )
        assert (
            f"state={urllib.parse.quote(state)}" in location
            or f"state={state}" in location
        ), f"expected state {state} in final location, got {location}"

    def test_05_unregistered_redirect_not_followed(self, oidc_client: OidcClient):
        """An unregistered post_logout_redirect_uri must not be honoured."""
        if not self._id_token:
            pytest.skip("No id_token")
        resp = oidc_client.session.get(
            oidc_client.end_session_endpoint,
            params={
                "id_token_hint": self._id_token,
                "post_logout_redirect_uri": "https://evil.example.com/steal",
                "client_id": self._cid,
            },
            allow_redirects=False,
        )
        location = resp.headers.get("location", "")
        assert "evil.example.com" not in location, (
            f"open-redirect: server forwarded to unregistered uri {location}"
        )

    def test_99_cleanup(self, cli_logged_in: CliHelper):
        delete_client(cli_logged_in, self._cid)


class TestCheckSession:
    """check_session_iframe / front-channel logout discovery."""

    def test_01_endsession_no_params_renders_page(self, oidc_client: OidcClient):
        """end_session without parameters should render a logout page, not error."""
        resp = oidc_client.session.get(
            oidc_client.end_session_endpoint, allow_redirects=False
        )
        assert resp.status_code in (200, 302), resp.status_code

    def test_02_check_session_iframe_advertised(self, oidc_client: OidcClient):
        disco = oidc_client.discovery
        assert disco.get("check_session_iframe"), "check_session_iframe must be advertised"
        assert disco.get("frontchannel_logout_supported") is True
