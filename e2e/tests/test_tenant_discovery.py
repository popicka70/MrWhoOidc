"""
P12 — Multi-tenant discovery (gap G17).

With MultiTenancy.Enabled=true and DefaultTenantSlug=default the OP serves a
tenant-scoped discovery document at /t/{slug}/.well-known/openid-configuration
whose issuer and endpoints are tenant-prefixed. Unknown tenants 404.
"""

from __future__ import annotations

import pytest

from utils.oidc_client import OidcClient
from .oidc_helpers import BASE_URL

_DEFAULT_SLUG = "default"


def _discovery(oidc_client: OidcClient, path: str):
    return oidc_client.raw_get(f"{BASE_URL}{path}")


class TestTenantDiscovery:
    def test_01_tenant_discovery_present(self, oidc_client: OidcClient):
        status, body = _discovery(
            oidc_client, f"/t/{_DEFAULT_SLUG}/.well-known/openid-configuration"
        )
        assert status == 200, f"tenant discovery returned {status}"
        assert isinstance(body, dict), "discovery should be JSON object"
        self._doc = body

    def test_02_issuer_is_tenant_scoped(self, oidc_client: OidcClient):
        status, body = _discovery(
            oidc_client, f"/t/{_DEFAULT_SLUG}/.well-known/openid-configuration"
        )
        assert status == 200
        issuer = body.get("issuer", "")
        assert issuer.rstrip("/").endswith(f"/t/{_DEFAULT_SLUG}"), (
            f"issuer '{issuer}' should be tenant-scoped"
        )

    def test_03_endpoints_are_tenant_prefixed(self, oidc_client: OidcClient):
        status, body = _discovery(
            oidc_client, f"/t/{_DEFAULT_SLUG}/.well-known/openid-configuration"
        )
        assert status == 200
        for key in ("authorization_endpoint", "token_endpoint", "jwks_uri"):
            value = body.get(key, "")
            assert value, f"{key} missing from tenant discovery"
            assert f"/t/{_DEFAULT_SLUG}/" in value, (
                f"{key} '{value}' should be tenant-prefixed"
            )

    def test_04_unknown_tenant_404(self, oidc_client: OidcClient):
        status, _ = _discovery(
            oidc_client,
            "/t/no-such-tenant-zzz/.well-known/openid-configuration",
        )
        assert status == 404, (
            f"Unknown tenant discovery should be 404, got {status}"
        )
