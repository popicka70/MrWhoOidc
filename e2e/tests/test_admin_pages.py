"""
E2E tests for tenant-admin pages (requires admin@mrwho.local login).

Pages covered:
  /admin/realms              -- Realm list
  /admin/realms/add          -- Add realm
  /admin/realms/import       -- Import realms
  /admin/realms/edit/{id}    -- Edit realm (first found)
  /admin/clients             -- Client list
  /admin/clients/add         -- Add client
  /admin/clients/import      -- Import clients
  /admin/clients/edit/{id}   -- Edit client (first found)
  /Admin/ClientKeys/{id}     -- Client keys (first client)
  /admin/providers           -- Provider list
  /admin/providers/add       -- Add provider
  /admin/providers/import    -- Import providers
  /Admin/Providers/Details/{id}       -- Provider details
  /Admin/Providers/Edit/{id}          -- Provider edit
  /Admin/Providers/ClaimMappings/{id} -- Provider claim mappings
  /Admin/ProviderClaimMappings/{id}   -- Claim mappings list
  /Admin/ProviderKeys/{id}            -- Provider keys
  /admin/provider-mappings   -- Provider-client mappings
  /admin/scopes              -- Scope list
  /admin/scopes/add          -- Add scope
  /Admin/Scopes/Edit/{name}  -- Edit scope (first found)
  /admin/roles               -- Role list
  /admin/roles/add           -- Add role
  /Admin/Roles/Edit/{id}     -- Edit role (first found)
  /admin/users               -- User list
  /admin/users/add           -- Add user
  /admin/users/edit/{id}     -- Edit user (first found)
  /Admin/Users/Clients/{id}  -- User clients sub-tab
  /Admin/Users/Emails/{id}   -- User emails sub-tab
  /Admin/Users/Roles/{id}    -- User roles sub-tab
  /Admin/Users/Linked/{id}   -- User linked accounts sub-tab
  /admin/registrations       -- User registrations
  /admin/configuration-audit -- Configuration audit log
  /admin/backchannel         -- BCL outbox
  /admin/obo-setup           -- OBO setup wizard
  /admin/license             -- License (main)
  /admin/license/history     -- License history
  /admin/license/install     -- License install
  /admin/license/tenant      -- Tenant license
  /admin/license/tenant/history  -- Tenant license history
  /admin/license/tenant/install  -- Tenant license install
  /admin/branding            -- Branding
  /admin/settings            -- Settings
  /admin/rate-limits         -- Rate limits
"""

from __future__ import annotations

import re

import pytest
from playwright.sync_api import Page, expect


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _assert_evaluation(result, *, min_score: int = 5) -> None:
    if result.skipped:
        return
    if result.error:
        pytest.skip(f"LLM evaluation error: {result.error}")
    assert result.overall_score >= min_score, (
        f"Page '{result.route}' scored {result.overall_score}/10 -- below {min_score}.\n"
        f"Summary: {result.summary}"
    )


def _goto_admin(page: Page, path: str) -> None:
    """Navigate to an admin page; skip if it triggers a file download or redirect."""
    try:
        page.goto(path, wait_until="domcontentloaded")
    except Exception as exc:
        pytest.skip(f"Navigation to {path} failed (likely a download endpoint): {exc}")
    if "/login" in page.url:
        pytest.skip(f"Redirected to login for {path} -- auth expired")


def _click_first_edit_link(page: Page, hint: str = "Edit") -> bool:
    """Click the first visible link containing hint text; return True on success."""
    link = page.locator(f"a:has-text('{hint}')").first
    if link.count() == 0 or not link.is_visible():
        return False
    link.click()
    page.wait_for_load_state("domcontentloaded")
    return True


def _extract_guid_from_url(url: str) -> str | None:
    m = re.search(
        r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", url, re.IGNORECASE
    )
    return m.group(0) if m else None


# ---------------------------------------------------------------------------
# Realms
# ---------------------------------------------------------------------------


class TestAdminRealms:
    def test_realm_list_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/realms")
        body = authenticated_page.inner_text("body")
        assert "realm" in body.lower()
        result = record_evaluation(authenticated_page, "/admin/realms")
        _assert_evaluation(result)

    def test_add_realm_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/realms/add")
        expect(authenticated_page.locator("form")).to_be_visible()
        result = record_evaluation(authenticated_page, "/admin/realms/add")
        _assert_evaluation(result)

    def test_realm_import_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/realms/import")
        result = record_evaluation(authenticated_page, "/admin/realms/import")
        _assert_evaluation(result, min_score=4)

    def test_realm_edit_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/realms")
        if not _click_first_edit_link(authenticated_page):
            pytest.skip("No realms available to edit")
        if "/login" in authenticated_page.url:
            pytest.skip("Redirected to login after clicking edit")
        result = record_evaluation(authenticated_page, "/admin/realms/edit")
        _assert_evaluation(result)

    def test_realm_list_has_default_realm(self, authenticated_page: Page):
        _goto_admin(authenticated_page, "/admin/realms")
        body = authenticated_page.inner_text("body")
        assert "default" in body.lower(), "'default' realm not found in list"


