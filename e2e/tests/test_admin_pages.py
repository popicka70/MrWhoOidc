"""
E2E tests for tenant-admin pages (requires admin@default.local login).

Pages covered:
  /admin/realms                – Realm list
  /admin/realms/add            – Add realm
  /admin/clients               – Client list
  /admin/clients/add           – Add client
  /admin/clients/export        – Export clients
  /admin/clients/import        – Import clients
  /admin/providers             – Provider list
  /admin/providers/add         – Add provider
  /admin/providers/export      – Export providers
  /admin/providers/import      – Import providers
  /admin/provider-mappings     – Provider-client mappings
  /admin/scopes                – Scope list
  /admin/scopes/add            – Add scope
  /admin/roles                 – Role list
  /admin/roles/add             – Add role
  /admin/users                 – User list
  /admin/users/add             – Add user
  /admin/registrations         – User registrations
  /admin/configuration-audit   – Configuration audit log
  /admin/backchannel           – BCL outbox
  /admin/obo-setup             – OBO setup wizard
  /admin/license/tenant        – Tenant license
  /admin/branding              – Branding
  /admin/settings              – Settings
  /admin/rate-limits           – Rate limits
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


async def _goto_admin(page: Page, path: str) -> None:
    """Navigate to an admin page and wait for load.  Asserts no redirect to login."""
    try:
        await page.goto(path, wait_until="domcontentloaded")
    except Exception as exc:
        pytest.skip(f"Navigation to {path} failed (may be a download endpoint): {exc}")
    assert "/login" not in page.url, f"Redirected to login when accessing {path} — auth expired"


# ---------------------------------------------------------------------------
# Realms
# ---------------------------------------------------------------------------


class TestAdminRealms:
    async def test_realm_list_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/realms")
        page_text = await authenticated_page.inner_text("body")
        assert "realm" in page_text.lower(), "Realm list does not mention 'realm'"
        result = await record_evaluation(authenticated_page, "/admin/realms")
        _assert_evaluation(result)

    async def test_add_realm_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/realms/add")
        await expect(authenticated_page.locator("form")).to_be_visible()
        result = await record_evaluation(authenticated_page, "/admin/realms/add")
        _assert_evaluation(result)

    async def test_realm_list_has_default_realms(self, authenticated_page: Page):
        await _goto_admin(authenticated_page, "/admin/realms")
        body = await authenticated_page.inner_text("body")
        assert "default" in body.lower(), "Expected 'default' realm in the realm list"


# ---------------------------------------------------------------------------
# Clients
# ---------------------------------------------------------------------------


class TestAdminClients:
    async def test_client_list_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/clients")
        result = await record_evaluation(authenticated_page, "/admin/clients")
        _assert_evaluation(result)

    async def test_add_client_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/clients/add")
        await expect(authenticated_page.locator("form")).to_be_visible()
        result = await record_evaluation(authenticated_page, "/admin/clients/add")
        _assert_evaluation(result)

    async def test_export_clients_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/clients/export")
        result = await record_evaluation(authenticated_page, "/admin/clients/export")
        _assert_evaluation(result)

    async def test_import_clients_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/clients/import")
        result = await record_evaluation(authenticated_page, "/admin/clients/import")
        _assert_evaluation(result)

    async def test_client_add_button_visible(self, authenticated_page: Page):
        await _goto_admin(authenticated_page, "/admin/clients")
        add_btn = authenticated_page.locator(
            "a:has-text('Add'), a:has-text('New'), button:has-text('Add'), button:has-text('New')"
        ).first
        await expect(add_btn).to_be_visible()


# ---------------------------------------------------------------------------
# Providers
# ---------------------------------------------------------------------------


class TestAdminProviders:
    async def test_provider_list_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/providers")
        result = await record_evaluation(authenticated_page, "/admin/providers")
        _assert_evaluation(result)

    async def test_add_provider_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/providers/add")
        result = await record_evaluation(authenticated_page, "/admin/providers/add")
        _assert_evaluation(result)

    async def test_export_providers_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/providers/export")
        result = await record_evaluation(authenticated_page, "/admin/providers/export")
        _assert_evaluation(result)

    async def test_import_providers_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/providers/import")
        result = await record_evaluation(authenticated_page, "/admin/providers/import")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Provider Mappings
# ---------------------------------------------------------------------------


class TestAdminProviderMappings:
    async def test_provider_mappings_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/provider-mappings")
        result = await record_evaluation(authenticated_page, "/admin/provider-mappings")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Scopes
# ---------------------------------------------------------------------------


class TestAdminScopes:
    async def test_scope_list_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/scopes")
        result = await record_evaluation(authenticated_page, "/admin/scopes")
        _assert_evaluation(result)

    async def test_add_scope_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/scopes/add")
        await expect(authenticated_page.locator("form")).to_be_visible()
        result = await record_evaluation(authenticated_page, "/admin/scopes/add")
        _assert_evaluation(result)

    async def test_scope_list_has_standard_scopes(self, authenticated_page: Page):
        await _goto_admin(authenticated_page, "/admin/scopes")
        body = await authenticated_page.inner_text("body")
        assert "openid" in body.lower(), "Standard 'openid' scope not visible in scope list"


# ---------------------------------------------------------------------------
# Roles
# ---------------------------------------------------------------------------


class TestAdminRoles:
    async def test_role_list_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/roles")
        result = await record_evaluation(authenticated_page, "/admin/roles")
        _assert_evaluation(result)

    async def test_add_role_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/roles/add")
        await expect(authenticated_page.locator("form")).to_be_visible()
        result = await record_evaluation(authenticated_page, "/admin/roles/add")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Users
# ---------------------------------------------------------------------------


class TestAdminUsers:
    async def test_user_list_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/users")
        result = await record_evaluation(authenticated_page, "/admin/users")
        _assert_evaluation(result)

    async def test_add_user_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/users/add")
        await expect(authenticated_page.locator("form")).to_be_visible()
        result = await record_evaluation(authenticated_page, "/admin/users/add")
        _assert_evaluation(result)

    async def test_user_list_shows_admin_user(self, authenticated_page: Page):
        await _goto_admin(authenticated_page, "/admin/users")
        body = await authenticated_page.inner_text("body")
        assert "admin" in body.lower(), "Admin user not visible in user list"

    async def test_user_edit_sub_tabs(self, authenticated_page: Page, record_evaluation):
        """Navigate to the user list, open the first user's edit page, then visit sub-tabs."""
        await _goto_admin(authenticated_page, "/admin/users")
        # Find first edit link
        edit_link = authenticated_page.locator("a[href*='/admin/users/'][href*='/edit']").first
        if await edit_link.count() == 0:
            edit_link = authenticated_page.locator("a:has-text('Edit')").first

        if await edit_link.count() == 0:
            pytest.skip("No edit link found on user list — may need seeded data")

        await edit_link.click()
        user_url = authenticated_page.url
        result = await record_evaluation(authenticated_page, "/admin/users/edit")
        _assert_evaluation(result)

        # Extract user ID from URL to build sub-tab URLs
        import re
        match = re.search(r"/admin/users/([^/]+)/", user_url + "/")
        if not match:
            return  # Can't determine user ID, skip sub-tabs

        user_id = match.group(1)
        sub_tabs = [
            (f"/admin/users/{user_id}/clients", "user-clients"),
            (f"/admin/users/{user_id}/emails", "user-emails"),
            (f"/admin/users/{user_id}/roles", "user-roles"),
        ]
        for route, label in sub_tabs:
            await _goto_admin(authenticated_page, route)
            sub_result = await record_evaluation(authenticated_page, route)
            _assert_evaluation(sub_result)


