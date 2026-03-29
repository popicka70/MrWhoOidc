"""
License-gated feature tests.

These tests install an Enterprise+ license and verify that license-gated
features become available — discovery advertises them, endpoints respond,
and relevant admin UI pages load.

Prefix: e2e-licensed
"""

from __future__ import annotations

import json
import os

import pytest
import requests

from playwright.sync_api import BrowserContext, Page

BASE_URL: str = os.getenv("BASE_URL", "https://localhost:8443")
DISCOVERY_URL = f"{BASE_URL}/t/default/.well-known/openid-configuration"


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _fetch_discovery() -> dict:
    """Fetch the OpenID Connect discovery document."""
    resp = requests.get(DISCOVERY_URL, verify=False, timeout=10)
    resp.raise_for_status()
    return resp.json()


def _goto_admin(page: Page, path: str) -> bool:
    """Navigate to an admin page; return True if the page loaded (no redirect away)."""
    resp = page.goto(path, wait_until="domcontentloaded")
    if resp and resp.status >= 400:
        return False
    # Check we didn't get redirected to login
    if "/login" in page.url.lower():
        return False
    return True


# ---------------------------------------------------------------------------
# Discovery tests — verify features appear after license installation
# ---------------------------------------------------------------------------


class TestDiscoveryWithLicense:
    """Verify that the discovery document reflects licensed features."""

    @pytest.fixture(autouse=True)
    def _require_license(self, install_enterprise_license: str) -> None:
        """Ensure the Enterprise+ license is installed before running these tests."""

    def test_discovery_includes_par_endpoint(self):
        """PAR endpoint should be advertised when AdvancedSecurity feature is enabled."""
        disco = _fetch_discovery()
        assert "pushed_authorization_request_endpoint" in disco, (
            "Discovery should include pushed_authorization_request_endpoint with Enterprise+ license"
        )
        assert disco["pushed_authorization_request_endpoint"].endswith("/par")

    def test_discovery_includes_token_exchange_grant(self):
        """Token exchange grant type should be in grant_types_supported."""
        disco = _fetch_discovery()
        grants = disco.get("grant_types_supported", [])
        assert "urn:ietf:params:oauth:grant-type:token-exchange" in grants, (
            f"Token exchange grant not found in grant_types_supported: {grants}"
        )

    def test_discovery_includes_device_authorization(self):
        """Device authorization endpoint should be advertised."""
        disco = _fetch_discovery()
        assert "device_authorization_endpoint" in disco, (
            "Discovery should include device_authorization_endpoint with Enterprise+ license"
        )

    def test_discovery_includes_dpop_algs(self):
        """DPoP signing algorithms should be advertised."""
        disco = _fetch_discovery()
        dpop_algs = disco.get("dpop_signing_alg_values_supported", [])
        assert "ES256" in dpop_algs, f"ES256 not in dpop_signing_alg_values_supported: {dpop_algs}"

    def test_discovery_includes_jar_support(self):
        """JAR (request parameter) should be advertised."""
        disco = _fetch_discovery()
        assert disco.get("request_parameter_supported") is True
        assert disco.get("request_uri_parameter_supported") is True
        assert "request_object_signing_alg_values_supported" in disco

    def test_discovery_includes_jarm_response_modes(self):
        """JARM response modes (*.jwt) should be advertised."""
        disco = _fetch_discovery()
        modes = disco.get("response_modes_supported", [])
        jwt_modes = [m for m in modes if m.endswith(".jwt")]
        assert len(jwt_modes) >= 3, f"Expected at least 3 JARM modes, got: {jwt_modes}"


# ---------------------------------------------------------------------------
# License admin API/UI tests
# ---------------------------------------------------------------------------


class TestLicenseInstalled:
    """Verify the license is visible through admin API and UI after installation."""

    @pytest.fixture(autouse=True)
    def _require_license(self, install_enterprise_license: str) -> None:
        pass

    def test_license_api_returns_installed_license(self, authenticated_context: BrowserContext):
        """GET /admin/api/license should return the installed license."""
        api = authenticated_context.request
        resp = api.get(f"{BASE_URL}/admin/api/license")
        assert resp.status == 200, f"Expected 200, got {resp.status}: {resp.text()}"
        data = resp.json()
        assert data.get("tier", "").lower() in ("enterprise+", "enterpriseplus", "enterprise_plus"), (
            f"Unexpected tier: {data.get('tier')}"
        )

    def test_license_page_shows_enterprise_plus(self, authenticated_page: Page):
        """Admin license page should display the Enterprise+ tier."""
        ok = _goto_admin(authenticated_page, f"{BASE_URL}/admin/license")
        assert ok, "Failed to load /admin/license"
        content = authenticated_page.content()
        assert any(t in content.lower() for t in ("enterprise+", "enterprise plus", "enterpriseplus")), (
            "License page should show Enterprise+ tier"
        )

    def test_license_history_has_entry(self, authenticated_context: BrowserContext):
        """License history should have at least one entry after installation."""
        api = authenticated_context.request
        resp = api.get(f"{BASE_URL}/admin/api/license/history")
        assert resp.status == 200
        data = resp.json()
        entries = data.get("entries") or data.get("items") or data.get("history") or []
        assert len(entries) >= 1, "Expected at least one license history entry"


# ---------------------------------------------------------------------------
# Feature-gated endpoint tests
# ---------------------------------------------------------------------------


class TestFeatureGatedEndpoints:
    """Verify that license-gated API endpoints respond correctly."""

    @pytest.fixture(autouse=True)
    def _require_license(self, install_enterprise_license: str) -> None:
        pass

    def test_par_endpoint_reachable(self):
        """POST /t/default/par should not return 403 (feature_disabled)."""
        # Send a minimal (invalid) PAR request — we expect a 400 (bad request)
        # not a 403 (feature disabled)
        resp = requests.post(
            f"{BASE_URL}/t/default/par",
            data={"client_id": "nonexistent"},
            verify=False,
            timeout=10,
        )
        assert resp.status_code != 403, (
            f"PAR endpoint returned 403 — feature gate still active: {resp.text}"
        )
        # 400 or 401 is expected for an invalid request
        assert resp.status_code in (400, 401), (
            f"Unexpected status from PAR: {resp.status_code}"
        )

    def test_license_tiers_api(self, authenticated_context: BrowserContext):
        """GET /admin/api/license/tiers should list available tiers."""
        api = authenticated_context.request
        resp = api.get(f"{BASE_URL}/admin/api/license/tiers")
        assert resp.status == 200
        tiers = resp.json()
        tier_names = [t.get("name", "").lower() for t in tiers] if isinstance(tiers, list) else []
        assert any("enterprise" in n for n in tier_names), f"No enterprise tier found in: {tier_names}"

    def test_license_usage_api(self, authenticated_context: BrowserContext):
        """GET /admin/api/license/usage should return feature usage data."""
        api = authenticated_context.request
        resp = api.get(f"{BASE_URL}/admin/api/license/usage")
        assert resp.status == 200