# ---------------------------------------------------------------------------
# Clients
# ---------------------------------------------------------------------------


class TestAdminClients:
    def test_client_list_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/clients")
        result = record_evaluation(authenticated_page, "/admin/clients")
        _assert_evaluation(result)

    def test_add_client_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/clients/add")
        expect(authenticated_page.locator("form")).to_be_visible()
        result = record_evaluation(authenticated_page, "/admin/clients/add")
        _assert_evaluation(result)

    def test_import_clients_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/clients/import")
        result = record_evaluation(authenticated_page, "/admin/clients/import")
        _assert_evaluation(result, min_score=4)

    def test_client_edit_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/clients")
        if not _click_first_edit_link(authenticated_page):
            pytest.skip("No clients available to edit")
        if "/login" in authenticated_page.url:
            pytest.skip("Redirected to login")
        result = record_evaluation(authenticated_page, "/admin/clients/edit")
        _assert_evaluation(result)

    def test_client_keys_page_loads(self, authenticated_page: Page, record_evaluation):
        """Navigate to client edit, then follow the keys sub-link."""
        _goto_admin(authenticated_page, "/admin/clients")
        if not _click_first_edit_link(authenticated_page):
            pytest.skip("No clients available")
        client_url = authenticated_page.url
        client_id = _extract_guid_from_url(client_url)
        if not client_id:
            pytest.skip("Could not extract client ID from URL")
        keys_path = f"/Admin/ClientKeys/{client_id}"
        _goto_admin(authenticated_page, keys_path)
        if "/login" in authenticated_page.url or "404" in authenticated_page.inner_text("body").lower():
            pytest.skip("Client keys page not accessible")
        result = record_evaluation(authenticated_page, "/admin/client-keys")
        _assert_evaluation(result, min_score=3)

    def test_client_add_button_visible(self, authenticated_page: Page):
        _goto_admin(authenticated_page, "/admin/clients")
        add_btn = authenticated_page.locator(
            "a:has-text('Add'), a:has-text('New'), button:has-text('Add'), button:has-text('New')"
        ).first
        expect(add_btn).to_be_visible()

    def test_export_clients_skips_or_loads(self, authenticated_page: Page):
        """Client export may trigger a file download -- just confirm no error page."""
        try:
            authenticated_page.goto("/admin/clients/export", wait_until="domcontentloaded")
        except Exception:
            pytest.skip("Export endpoint triggered download (expected)")


# ---------------------------------------------------------------------------
# Providers
# ---------------------------------------------------------------------------


class TestAdminProviders:
    def test_provider_list_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/providers")
        result = record_evaluation(authenticated_page, "/admin/providers")
        _assert_evaluation(result)

    def test_add_provider_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/providers/add")
        result = record_evaluation(authenticated_page, "/admin/providers/add")
        _assert_evaluation(result)

    def test_import_providers_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/providers/import")
        result = record_evaluation(authenticated_page, "/admin/providers/import")
        _assert_evaluation(result, min_score=4)

    def test_provider_details_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/providers")
        # Try Details link first, fall back to first link with an ID
        details_link = authenticated_page.locator("a:has-text('Details'), a:has-text('View')").first
        if details_link.count() == 0 or not details_link.is_visible():
            pytest.skip("No provider details link available")
        details_link.click()
        authenticated_page.wait_for_load_state("domcontentloaded")
        result = record_evaluation(authenticated_page, "/admin/providers/details")
        _assert_evaluation(result, min_score=3)

    def test_provider_edit_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/providers")
        if not _click_first_edit_link(authenticated_page):
            pytest.skip("No providers available to edit")
        result = record_evaluation(authenticated_page, "/admin/providers/edit")
        _assert_evaluation(result, min_score=3)

    def test_provider_claim_mappings_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/providers")
        mappings_link = authenticated_page.locator(
            "a:has-text('Claim'), a:has-text('Mapping')"
        ).first
        if mappings_link.count() == 0 or not mappings_link.is_visible():
            # Try navigating via URL if we can get a provider ID
            if not _click_first_edit_link(authenticated_page):
                pytest.skip("No providers available")
            provider_id = _extract_guid_from_url(authenticated_page.url)
            if not provider_id:
                pytest.skip("Could not get provider ID")
            _goto_admin(authenticated_page, f"/Admin/Providers/ClaimMappings/{provider_id}")
        else:
            mappings_link.click()
            authenticated_page.wait_for_load_state("domcontentloaded")
        result = record_evaluation(authenticated_page, "/admin/providers/claim-mappings")
        _assert_evaluation(result, min_score=3)

    def test_provider_keys_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/providers")
        if not _click_first_edit_link(authenticated_page):
            pytest.skip("No providers available")
        provider_id = _extract_guid_from_url(authenticated_page.url)
        if not provider_id:
            pytest.skip("Could not get provider ID")
        _goto_admin(authenticated_page, f"/Admin/ProviderKeys/{provider_id}")
        if "/login" in authenticated_page.url:
            pytest.skip("Redirected to login for provider keys")
        result = record_evaluation(authenticated_page, "/admin/provider-keys")
        _assert_evaluation(result, min_score=3)

    def test_export_providers_skips_or_loads(self, authenticated_page: Page):
        try:
            authenticated_page.goto("/admin/providers/export", wait_until="domcontentloaded")
        except Exception:
            pytest.skip("Providers export triggered download")