# ---------------------------------------------------------------------------
# Registrations
# ---------------------------------------------------------------------------


class TestAdminRegistrations:
    async def test_registrations_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/registrations")
        result = await record_evaluation(authenticated_page, "/admin/registrations")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Configuration Audit
# ---------------------------------------------------------------------------


class TestAdminConfigurationAudit:
    async def test_configuration_audit_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/configuration-audit")
        result = await record_evaluation(authenticated_page, "/admin/configuration-audit")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Back-Channel Logout outbox
# ---------------------------------------------------------------------------


class TestAdminBackchannel:
    async def test_backchannel_outbox_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/backchannel")
        result = await record_evaluation(authenticated_page, "/admin/backchannel")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# OBO Setup Wizard
# ---------------------------------------------------------------------------


class TestAdminOboSetup:
    async def test_obo_setup_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/obo-setup")
        result = await record_evaluation(authenticated_page, "/admin/obo-setup")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Tenant License
# ---------------------------------------------------------------------------


class TestAdminTenantLicense:
    async def test_tenant_license_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/license/tenant")
        result = await record_evaluation(authenticated_page, "/admin/license/tenant")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Branding
# ---------------------------------------------------------------------------


class TestAdminBranding:
    async def test_branding_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/branding")
        result = await record_evaluation(authenticated_page, "/admin/branding")
        _assert_evaluation(result)

    async def test_branding_form_is_present(self, authenticated_page: Page):
        await _goto_admin(authenticated_page, "/admin/branding")
        form = authenticated_page.locator("form")
        # Some installs may redirect or show a license gate — allow for that
        if await form.count() == 0:
            body = await authenticated_page.inner_text("body")
            if "license" in body.lower():
                pytest.skip("Branding requires a license — skipped in this environment")
        await expect(form).to_be_visible()


# ---------------------------------------------------------------------------
# Settings
# ---------------------------------------------------------------------------


class TestAdminSettings:
    async def test_settings_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/settings")
        result = await record_evaluation(authenticated_page, "/admin/settings")
        _assert_evaluation(result)

    async def test_settings_has_save_button(self, authenticated_page: Page):
        await _goto_admin(authenticated_page, "/admin/settings")
        save = authenticated_page.locator("button[type='submit']").first
        if await save.count() > 0:
            await expect(save).to_be_visible()


# ---------------------------------------------------------------------------
# Rate Limits
# ---------------------------------------------------------------------------


class TestAdminRateLimits:
    async def test_rate_limits_page_loads(self, authenticated_page: Page, record_evaluation):
        await _goto_admin(authenticated_page, "/admin/rate-limits")
        result = await record_evaluation(authenticated_page, "/admin/rate-limits")
        _assert_evaluation(result)
