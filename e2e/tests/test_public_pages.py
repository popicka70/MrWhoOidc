"""
E2E tests for publicly accessible (unauthenticated) pages.

Pages covered:
  /         -- Home / Landing
  /login    -- Login page
  /Privacy  -- Privacy policy
  /Account/ForgotPassword -- Forgot password
  /select-tenant          -- Tenant selector
  /.well-known/openid-configuration -- OIDC discovery
  /jwks                   -- JWKS endpoint
  /NotFound               -- 404 handler (any unknown path)
"""

from __future__ import annotations

import pytest
from playwright.sync_api import Page, expect


def _assert_evaluation(result, *, min_score: int = 4) -> None:
    if result.skipped:
        return
    if result.error:
        pytest.skip(f"LLM evaluation error: {result.error}")
    assert result.overall_score >= min_score, (
        f"Page '{result.route}' scored {result.overall_score}/10 -- below {min_score}.\n"
        f"Summary: {result.summary}"
    )


class TestHomePage:
    def test_home_page_loads(self, page: Page, record_evaluation):
        page.goto("/", wait_until="domcontentloaded")
        expect(page).to_have_title(lambda t: len(t) > 0)
        assert page.locator("nav, aside, header").count() > 0, "No navigation element found"
        result = record_evaluation(page, "/")
        _assert_evaluation(result, min_score=5)

    def test_home_page_discovery_link(self, page: Page):
        page.goto("/", wait_until="domcontentloaded")
        links = page.locator(
            "a[href*='openid-configuration'], a[href*='jwks'], a[href*='discovery']"
        ).count()
        assert links > 0, "No OIDC discovery or JWKS link on home page"

    def test_home_page_no_console_errors(self, page: Page):
        errors: list[str] = []
        page.on("console", lambda msg: errors.append(msg.text) if msg.type == "error" else None)
        page.goto("/", wait_until="domcontentloaded")
        hard_errors = [e for e in errors if "favicon" not in e.lower()]
        assert not hard_errors, f"Console errors on home page: {hard_errors}"


class TestLoginPage:
    def test_login_page_loads(self, page: Page, record_evaluation):
        page.goto("/login", wait_until="domcontentloaded")
        expect(page.locator("input#Username")).to_be_visible()
        expect(page.locator("input#Password")).to_be_visible()
        expect(page.locator("button[type='submit']")).to_be_visible()
        result = record_evaluation(page, "/login")
        _assert_evaluation(result, min_score=5)

    def test_login_page_invalid_credentials_shows_error(self, page: Page, record_evaluation):
        page.goto("/login", wait_until="domcontentloaded")
        page.locator("input#Username").fill("nonexistent@bad.local")
        page.locator("input#Password").fill("WrongPassword999!")
        page.locator("button[type='submit']").click()
        assert "/login" in page.url, f"Expected to stay on /login, got {page.url}"
        error_element = page.locator(".alert-danger, .text-danger, [role='alert']").first
        expect(error_element).to_be_visible()
        record_evaluation(page, "/login", label="error-state")

    def test_login_password_field_masked(self, page: Page):
        page.goto("/login", wait_until="domcontentloaded")
        field_type = page.locator("input#Password").get_attribute("type")
        assert field_type == "password", "Password field must be type='password'"

    def test_login_page_title(self, page: Page):
        page.goto("/login", wait_until="domcontentloaded")
        title = page.title()
        assert (
            "login" in title.lower() or "sign" in title.lower() or "mrwho" in title.lower()
        ), f"Unexpected page title: {title}"


class TestPrivacyPage:
    def test_privacy_page_loads(self, page: Page, record_evaluation):
        page.goto("/Privacy", wait_until="domcontentloaded")
        title = page.title()
        assert len(title) > 0, "Privacy page has no title"
        content = page.inner_text("body")
        assert len(content) > 50, "Privacy page appears to have no content"
        result = record_evaluation(page, "/Privacy")
        _assert_evaluation(result, min_score=4)


class TestForgotPasswordPage:
    def test_forgot_password_page_loads(self, page: Page, record_evaluation):
        page.goto("/Account/ForgotPassword", wait_until="domcontentloaded")
        # May redirect to login or show a form -- both are valid
        if "/login" in page.url:
            pytest.skip("ForgotPassword redirects to login (auth required in this config)")
        result = record_evaluation(page, "/Account/ForgotPassword")
        _assert_evaluation(result, min_score=3)


class TestSelectTenantPage:
    def test_select_tenant_page_loads(self, page: Page, record_evaluation):
        page.goto("/select-tenant", wait_until="domcontentloaded")
        # May redirect if single-tenant
        if "/login" in page.url or "/" == page.url.rstrip("/").split("://", 1)[-1].split("/", 1)[-1:][0:1]:
            pytest.skip("select-tenant redirects (single-tenant mode)")
        result = record_evaluation(page, "/select-tenant")
        _assert_evaluation(result, min_score=3)


class TestNotFoundPage:
    def test_not_found_page_loads(self, page: Page, record_evaluation):
        page.goto("/this-page-definitely-does-not-exist-xyz123", wait_until="domcontentloaded")
        # Should show 404 / NotFound page, not a crash
        body = page.inner_text("body")
        assert len(body) > 10, "404 page has no content"
        result = record_evaluation(page, "/not-found")
        _assert_evaluation(result, min_score=2)


class TestDiscoveryEndpoints:
    def test_oidc_discovery_returns_json(self, page: Page):
        response = page.request.get("/.well-known/openid-configuration")
        assert response.status == 200, f"Discovery endpoint returned {response.status}"
        data = response.json()
        assert "issuer" in data, "Discovery JSON missing 'issuer'"
        assert "authorization_endpoint" in data, "Discovery JSON missing 'authorization_endpoint'"
        assert "jwks_uri" in data, "Discovery JSON missing 'jwks_uri'"

    def test_jwks_endpoint_returns_keys(self, page: Page):
        response = page.request.get("/jwks")
        assert response.status == 200, f"JWKS endpoint returned {response.status}"
        data = response.json()
        assert "keys" in data, "JWKS response missing 'keys' array"
        assert len(data["keys"]) > 0, "JWKS endpoint returned empty keys array"
