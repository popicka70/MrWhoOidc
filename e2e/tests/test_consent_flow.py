"""
P3 — Interactive consent screen (gap G3).

Drives the ``/consent`` page in a real browser:
  provision a client with RequireConsent=true (no auto-approval) →
  navigate /authorize → land on consent → assert client + scopes shown →
  Deny → assert access_denied redirect →
  Allow → assert authorization code returned →
  re-authorize → consent remembered (no second prompt).

Uses a page-scoped route to satisfy the (non-routable) callback host so the
browser navigation to the redirect URI completes and the URL can be read.
"""

from __future__ import annotations

import secrets
import urllib.parse
from pathlib import Path

import pytest

from utils.cli_helper import CliHelper
from utils.oidc_client import OidcClient, generate_pkce
from .oidc_helpers import (
    BASE_URL,
    REDIRECT_URI,
    RUN_SUFFIX,
    create_client_with_secret,
    delete_client,
    get_client_internal_id,
    get_default_realm_id,
    parse_callback,
    set_auto_approval,
)

_LOCAL_CALLBACK_URI = f"{BASE_URL}/t/default/account"


def _drive_authorize_until_settled(authenticated_context, auth_url: str):
    """Open a page, follow the authorize flow, and return the settled page."""
    page = authenticated_context.new_page()
    page.goto(auth_url, wait_until="domcontentloaded")
    return page


class TestConsentScreen:
    """Interactive consent: deny, allow, and remembered consent."""

    _cid = f"e2e-consent-{RUN_SUFFIX}"
    _client_secret: str | None = None
    _scope = "openid profile email"

    def test_01_provision_client(self, cli_logged_in: CliHelper, tmp_path: Path,
                                 authenticated_context):
        """Confidential client requiring interactive consent."""
        delete_client(cli_logged_in, self._cid)
        realm_id = get_default_realm_id(cli_logged_in)
        creds = create_client_with_secret(
            cli_logged_in,
            client_id=self._cid,
            client_name=f"E2E Consent {RUN_SUFFIX}",
            realm_id=realm_id,
            scope=self._scope,
            grant_types=["authorization_code", "refresh_token"],
            redirect_uris=[_LOCAL_CALLBACK_URI],
            require_pkce=True,
            require_consent=True,
            cred_path=tmp_path / "consent-creds.json",
        )
        TestConsentScreen._client_secret = creds["initialSecret"]
        internal_id = get_client_internal_id(cli_logged_in, self._cid)
        set_auto_approval(authenticated_context, internal_id)

    def test_02_deny_returns_access_denied(self, oidc_client: OidcClient,
                                           authenticated_context):
        """Clicking Deny redirects to the client with error=access_denied."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        verifier, challenge = generate_pkce()
        state = secrets.token_urlsafe(16)
        auth_url = oidc_client.build_authorize_url(
            client_id=self._cid,
            redirect_uri=_LOCAL_CALLBACK_URI,
            scope=self._scope,
            code_challenge=challenge,
            state=state,
            extra_params={"prompt": "consent"},
        )
        page = _drive_authorize_until_settled(authenticated_context, auth_url)
        try:
            assert "/consent" in page.url.lower(), f"expected consent page, got {page.url}"
            # The requesting client and at least one scope must be shown.
            body = page.content()
            assert self._cid in body
            assert "openid" in body
            deny = page.locator("a.btn-outline-danger").first
            assert deny.count() > 0, "Deny link not found"
            deny.click()
            page.wait_for_load_state("domcontentloaded")
            params = parse_callback(page.url)
            assert params.get("error") == "access_denied", f"got {page.url}"
            assert params.get("state") == state
        finally:
            page.close()

    def test_03_allow_returns_code(self, oidc_client: OidcClient, authenticated_context):
        """Clicking Allow grants consent and returns an authorization code."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        verifier, challenge = generate_pkce()
        state = secrets.token_urlsafe(16)
        auth_url = oidc_client.build_authorize_url(
            client_id=self._cid,
            redirect_uri=_LOCAL_CALLBACK_URI,
            scope=self._scope,
            code_challenge=challenge,
            state=state,
            extra_params={"prompt": "consent"},
        )
        page = _drive_authorize_until_settled(authenticated_context, auth_url)
        try:
            assert "/consent" in page.url.lower(), f"expected consent page, got {page.url}"
            allow = page.locator("button[type='submit'].btn-success").first
            assert allow.count() > 0, "Allow button not found"
            allow.click()
            page.wait_for_load_state("domcontentloaded")
            params = parse_callback(page.url)
            assert params.get("code"), f"expected code, got {page.url}"
            assert params.get("state") == state
            # The captured code should exchange for tokens.
            tok = oidc_client.token_authorization_code(
                params["code"],
                _LOCAL_CALLBACK_URI,
                self._cid,
                client_secret=self._client_secret,
                code_verifier=verifier,
                auth_method="basic",
            )
            assert tok.ok, f"code exchange failed: {tok.status_code} {tok.raw}"
        finally:
            page.close()

    def test_04_consent_remembered(self, oidc_client: OidcClient, authenticated_context):
        """After granting, a normal authorize (no prompt=consent) should not
        re-prompt — consent is persisted for the user+client."""
        if not self._client_secret:
            pytest.skip("Client not provisioned")
        verifier, challenge = generate_pkce()
        state = secrets.token_urlsafe(16)
        auth_url = oidc_client.build_authorize_url(
            client_id=self._cid,
            redirect_uri=_LOCAL_CALLBACK_URI,
            scope=self._scope,
            code_challenge=challenge,
            state=state,
        )
        page = _drive_authorize_until_settled(authenticated_context, auth_url)
        try:
            page.wait_for_load_state("domcontentloaded")
            # Should have flowed straight to the callback without a consent stop.
            params = parse_callback(page.url)
            assert "/consent" not in page.url.lower(), (
                f"unexpected re-prompt for consent: {page.url}"
            )
            assert params.get("code"), f"expected code on remembered consent, got {page.url}"
        finally:
            page.close()

    def test_99_cleanup(self, cli_logged_in: CliHelper):
        delete_client(cli_logged_in, self._cid)