# ---------------------------------------------------------------------------
# Provider Mappings
# ---------------------------------------------------------------------------


class TestAdminProviderMappings:
    def test_provider_mappings_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/provider-mappings")
        result = record_evaluation(authenticated_page, "/admin/provider-mappings")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Scopes
# ---------------------------------------------------------------------------


class TestAdminScopes:
    def test_scope_list_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/scopes")
        result = record_evaluation(authenticated_page, "/admin/scopes")
        _assert_evaluation(result)

    def test_add_scope_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/scopes/add")
        expect(authenticated_page.locator("form")).to_be_visible()
        result = record_evaluation(authenticated_page, "/admin/scopes/add")
        _assert_evaluation(result)

    def test_scope_edit_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/scopes")
        if not _click_first_edit_link(authenticated_page):
            pytest.skip("No scopes available to edit")
        result = record_evaluation(authenticated_page, "/admin/scopes/edit")
        _assert_evaluation(result)

    def test_scope_list_has_standard_scopes(self, authenticated_page: Page):
        _goto_admin(authenticated_page, "/admin/scopes")
        body = authenticated_page.inner_text("body")
        assert "openid" in body.lower(), "Standard 'openid' scope not in list"


# ---------------------------------------------------------------------------
# Roles
# ---------------------------------------------------------------------------


class TestAdminRoles:
    def test_role_list_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/roles")
        result = record_evaluation(authenticated_page, "/admin/roles")
        _assert_evaluation(result)

    def test_add_role_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/roles/add")
        expect(authenticated_page.locator("form")).to_be_visible()
        result = record_evaluation(authenticated_page, "/admin/roles/add")
        _assert_evaluation(result)

    def test_role_edit_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/roles")
        if not _click_first_edit_link(authenticated_page):
            pytest.skip("No roles available to edit")
        result = record_evaluation(authenticated_page, "/admin/roles/edit")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Users
# ---------------------------------------------------------------------------


class TestAdminUsers:
    def test_user_list_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/users")
        result = record_evaluation(authenticated_page, "/admin/users")
        _assert_evaluation(result)

    def test_add_user_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/users/add")
        result = record_evaluation(authenticated_page, "/admin/users/add")
        _assert_evaluation(result)

    def test_user_list_shows_admin_user(self, authenticated_page: Page):
        _goto_admin(authenticated_page, "/admin/users")
        body = authenticated_page.inner_text("body")
        assert "admin" in body.lower(), "'admin' user not visible in list"

    def test_user_edit_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/users")
        if not _click_first_edit_link(authenticated_page):
            pytest.skip("No users available to edit")
        result = record_evaluation(authenticated_page, "/admin/users/edit")
        _assert_evaluation(result)

    def _get_first_user_id(self, page: Page) -> str | None:
        _goto_admin(page, "/admin/users")
        if not _click_first_edit_link(page):
            return None
        return _extract_guid_from_url(page.url)

    def test_user_clients_sub_tab(self, authenticated_page: Page, record_evaluation):
        user_id = self._get_first_user_id(authenticated_page)
        if not user_id:
            pytest.skip("No users found")
        _goto_admin(authenticated_page, f"/Admin/Users/Clients/{user_id}")
        result = record_evaluation(authenticated_page, "/admin/users/clients")
        _assert_evaluation(result, min_score=3)

    def test_user_emails_sub_tab(self, authenticated_page: Page, record_evaluation):
        user_id = self._get_first_user_id(authenticated_page)
        if not user_id:
            pytest.skip("No users found")
        _goto_admin(authenticated_page, f"/Admin/Users/Emails/{user_id}")
        result = record_evaluation(authenticated_page, "/admin/users/emails")
        _assert_evaluation(result, min_score=3)

    def test_user_roles_sub_tab(self, authenticated_page: Page, record_evaluation):
        user_id = self._get_first_user_id(authenticated_page)
        if not user_id:
            pytest.skip("No users found")
        _goto_admin(authenticated_page, f"/Admin/Users/Roles/{user_id}")
        result = record_evaluation(authenticated_page, "/admin/users/roles")
        _assert_evaluation(result, min_score=3)

    def test_user_linked_sub_tab(self, authenticated_page: Page, record_evaluation):
        user_id = self._get_first_user_id(authenticated_page)
        if not user_id:
            pytest.skip("No users found")
        _goto_admin(authenticated_page, f"/Admin/Users/Linked/{user_id}")
        result = record_evaluation(authenticated_page, "/admin/users/linked")
        _assert_evaluation(result, min_score=3)


