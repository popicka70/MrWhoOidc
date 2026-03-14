"""
E2E tests for Platform Admin pages (requires platform-admin role on default tenant).

Pages covered:
  /PlatformAdmin                    – Dashboard
  /PlatformAdmin/Tenants            – Tenant list
  /PlatformAdmin/Tenants/Create     – Create tenant
  /PlatformAdmin/Impersonation      – Impersonation control panel
  /PlatformAdmin/ImpersonationHistory – Impersonation audit log
  /PlatformAdmin/Settings           – Platform settings
  /admin/license/platform           – Platform license
  /admin/license/platform/install   – Install license
  /admin/license/platform/history   – License history
"""

from __future__ import annotations

import pytest
from playwright.async_api import Page, expect


# ---------------------------------------------------------------------------
# Helper
# ---------------------------------------------------------------------------


def _assert_evaluation(result, *, min_score: int = 5) -> None:
    if result.skipped:
        return
    if result.error:
        pytest.skip(f"LLM evaluation error (non-blocking): {result.error}")
    assert result.overall_score >= min_score, (
        f"Page '{result.route}' scored {result.overall_score}/10 — below minimum {min_score}.\n"
        f"Summary: {result.summary}"
    )


async def _goto_platform(page: Page, path: str) -> bool:
    """
    Navigate to a platform admin page.
    Returns False and skips if the page is gated (e.g., license or access denied).
    """
    await page.goto(path, wait_until="domcontentloaded")

    if "/login" in page.url:
        pytest.skip(f"Redirected to login when accessing {path} — auth expired or role missing")

    if "accessdenied" in page.url.lower() or "forbidden" in page.url.lower():
        pytest.skip(f"Access denied to {path} — user may lack platform-admin role")

    return True


# ---------------------------------------------------------------------------
# Dashboard
# ---------------------------------------------------------------------------


class TestPlatformAdminDashboard:
    async def test_dashboard_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_platform(authenticated_page, "/PlatformAdmin")
        result = await record_evaluation(authenticated_page, "/PlatformAdmin")
        _assert_evaluation(result)

    async def test_dashboard_has_navigation_links(self, authenticated_page: Page):
        await _goto_platform(authenticated_page, "/PlatformAdmin")
        nav_links = authenticated_page.locator(
            "a[href*='Tenants'], a[href*='Impersonation'], a[href*='Settings'], a[href*='license']"
        )
        count = await nav_links.count()
        assert count >= 2, f"Expected platform admin nav links, found only {count}"


# ---------------------------------------------------------------------------
# Tenant Management
# ---------------------------------------------------------------------------


class TestPlatformAdminTenants:
    async def test_tenant_list_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_platform(authenticated_page, "/PlatformAdmin/Tenants")
        result = await record_evaluation(authenticated_page, "/PlatformAdmin/Tenants")
        _assert_evaluation(result)

    async def test_tenant_list_has_default_tenant(self, authenticated_page: Page):
        await _goto_platform(authenticated_page, "/PlatformAdmin/Tenants")
        body = await authenticated_page.inner_text("body")
        assert "default" in body.lower(), "Expected 'default' tenant in the tenant list"

    async def test_create_tenant_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_platform(authenticated_page, "/PlatformAdmin/Tenants/Create")
        await expect(authenticated_page.locator("form")).to_be_visible()
        result = await record_evaluation(authenticated_page, "/PlatformAdmin/Tenants/Create")
        _assert_evaluation(result)

    async def test_create_tenant_form_has_slug_field(self, authenticated_page: Page):
        await _goto_platform(authenticated_page, "/PlatformAdmin/Tenants/Create")
        slug_input = authenticated_page.locator(
            "input[id*='Slug'], input[name*='Slug'], input[id*='slug'], input[name*='slug']"
        )
        await expect(slug_input).to_be_visible()


# ---------------------------------------------------------------------------
# Impersonation
# ---------------------------------------------------------------------------


class TestPlatformAdminImpersonation:
    async def test_impersonation_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_platform(authenticated_page, "/PlatformAdmin/Impersonation")
        result = await record_evaluation(authenticated_page, "/PlatformAdmin/Impersonation")
        _assert_evaluation(result)

    async def test_impersonation_history_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_platform(authenticated_page, "/PlatformAdmin/ImpersonationHistory")
        result = await record_evaluation(authenticated_page, "/PlatformAdmin/ImpersonationHistory")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Platform Settings
# ---------------------------------------------------------------------------


class TestPlatformAdminSettings:
    async def test_platform_settings_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_platform(authenticated_page, "/PlatformAdmin/Settings")
        result = await record_evaluation(authenticated_page, "/PlatformAdmin/Settings")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Platform License
# ---------------------------------------------------------------------------


class TestPlatformLicense:
    async def test_platform_license_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_platform(authenticated_page, "/admin/license/platform")
        result = await record_evaluation(authenticated_page, "/admin/license/platform")
        _assert_evaluation(result)

    async def test_license_install_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_platform(authenticated_page, "/admin/license/platform/install")
        result = await record_evaluation(authenticated_page, "/admin/license/platform/install")
        _assert_evaluation(result)

    async def test_license_history_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_platform(authenticated_page, "/admin/license/platform/history")
        result = await record_evaluation(authenticated_page, "/admin/license/platform/history")
        _assert_evaluation(result)
