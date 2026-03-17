"""
E2E tests for Platform Admin pages (requires platform-admin role).

Pages covered:
  /platform-admin                   -- Dashboard
  /platform-admin/tenants           -- Tenant list
  /platform-admin/tenants/create    -- Create tenant
  /platform-admin/tenants/edit/{id} -- Edit tenant (first found)
  /platform-admin/tenants/import    -- Import tenants
  /platform-admin/impersonation     -- Impersonation control panel
  /platform-admin/impersonation-history -- Impersonation audit log
  /platform-admin/settings          -- Platform settings
  /admin/license/platform           -- Platform license
  /admin/license/platform/install   -- Install license
  /admin/license/platform/history   -- License history
"""

from __future__ import annotations

import re

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


def _goto_platform(page: Page, path: str) -> None:
    page.goto(path, wait_until="domcontentloaded")
    if "/login" in page.url:
        pytest.skip(f"Redirected to login for {path} -- auth expired or role missing")
    url_lower = page.url.lower()
    if "accessdenied" in url_lower or "forbidden" in url_lower or "access-denied" in url_lower:
        pytest.skip(f"Access denied to {path} -- user may lack platform-admin role")


def _extract_guid_from_url(url: str) -> str | None:
    m = re.search(
        r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", url, re.IGNORECASE
    )
    return m.group(0) if m else None


# ---------------------------------------------------------------------------
# Dashboard
# ---------------------------------------------------------------------------


class TestPlatformAdminDashboard:
    def test_dashboard_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/platform-admin")
        result = record_evaluation(authenticated_page, "/platform-admin")
        _assert_evaluation(result)

    def test_dashboard_has_navigation_links(self, authenticated_page: Page):
        _goto_platform(authenticated_page, "/platform-admin")
        nav_links = authenticated_page.locator(
            "a[href*='tenants'], a[href*='impersonation'], a[href*='settings'], a[href*='license']"
        )
        assert nav_links.count() >= 2, "Expected platform admin nav links"


# ---------------------------------------------------------------------------
# Tenant Management
# ---------------------------------------------------------------------------


class TestPlatformAdminTenants:
    def test_tenant_list_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/platform-admin/tenants")
        result = record_evaluation(authenticated_page, "/platform-admin/tenants")
        _assert_evaluation(result)

    def test_tenant_list_has_default_tenant(self, authenticated_page: Page):
        _goto_platform(authenticated_page, "/platform-admin/tenants")
        body = authenticated_page.inner_text("body")
        assert "default" in body.lower(), "'default' tenant not found in list"

    def test_create_tenant_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/platform-admin/tenants/create")
        if "/create" not in authenticated_page.url:
            pytest.skip("Create tenant page redirected away (capacity limit reached)")
        # Use specific form id to avoid matching SwitchTenant mini-forms on the page
        expect(authenticated_page.locator("#createTenantForm, form:has(input[id*='Slug'])").first).to_be_visible()
        result = record_evaluation(authenticated_page, "/platform-admin/tenants/create")
        _assert_evaluation(result)

    def test_create_tenant_form_has_slug_field(self, authenticated_page: Page):
        _goto_platform(authenticated_page, "/platform-admin/tenants/create")
        if "/create" not in authenticated_page.url:
            pytest.skip("Create tenant page redirected away (capacity limit reached)")
        slug_input = authenticated_page.locator(
            "input[id*='Slug'], input[name*='Slug'], input[id*='slug'], input[name*='slug']"
        )
        expect(slug_input).to_be_visible()

    def test_tenant_edit_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/platform-admin/tenants")
        edit_link = authenticated_page.locator("a:has-text('Edit')").first
        if edit_link.count() == 0 or not edit_link.is_visible():
            pytest.skip("No tenants available to edit")
        edit_link.click()
        authenticated_page.wait_for_load_state("domcontentloaded")
        if "/login" in authenticated_page.url:
            pytest.skip("Redirected to login")
        result = record_evaluation(authenticated_page, "/platform-admin/tenants/edit")
        _assert_evaluation(result)

    def test_tenant_import_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/PlatformAdmin/Tenants/Import")
        result = record_evaluation(authenticated_page, "/platform-admin/tenants/import")
        _assert_evaluation(result, min_score=3)


# ---------------------------------------------------------------------------
# Impersonation
# ---------------------------------------------------------------------------


class TestPlatformAdminImpersonation:
    def test_impersonation_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/platform-admin/impersonation")
        result = record_evaluation(authenticated_page, "/platform-admin/impersonation")
        _assert_evaluation(result)

    def test_impersonation_history_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/platform-admin/impersonation-history")
        result = record_evaluation(authenticated_page, "/platform-admin/impersonation-history")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Platform Settings
# ---------------------------------------------------------------------------


class TestPlatformAdminSettings:
    def test_platform_settings_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/platform-admin/settings")
        result = record_evaluation(authenticated_page, "/platform-admin/settings")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Platform License
# ---------------------------------------------------------------------------


class TestPlatformLicense:
    def test_platform_license_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/admin/license/platform")
        result = record_evaluation(authenticated_page, "/admin/license/platform")
        _assert_evaluation(result)

    def test_license_install_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/admin/license/platform/install")
        result = record_evaluation(authenticated_page, "/admin/license/platform/install")
        _assert_evaluation(result)

    def test_license_history_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/admin/license/platform/history")
        result = record_evaluation(authenticated_page, "/admin/license/platform/history")
        _assert_evaluation(result)
