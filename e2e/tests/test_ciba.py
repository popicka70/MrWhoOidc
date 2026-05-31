"""
P13 — CIBA backchannel authentication (gap G19).

Enabled in the dev stack via Auth__EnableCiba=true. Exercises the initiation
path of OpenID Connect CIBA Core 1.0 (poll mode):

  POST {backchannel_authentication_endpoint} -> auth_req_id
  POST {token_endpoint} grant=ciba           -> authorization_pending

Full approval needs an out-of-band user decision, so only the initiation and
the pending-poll contract are asserted here. Skips when CIBA is not advertised.
"""

from __future__ import annotations

import base64
from pathlib import Path

import pytest

from utils.cli_helper import CliHelper
from utils.oidc_client import OidcClient
from .oidc_helpers import (
    E2E_PREFIX,
    RUN_SUFFIX,
    create_client_with_secret,
    delete_client,
    get_default_realm_id,
)

CIBA_GRANT = "urn:openid:params:grant-type:ciba"


def _basic_auth(client_id: str, secret: str) -> str:
    raw = f"{client_id}:{secret}".encode()
    return "Basic " + base64.b64encode(raw).decode()


class TestCiba:
    _client_id = f"{E2E_PREFIX}-ciba-{RUN_SUFFIX}"
    _secret: str | None = None

    def test_01_discovery_advertises_ciba(self, oidc_client: OidcClient):
        endpoint = oidc_client.endpoint("backchannel_authentication_endpoint")
        if not endpoint:
            pytest.skip("backchannel_authentication_endpoint not advertised")
        modes = oidc_client.discovery.get("backchannel_token_delivery_modes_supported")
        assert modes, "backchannel_token_delivery_modes_supported should be present"
        assert "poll" in modes, "poll delivery mode expected for these tests"

    def test_02_provision_ciba_client(self, cli_logged_in: CliHelper, tmp_path: Path,
                                      oidc_client: OidcClient):
        if not oidc_client.endpoint("backchannel_authentication_endpoint"):
            pytest.skip("CIBA not advertised")
        realm_id = get_default_realm_id(cli_logged_in)
        creds = create_client_with_secret(
            cli_logged_in,
            client_id=self._client_id,
            client_name="E2E CIBA Client",
            realm_id=realm_id,
            scope="openid profile",
            grant_types=[CIBA_GRANT],
            require_consent=False,
            cred_path=str(tmp_path / "ciba-client.json"),
        )
        type(self)._secret = creds["initialSecret"]
        assert self._secret

    def test_03_initiate_returns_auth_req_id(self, oidc_client: OidcClient):
        endpoint = oidc_client.endpoint("backchannel_authentication_endpoint")
        if not endpoint or not self._secret:
            pytest.skip("CIBA client not provisioned")
        status, body = oidc_client.raw_post(
            endpoint,
            data={
                "scope": "openid profile",
                "login_hint": "admin@mrwho.local",
            },
            headers={"Authorization": _basic_auth(self._client_id, self._secret)},
            allow_redirects=False,
        )
        assert status == 200, f"CIBA initiation failed ({status}): {body}"
        assert isinstance(body, dict)
        assert body.get("auth_req_id"), "auth_req_id missing from CIBA response"
        assert int(body.get("expires_in", 0)) > 0
        assert int(body.get("interval", 0)) >= 0
        type(self)._auth_req_id = body["auth_req_id"]

    def test_04_poll_pending(self, oidc_client: OidcClient):
        endpoint = oidc_client.endpoint("backchannel_authentication_endpoint")
        auth_req_id = getattr(self, "_auth_req_id", None)
        if not endpoint or not self._secret or not auth_req_id:
            pytest.skip("CIBA request not initiated")
        status, body = oidc_client.raw_token_request(
            data={
                "grant_type": CIBA_GRANT,
                "auth_req_id": auth_req_id,
            },
            headers={"Authorization": _basic_auth(self._client_id, self._secret)},
        )
        assert status == 400, f"Unapproved CIBA poll should be 400, got {status}: {body}"
        error = body.get("error") if isinstance(body, dict) else None
        assert error in ("authorization_pending", "slow_down"), (
            f"Expected pending/slow_down, got '{error}'"
        )

    def test_05_unknown_auth_req_id_rejected(self, oidc_client: OidcClient):
        endpoint = oidc_client.endpoint("backchannel_authentication_endpoint")
        if not endpoint or not self._secret:
            pytest.skip("CIBA client not provisioned")
        status, body = oidc_client.raw_token_request(
            data={
                "grant_type": CIBA_GRANT,
                "auth_req_id": "totally-unknown-auth-req-id",
            },
            headers={"Authorization": _basic_auth(self._client_id, self._secret)},
        )
        assert status == 400
        error = body.get("error") if isinstance(body, dict) else None
        assert error in ("invalid_grant", "expired_token"), (
            f"Unknown auth_req_id should be invalid_grant, got '{error}'"
        )

    def test_99_cleanup(self, cli_logged_in: CliHelper):
        delete_client(cli_logged_in, self._client_id)
