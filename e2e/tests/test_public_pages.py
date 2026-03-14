"""
E2E tests for publicly accessible (unauthenticated) pages.

Pages covered:
  /         – Home / Landing
  /login    – Login page
  /Privacy  – Privacy policy
"""

from __future__ import annotations

import pytest
from playwright.async_api import Page, expect



class TestHomePage:
    """Tests for the home/landing page."""

    async def test_home_page_loads(self, page: Page, record_evaluation, base_url):
        await page.goto("/", wait_until="domcontentloaded")

        # Basic DOM assertions
        await expect(page).to_have_title(lambda t: len(t) > 0)
        # Check navigation sidebar or header is present
        assert await page.locator("nav, aside, header").count() > 0, "No navigation element found"

        result = await record_evaluation(page, "/")
        _assert_evaluation(result, min_score=5)

    async def test_home_page_discovery_link(self, page: Page, base_url):
        """The home page should have a link to OIDC discovery or JWKS."""
        await page.goto("/", wait_until="domcontentloaded")

        # Look for any link pointing to the OIDC discovery endpoint
        discovery_links = await page.locator(
            "a[href*='openid-configuration'], a[href*='jwks'], a[href*='discovery']"
        ).count()
        assert discovery_links > 0, "No OIDC discovery or JWKS link found on home page"

    async def test_home_page_no_console_errors(self, page: Page, base_url):
        """No JavaScript console errors on the home page."""
        errors: list[str] = []
        page.on("console", lambda msg: errors.append(msg.text) if msg.type == "error" else None)
        await page.goto("/", wait_until="domcontentloaded")
        hard_errors = [e for e in errors if "favicon" not in e.lower()]
        assert not hard_errors, f"Console errors on home page: {hard_errors}"


class TestLoginPage:
    """Tests for the /login page."""

    async def test_login_page_loads(self, page: Page, record_evaluation):
        await page.goto("/login", wait_until="domcontentloaded")

        # Verify key form elements
        await expect(page.locator("input#Username")).to_be_visible()
        await expect(page.locator("input#Password")).to_be_visible()
        await expect(page.locator("button[type='submit']")).to_be_visible()

        result = await record_evaluation(page, "/login")
        _assert_evaluation(result, min_score=5)

    async def test_login_page_invalid_credentials_shows_error(self, page: Page, record_evaluation):
        await page.goto("/login", wait_until="domcontentloaded")

        await page.locator("input#Username").fill("nonexistent@bad.local")
        await page.locator("input#Password").fill("WrongPassword999!")
        await page.locator("button[type='submit']").click()

        # Should stay on login, show error
        assert "/login" in page.url, f"Expected to stay on /login, got {page.url}"
        error_element = page.locator(".alert-danger, .text-danger, [role='alert']").first
        await expect(error_element).to_be_visible()

        result = await record_evaluation(page, "/login", label="error-state")
        # Error state is still valid UI — just capture and record

    async def test_login_password_field_masked(self, page: Page):
        """Password input must mask characters."""
        await page.goto("/login", wait_until="domcontentloaded")
        pwd_input = page.locator("input#Password")
        field_type = await pwd_input.get_attribute("type")
        assert field_type == "password", "Password field must be type='password'"

    async def test_login_page_title(self, page: Page):
        await page.goto("/login", wait_until="domcontentloaded")
        title = await page.title()
        assert "login" in title.lower() or "sign" in title.lower() or "mrwho" in title.lower(), (
            f"Unexpected page title: {title}"
        )


class TestPrivacyPage:
    """Tests for the /Privacy page."""

    async def test_privacy_page_loads(self, page: Page, record_evaluation):
        await page.goto("/Privacy", wait_until="domcontentloaded")

        title = await page.title()
        assert len(title) > 0, "Privacy page has no title"

        content = await page.inner_text("body")
        assert len(content) > 50, "Privacy page appears to have no content"

        result = await record_evaluation(page, "/Privacy")
        _assert_evaluation(result, min_score=4)


class TestDiscoveryEndpoints:
    """Verify the OIDC discovery and JWKS endpoints are reachable."""

    async def test_oidc_discovery_returns_json(self, page: Page):
        response = await page.request.get("/.well-known/openid-configuration")
        assert response.status == 200, f"Discovery endpoint returned {response.status}"
        data = await response.json()
        assert "issuer" in data, "Discovery JSON missing 'issuer' field"
        assert "authorization_endpoint" in data, "Discovery JSON missing 'authorization_endpoint'"
        assert "jwks_uri" in data, "Discovery JSON missing 'jwks_uri'"

    async def test_jwks_endpoint_returns_keys(self, page: Page):
        response = await page.request.get("/jwks")
        assert response.status == 200, f"JWKS endpoint returned {response.status}"
        data = await response.json()
        assert "keys" in data, "JWKS response missing 'keys' array"
        assert len(data["keys"]) > 0, "JWKS endpoint returned empty keys array"


# ---------------------------------------------------------------------------
# Assertion helpers
# ---------------------------------------------------------------------------


def _assert_evaluation(result, *, min_score: int = 4) -> None:
    """For LLM evaluations: log issues but only hard-fail on critical low scores."""
    if result.skipped:
        return  # No API key — gracefully skip
    if result.error:
        pytest.skip(f"LLM evaluation error (non-blocking): {result.error}")

    high_issues = result.high_issues
    if high_issues:
        issues_text = "; ".join(i["description"] for i in high_issues)
        pytest.fail(  # noqa: PT017
            f"Page '{result.route}' has HIGH severity UI issues (score={result.overall_score}):\n{issues_text}"
        ) if result.overall_score < min_score else None

    assert result.overall_score >= min_score, (
        f"Page '{result.route}' scored {result.overall_score}/10 — below minimum {min_score}.\n"
        f"Summary: {result.summary}"
    )
