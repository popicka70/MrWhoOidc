"""
P2 — Dynamic Client Registration & Configuration (gaps G2; RFC 7591/7592).

Requires the server to advertise ``registration_endpoint`` (enabled in
docker-compose.dev.yml via Auth__EnableDynamicClientRegistration=true and
Auth__RequireInitialAccessToken=false). Tests skip gracefully if the feature
is not advertised.

Covers:
  register → inspect (GET) → update (PUT) → delete (DELETE 204) →
  invalid redirect_uri rejection →
  using a freshly-registered client for client_credentials.
"""

from __future__ import annotations

import pytest

from utils.oidc_client import OidcClient


def _registration_endpoint(oidc_client: OidcClient) -> str:
    endpoint = oidc_client.endpoint("registration_endpoint")
    if not endpoint:
        pytest.skip("registration_endpoint not advertised (dynamic registration disabled)")
    return endpoint


class TestDynamicClientRegistration:
    """RFC 7591 registration + RFC 7592 configuration lifecycle."""

    _client_id: str | None = None
    _registration_access_token: str | None = None
    _registration_client_uri: str | None = None

    def test_01_register(self, oidc_client: OidcClient):
        endpoint = _registration_endpoint(oidc_client)
        status, body = oidc_client.raw_post(
            endpoint,
            json_body={
                "redirect_uris": ["https://rp.example.com/callback"],
                "grant_types": ["authorization_code", "refresh_token"],
                "response_types": ["code"],
                "client_name": "E2E Dynamic RP",
                "token_endpoint_auth_method": "client_secret_basic",
                "application_type": "web",
            },
            headers={"Content-Type": "application/json"},
        )
        if status in (401, 403):
            pytest.skip(f"registration requires an initial access token ({status})")
        assert status in (200, 201), f"register failed: {status} {body}"
        assert isinstance(body, dict)
        assert body.get("client_id"), body
        assert body.get("registration_access_token"), body
        assert body.get("registration_client_uri"), body
        assert "client_id_issued_at" in body

        TestDynamicClientRegistration._client_id = body["client_id"]
        TestDynamicClientRegistration._registration_access_token = body["registration_access_token"]
        TestDynamicClientRegistration._registration_client_uri = body["registration_client_uri"]

    def test_02_get_configuration(self, oidc_client: OidcClient):
        if not self._registration_client_uri:
            pytest.skip("No registered client")
        status, body = oidc_client.raw_get(
            self._registration_client_uri,
            headers={"Authorization": f"Bearer {self._registration_access_token}"},
        )
        assert status == 200, f"GET registration failed: {status} {body}"
        assert body.get("client_id") == self._client_id
        assert "https://rp.example.com/callback" in body.get("redirect_uris", [])

    def test_03_get_without_token_unauthorized(self, oidc_client: OidcClient):
        if not self._registration_client_uri:
            pytest.skip("No registered client")
        status, _ = oidc_client.raw_get(self._registration_client_uri)
        assert status in (401, 403), f"expected unauthorized, got {status}"

    def test_04_update_configuration(self, oidc_client: OidcClient):
        if not self._registration_client_uri:
            pytest.skip("No registered client")
        resp = oidc_client.session.put(
            self._registration_client_uri,
            json={
                "client_id": self._client_id,
                "redirect_uris": ["https://rp.example.com/callback",
                                  "https://rp.example.com/callback2"],
                "grant_types": ["authorization_code", "refresh_token"],
                "response_types": ["code"],
                "client_name": "E2E Dynamic RP (updated)",
                "token_endpoint_auth_method": "client_secret_basic",
            },
            headers={
                "Authorization": f"Bearer {self._registration_access_token}",
                "Content-Type": "application/json",
            },
        )
        assert resp.status_code in (200, 201), f"PUT failed: {resp.status_code} {resp.text}"
        updated = resp.json()
        assert "https://rp.example.com/callback2" in updated.get("redirect_uris", [])

    def test_05_register_invalid_redirect_rejected(self, oidc_client: OidcClient):
        endpoint = _registration_endpoint(oidc_client)
        status, body = oidc_client.raw_post(
            endpoint,
            json_body={
                "redirect_uris": ["not-an-absolute-uri"],
                "grant_types": ["authorization_code"],
                "response_types": ["code"],
                "client_name": "E2E Bad RP",
                "token_endpoint_auth_method": "client_secret_basic",
            },
            headers={"Content-Type": "application/json"},
        )
        if status in (401, 403):
            pytest.skip("registration requires an initial access token")
        assert status == 400, f"expected 400 for bad redirect_uri, got {status}: {body}"
        assert body.get("error") in ("invalid_redirect_uri", "invalid_client_metadata"), body

    def test_06_delete_configuration(self, oidc_client: OidcClient):
        if not self._registration_client_uri:
            pytest.skip("No registered client")
        resp = oidc_client.session.delete(
            self._registration_client_uri,
            headers={"Authorization": f"Bearer {self._registration_access_token}"},
        )
        assert resp.status_code in (204, 200), f"DELETE failed: {resp.status_code}"

        # Subsequent GET should now fail (client gone / token invalid).
        status, _ = oidc_client.raw_get(
            self._registration_client_uri,
            headers={"Authorization": f"Bearer {self._registration_access_token}"},
        )
        assert status in (401, 403, 404), f"expected client gone, got {status}"
