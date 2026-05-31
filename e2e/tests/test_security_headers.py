"""
P9 — Security header coverage (gap G14).

Verifies the security headers applied by ``SecurityHeadersMiddleware`` to HTML
responses, and the special-cased ``/connect/checksession`` iframe which must NOT
send ``X-Frame-Options`` so relying parties can embed it.

These are pure HTTP assertions — no provisioning required — so the class only
depends on a running server (``oidc_client``).
"""

from __future__ import annotations

import pytest

from utils.oidc_client import OidcClient


def _html_get(oidc_client: OidcClient, path: str):
    """GET a path under the issuer and return the raw requests.Response."""
    url = f"{oidc_client.issuer_url}{path}"
    return oidc_client.session.get(url, allow_redirects=False)


class TestSecurityHeaders:
    """Security headers on HTML responses (RFC-agnostic hardening)."""

    def test_01_login_page_core_headers(self, oidc_client: OidcClient):
        """The login page must carry the baseline hardening headers."""
        resp = _html_get(oidc_client, "/login")
        # The login page may redirect (302) to a canonical URL; follow once.
        if resp.status_code in (301, 302, 303, 307, 308):
            loc = resp.headers.get("location", "/login")
            if loc.startswith("/"):
                loc = f"{oidc_client.issuer_url}{loc}"
            resp = oidc_client.session.get(loc, allow_redirects=True)

        ctype = resp.headers.get("Content-Type", "")
        if "text/html" not in ctype:
            pytest.skip(f"Login page did not return HTML (Content-Type={ctype!r})")

        headers = {k.lower(): v for k, v in resp.headers.items()}
        assert headers.get("x-content-type-options") == "nosniff"
        assert headers.get("referrer-policy") == "strict-origin-when-cross-origin"
        assert "geolocation=()" in headers.get("permissions-policy", "")
        assert headers.get("x-xss-protection") == "1; mode=block"

    def test_02_login_page_frame_and_csp(self, oidc_client: OidcClient):
        """Non-checksession HTML must deny framing and ship a CSP with a nonce."""
        resp = _html_get(oidc_client, "/login")
        if resp.status_code in (301, 302, 303, 307, 308):
            loc = resp.headers.get("location", "/login")
            if loc.startswith("/"):
                loc = f"{oidc_client.issuer_url}{loc}"
            resp = oidc_client.session.get(loc, allow_redirects=True)

        if "text/html" not in resp.headers.get("Content-Type", ""):
            pytest.skip("Login page did not return HTML")

        headers = {k.lower(): v for k, v in resp.headers.items()}
        assert headers.get("x-frame-options") == "DENY"

        csp = headers.get("content-security-policy", "")
        assert csp, "Missing Content-Security-Policy on HTML response"
        assert "frame-ancestors 'none'" in csp
        assert "default-src 'self'" in csp
        assert "object-src 'none'" in csp
        assert "'nonce-" in csp, "CSP should include a per-response script nonce"

    def test_03_checksession_iframe_allows_framing(self, oidc_client: OidcClient):
        """The check_session iframe is special-cased: no X-Frame-Options, CSP
        frame-ancestors https: so RP origins can embed it."""
        endpoint = oidc_client.endpoint("check_session_iframe")
        if not endpoint:
            # Fall back to the documented route under the issuer.
            endpoint = f"{oidc_client.issuer_url}/connect/checksession"

        resp = oidc_client.session.get(endpoint, allow_redirects=True)
        if resp.status_code != 200 or "text/html" not in resp.headers.get("Content-Type", ""):
            pytest.skip("check_session iframe not available as HTML")

        headers = {k.lower(): v for k, v in resp.headers.items()}
        # Must NOT forbid framing.
        assert headers.get("x-frame-options") is None, (
            "check_session iframe must not set X-Frame-Options"
        )
        csp = headers.get("content-security-policy", "")
        assert "frame-ancestors https:" in csp, (
            f"Expected relaxed frame-ancestors for the iframe, got CSP={csp!r}"
        )
        # Baseline hardening still applies.
        assert headers.get("x-content-type-options") == "nosniff"

    def test_04_discovery_advertises_frontchannel_logout(self, oidc_client: OidcClient):
        """Discovery should advertise front-channel logout support (paired with
        the check_session iframe)."""
        disco = oidc_client.discovery
        assert disco.get("frontchannel_logout_supported") is True
        assert disco.get("check_session_iframe"), "check_session_iframe should be advertised"
