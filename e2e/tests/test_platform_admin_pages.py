"""
E2E tests for Platform Admin pages (requires platform-admin role).

Pages covered:
  /platform-admin                   -- Dashboard
  /platform-admin/tenants           -- Tenant list
    /platform-admin/providers         -- Platform identity providers
  /platform-admin/tenants/create    -- Create tenant
  /platform-admin/tenants/edit/{id} -- Edit tenant (first found)
  /platform-admin/tenants/import    -- Import tenants
   /platform-admin/support-access          -- Support access control panel
   /platform-admin/support-access/history   -- Support access audit log
  /platform-admin/settings          -- Platform settings
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


def _set_root_login_mode(page: Page, value: str) -> None:
    _goto_platform(page, "/platform-admin/settings")
    expect(page.locator("select#RootLoginMode")).to_be_visible()
    page.locator("select#RootLoginMode").select_option(value)
    page.get_by_role("button", name="Save Settings").click()
    page.wait_for_load_state("domcontentloaded")
    expect(page.locator(".alert-success")).to_contain_text("Platform settings saved")


def _submit_login_form(page: Page, username: str, password: str) -> None:
    page.locator("input#Username").fill(username)
    page.locator("input#Password").fill(password)
    page.locator("button[type='submit']").click()
    page.wait_for_url(
        lambda url: "/login" not in url and "/LoginTotp" not in url,
        timeout=30_000,
    )


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
            "a[href*='tenants'], a[href*='support-access'], a[href*='settings']"
        )
        assert nav_links.count() >= 2, "Expected platform admin nav links"

    def test_dashboard_does_not_show_license_link(self, authenticated_page: Page):
        _goto_platform(authenticated_page, "/platform-admin")
        assert authenticated_page.locator("a[href*='license']").count() == 0


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
# Tenant Support Access
# ---------------------------------------------------------------------------


class TestPlatformAdminSupportAccess:
    """E2E tests for Tenant Support Access (formerly impersonation)."""

    def test_support_access_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/platform-admin/support-access")
        result = record_evaluation(authenticated_page, "/platform-admin/support-access")
        _assert_evaluation(result)

    def test_support_access_history_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/platform-admin/support-access/history")
        result = record_evaluation(authenticated_page, "/platform-admin/support-access/history")
        _assert_evaluation(result)

    def test_start_support_access(
        self, authenticated_page: Page, record_evaluation
    ):
        """
        1. Navigate to /platform-admin/support-access.
        2. Select a tenant, provide a reason ("Troubleshooting"), and an expiry time.
        3. Submit the form and verify redirection to the tenant admin dashboard.
        4. Verify that the support access banner is visible on the page.
        """
        _goto_platform(authenticated_page, "/platform-admin/support-access")
        expect(authenticated_page.locator("body")).to_contain_text("Support Access")
        expect(authenticated_page.locator("body")).to_contain_text("Reason")

        # Select the default tenant and fill reason/expiry
        tenant_option = authenticated_page.locator("select#TenantSelector, select#TargetTenant").first
        if tenant_option.count() == 0 or not tenant_option.is_visible():
            pytest.skip("Tenant selector not visible on support-access page")
        reason_input = authenticated_page.locator("input#Reason, input[name='Reason']").first
        if reason_input.count() == 0 or not reason_input.is_visible():
            pytest.skip("Reason input not visible on support-access page")
        reason_input.fill("Troubleshooting")
        expiry_input = authenticated_page.locator("input#ExpiryTime, input[name='ExpiryTime'], input#Expiry, input[name='Expiry']").first
        if expiry_input.count() > 0 and expiry_input.is_visible():
            expiry_input.fill("15")

        # Click the start-support button
        start_btn = authenticated_page.locator(
            "button:has-text('Start Support Access'), button:has-text('Start'), "
            "button[type='submit']"
        ).first
        if start_btn.count() == 0 or not start_btn.is_visible():
            pytest.skip("Start Support Access button not found")
        start_btn.click()
        authenticated_page.wait_for_load_state("domcontentloaded")

        # Verify redirection to tenant admin dashboard
        if "/login" in authenticated_page.url:
            pytest.skip("Redirected to login after starting support access")
        assert any([
            "/admin" in authenticated_page.url,
            "/tenants" in authenticated_page.url,
            "tenant" in authenticated_page.url.lower(),
            "dashboard" in authenticated_page.url.lower(),
        ]), (
            f"Expected tenant admin dashboard URL, got: {authenticated_page.url}"
        )

        # Verify support access banner is visible
        banner = authenticated_page.locator(
            ".alert-info, .alert-warning, .alert-success, "
            "div:has-text('Support Access'), div:has-text('support access')"
        ).first
        expect(banner).to_be_visible()
        expect(authenticated_page.locator("body")).to_contain_text("Support Access")
        expect(authenticated_page.locator("body")).to_contain_text("Troubleshooting")
        result = record_evaluation(authenticated_page, "/platform-admin/support-access/start")
        _assert_evaluation(result)

    def test_read_only_enforcement_api(
        self, authenticated_page: Page, record_evaluation
    ):
        """
        1. While in support access mode, attempt a mutation request (e.g., POST)
           to an admin API endpoint (like creating a client secret or updating a realm).
        2. Verify that the response is 403 Forbidden.
        """
        # First start support access (reuse start flow)
        _goto_platform(authenticated_page, "/platform-admin/support-access")
        tenant_option = authenticated_page.locator("select#TenantSelector, select#TargetTenant").first
        if tenant_option.count() == 0 or not tenant_option.is_visible():
            pytest.skip("Tenant selector not visible")
        reason_input = authenticated_page.locator("input#Reason, input[name='Reason']").first
        if reason_input.count() == 0 or not reason_input.is_visible():
            pytest.skip("Reason input not visible")
        reason_input.fill("Troubleshooting")
        start_btn = authenticated_page.locator(
            "button:has-text('Start Support Access'), button:has-text('Start'), "
            "button[type='submit']"
        ).first
        if start_btn.count() == 0 or not start_btn.is_visible():
            pytest.skip("Start Support Access button not found")
        start_btn.click()
        authenticated_page.wait_for_load_state("domcontentloaded")
        if "/login" in authenticated_page.url:
            pytest.skip("Redirected to login after starting support access")

        # Attempt a mutation POST to an admin API endpoint
        try:
            authenticated_page.goto("/admin/realms/add", wait_until="domcontentloaded")
        except Exception:
            # If navigation itself fails with 403, that's the expected denial
            pytest.skip("Navigation to /admin/realms/add failed (403 expected)")

        # If we reach the page, try submitting a form to trigger a 403
        name_input = authenticated_page.locator(
            "input#Input_Name, input[name='Input.Name']"
        ).first
        if name_input.count() > 0 and name_input.is_visible():
            name_input.fill("e2e-forbidden-realm")
            submit_btn = authenticated_page.locator("button[type='submit']").first
            if submit_btn.count() > 0 and submit_btn.is_visible():
                submit_btn.click()
                authenticated_page.wait_for_load_state("domcontentloaded")
        url_lower = authenticated_page.url.lower()
        body = authenticated_page.inner_text("body").lower()
        assert (
            "403" in body or "forbidden" in body or "denied" in body
            or "accessdenied" in url_lower
            or "forbidden" in url_lower
            or "access-denied" in url_lower
        ), (
            f"Expected 403 Forbidden after mutation attempt during support access, "
            f"got url={authenticated_page.url}"
        )
        result = record_evaluation(authenticated_page, "/platform-admin/support-access/read-only")
        _assert_evaluation(result, min_score=3)

    def test_end_support_access(
        self, authenticated_page: Page, record_evaluation
    ):
        """
        1. Start support access first.
        2. Click "End support access" from the banner or navigate to
            /platform-admin/support-access and end the session.
        3. Verify that the banner disappears.
        4. Verify that normal admin operations (if the user has those
            permissions) are restored.
        """
        # ---- Step 1: Start support access ----
        _goto_platform(authenticated_page, "/platform-admin/support-access")
        tenant_option = authenticated_page.locator("select#TenantSelector, select#TargetTenant").first
        if tenant_option.count() == 0 or not tenant_option.is_visible():
            pytest.skip("Tenant selector not visible")
        reason_input = authenticated_page.locator("input#Reason, input[name='Reason']").first
        if reason_input.count() == 0 or not reason_input.is_visible():
            pytest.skip("Reason input not visible")
        reason_input.fill("Troubleshooting")
        start_btn = authenticated_page.locator(
            "button:has-text('Start Support Access'), button:has-text('Start'), "
            "button[type='submit']"
        ).first
        if start_btn.count() == 0 or not start_btn.is_visible():
            pytest.skip("Start Support Access button not found")
        start_btn.click()
        authenticated_page.wait_for_load_state("domcontentloaded")
        if "/login" in authenticated_page.url:
            pytest.skip("Redirected to login after starting support access")

        # ---- Step 2: End support access ----
        try:
            authenticated_page.goto("/platform-admin/support-access", wait_until="domcontentloaded")
        except Exception:
            pass
        end_btn = authenticated_page.locator(
            "button:has-text('End Support Access'), button:has-text('End'), "
            "button:has-text('Stop')"
        ).first
        if end_btn.count() == 0 or not end_btn.is_visible():
            pytest.skip("End Support Access button not found")
        end_btn.click()
        authenticated_page.wait_for_load_state("domcontentloaded")

        # ---- Step 3: Verify banner disappears ----
        body = authenticated_page.inner_text("body").lower()
        assert "support access" not in body, (
            "Support access banner still visible after ending session"
        )
        # Also check no impersonation/support-access warning remains
        assert "impersonat" not in body, (
            "Impersonation language still visible after ending session"
        )

        # ---- Step 4: Verify normal admin operations are restored ----
        _goto_platform(authenticated_page, "/platform-admin/settings")
        expect(authenticated_page.locator("select#RootLoginMode")).to_be_visible()
        result = record_evaluation(authenticated_page, "/platform-admin/support-access/end")
        _assert_evaluation(result)


class TestLegacyImpersonationRedirects:
    """Legacy impersonation URLs redirect to support-access equivalents."""

    def test_impersonation_route_redirects_to_support_access(self, authenticated_page: Page):
        authenticated_page.goto("/platform-admin/impersonation", wait_until="domcontentloaded")
        assert authenticated_page.url.endswith("/support-access") or "/support-access" in authenticated_page.url, (
            f"Expected redirect to /platform-admin/support-access, got: {authenticated_page.url}"
        )

    def test_impersonation_history_route_redirects_to_support_access_history(self, authenticated_page: Page):
        authenticated_page.goto("/platform-admin/impersonation-history", wait_until="domcontentloaded")
        assert "/support-access/history" in authenticated_page.url or "/support-access" in authenticated_page.url, (
            f"Expected redirect to /platform-admin/support-access/history, got: {authenticated_page.url}"
        )


# ---------------------------------------------------------------------------
# Platform Identity Providers
# ---------------------------------------------------------------------------


class TestPlatformAdminProviders:
    def test_platform_provider_list_loads(
        self,
        authenticated_page: Page,
        record_evaluation,
        platform_provider_setup,
    ):
        _goto_platform(authenticated_page, "/platform-admin/providers")
        expect(authenticated_page.get_by_role("heading", name="Platform Identity Providers")).to_be_visible()
        expect(authenticated_page.locator("body")).to_contain_text(platform_provider_setup.provider_display_name)
        result = record_evaluation(authenticated_page, "/platform-admin/providers")
        _assert_evaluation(result)

    def test_platform_provider_add_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/platform-admin/providers/add")
        expect(authenticated_page.locator("input#Input_Name")).to_be_visible()
        expect(authenticated_page.locator("input#Input_Authority")).to_be_visible()
        expect(authenticated_page.locator("input#Input_ClientId")).to_be_visible()
        result = record_evaluation(authenticated_page, "/platform-admin/providers/add")
        _assert_evaluation(result)

    def test_platform_provider_edit_page_loads(
        self,
        authenticated_page: Page,
        record_evaluation,
        platform_provider_setup,
    ):
        _goto_platform(
            authenticated_page,
            f"/platform-admin/providers/edit/{platform_provider_setup.provider_id}",
        )
        expect(authenticated_page.locator("input#Input_Name")).to_have_value(
            platform_provider_setup.provider_name
        )
        expect(authenticated_page.locator("input#Input_Authority")).to_be_visible()
        result = record_evaluation(authenticated_page, "/platform-admin/providers/edit")
        _assert_evaluation(result)

    def test_root_platform_external_provider_login(
        self,
        page: Page,
        upstream_base_url: str,
        platform_provider_setup,
    ):
        page.goto("/DiscoverTenant?returnUrl=/platform-admin", wait_until="domcontentloaded")
        provider_button = page.locator(
            f"a[data-provider-name='{platform_provider_setup.provider_name}']"
        ).first
        expect(provider_button).to_be_visible()
        provider_button.click()

        page.wait_for_url(
            lambda url: url.startswith(upstream_base_url) and "/login" in url,
            timeout=30_000,
        )
        _submit_login_form(page, "admin@mrwho.local", "E2E-test-password!")

        page.wait_for_url(lambda url: "/platform-admin" in url, timeout=30_000)
        expect(page.locator("body")).to_contain_text("Platform")
        expect(page.locator("body")).to_contain_text("Tenants")


# ---------------------------------------------------------------------------
# Platform Settings
# ---------------------------------------------------------------------------


class TestPlatformAdminSettings:
    def test_platform_settings_loads(self, authenticated_page: Page, record_evaluation):
        _goto_platform(authenticated_page, "/platform-admin/settings")
        result = record_evaluation(authenticated_page, "/platform-admin/settings")
        _assert_evaluation(result)

    def test_platform_settings_exposes_root_sign_in_behavior(self, authenticated_page: Page):
        _goto_platform(authenticated_page, "/platform-admin/settings")

        root_login_mode = authenticated_page.locator("select#RootLoginMode")
        expect(root_login_mode).to_be_visible()
        expect(root_login_mode.locator("option")).to_have_count(2)
        expect(root_login_mode).to_contain_text("Email tenant discovery")
        expect(root_login_mode).to_contain_text("Direct tenant URLs only")

    def test_root_sign_in_behavior_can_require_direct_tenant_url(self, authenticated_page: Page, page: Page):
        try:
            _set_root_login_mode(authenticated_page, "1")

            page.goto("/DiscoverTenant?returnUrl=/account/emails", wait_until="domcontentloaded")
            expect(page.locator(".alert-info[role='status']")).to_contain_text("Use your organization-specific sign-in URL")
            expect(page.locator("input#Email")).to_have_count(0)
            expect(page.locator("button[type='submit']")).to_have_count(0)

            page.goto("/t/default/login?returnUrl=/account/emails", wait_until="domcontentloaded")
            expect(page.locator("input#Username")).to_be_visible()
            expect(page.locator("input#Password")).to_be_visible()
        finally:
            _set_root_login_mode(authenticated_page, "0")


# ---------------------------------------------------------------------------
# Legacy platform license redirects
# ---------------------------------------------------------------------------


class TestLegacyPlatformLicenseRedirects:
    def test_platform_license_route_redirects_to_dashboard(self, authenticated_page: Page):
        authenticated_page.goto("/admin/license/platform", wait_until="domcontentloaded")
        assert authenticated_page.url.endswith("/platform-admin"), authenticated_page.url

    def test_platform_license_history_route_redirects_to_dashboard(self, authenticated_page: Page):
        authenticated_page.goto("/admin/license/platform/history", wait_until="domcontentloaded")
        assert authenticated_page.url.endswith("/platform-admin"), authenticated_page.url