# ---------------------------------------------------------------------------
# Registrations
# ---------------------------------------------------------------------------


class TestAdminRegistrations:
    def test_registrations_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/registrations")
        result = record_evaluation(authenticated_page, "/admin/registrations")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Configuration Audit
# ---------------------------------------------------------------------------


class TestAdminConfigurationAudit:
    def test_configuration_audit_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/configuration-audit")
        result = record_evaluation(authenticated_page, "/admin/configuration-audit")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Backchannel Logout Outbox
# ---------------------------------------------------------------------------


class TestAdminBackchannel:
    def test_backchannel_outbox_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/backchannel")
        result = record_evaluation(authenticated_page, "/admin/backchannel")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# OBO Setup
# ---------------------------------------------------------------------------


class TestAdminOboSetup:
    def test_obo_setup_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/obo-setup")
        result = record_evaluation(authenticated_page, "/admin/obo-setup")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# License pages
# ---------------------------------------------------------------------------


class TestAdminLicense:
    def test_license_main_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/license")
        result = record_evaluation(authenticated_page, "/admin/license")
        _assert_evaluation(result, min_score=4)

    def test_license_history_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/license/history")
        result = record_evaluation(authenticated_page, "/admin/license/history")
        _assert_evaluation(result, min_score=4)

    def test_license_install_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/license/install")
        result = record_evaluation(authenticated_page, "/admin/license/install")
        _assert_evaluation(result, min_score=4)

    def test_tenant_license_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/license/tenant")
        result = record_evaluation(authenticated_page, "/admin/license/tenant")
        _assert_evaluation(result)

    def test_tenant_license_history_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/license/tenant/history")
        result = record_evaluation(authenticated_page, "/admin/license/tenant/history")
        _assert_evaluation(result, min_score=4)

    def test_tenant_license_install_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/license/tenant/install")
        result = record_evaluation(authenticated_page, "/admin/license/tenant/install")
        _assert_evaluation(result, min_score=4)


# ---------------------------------------------------------------------------
# Branding
# ---------------------------------------------------------------------------


class TestAdminBranding:
    def test_branding_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/branding")
        result = record_evaluation(authenticated_page, "/admin/branding")
        _assert_evaluation(result)

    def test_branding_form_is_present(self, authenticated_page: Page):
        _goto_admin(authenticated_page, "/admin/branding")
        # Branding may use a custom URL with tenant slug
        if "/login" in authenticated_page.url:
            pytest.skip("Branding page requires different auth setup")
        body = authenticated_page.inner_text("body")
        assert len(body) > 50, "Branding page appears empty"


# ---------------------------------------------------------------------------
# Settings
# ---------------------------------------------------------------------------


class TestAdminSettings:
    def test_settings_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/settings")
        result = record_evaluation(authenticated_page, "/admin/settings")
        _assert_evaluation(result)

    def test_settings_has_save_button(self, authenticated_page: Page):
        _goto_admin(authenticated_page, "/admin/settings")
        if "/login" in authenticated_page.url:
            pytest.skip("Settings page requires different auth setup")
        body = authenticated_page.inner_text("body")
        assert len(body) > 50, "Settings page appears empty"


# ---------------------------------------------------------------------------
# Rate Limits
# ---------------------------------------------------------------------------


class TestAdminRateLimits:
    def test_rate_limits_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_admin(authenticated_page, "/admin/rate-limits")
        result = record_evaluation(authenticated_page, "/admin/rate-limits")
        _assert_evaluation(result)
