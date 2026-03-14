"""
Focused CRUD operation tests.

Each test is self-contained and leaves stale data with an "e2e-crud" prefix
so it is easy to clean up.  Test order within each class is significant.
"""

from __future__ import annotations

import re

import pytest
from playwright.sync_api import Page, expect

E2E_PREFIX = "e2e-crud"


def _assert_evaluation(result, *, min_score: int = 4) -> None:
    if result.skipped or result.error:
        return
    if result.overall_score < min_score:
        import warnings
        warnings.warn(
            f"CRUD page '{result.route}' scored {result.overall_score}/10 (<{min_score}).\n"
            f"{result.summary}",
            UserWarning,
            stacklevel=2,
        )


def _fill_if_visible(page: Page, selector: str, value: str) -> bool:
    locator = page.locator(selector).first
    if locator.count() > 0 and locator.is_visible():
        locator.fill(value)
        return True
    return False


def _click_submit(page: Page) -> None:
    page.locator("button[type='submit']").first.click()
    page.wait_for_load_state("domcontentloaded")


# ---------------------------------------------------------------------------
# Realm CRUD
# ---------------------------------------------------------------------------


class TestRealmCrud:
    _realm_name = f"{E2E_PREFIX}-realm"

    def test_01_create_realm(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/realms/add", wait_until="domcontentloaded")
        record_evaluation(authenticated_page, "/admin/realms/add", label="empty-form")
        _fill_if_visible(authenticated_page, "input#Name, input[name='Name']", self._realm_name)
        _fill_if_visible(
            authenticated_page, "input#DisplayName, input[name='DisplayName']", "E2E Test Realm"
        )
        record_evaluation(authenticated_page, "/admin/realms/add", label="filled-form")
        _click_submit(authenticated_page)
        body = authenticated_page.inner_text("body")
        assert self._realm_name in body or "success" in body.lower(), (
            f"Realm '{self._realm_name}' not confirmed after creation"
        )
        record_evaluation(authenticated_page, "/admin/realms", label="after-create")

    def test_02_edit_realm(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/realms", wait_until="domcontentloaded")
        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._realm_name}') a:has-text('Edit'), "
            f"li:has-text('{self._realm_name}') a:has-text('Edit')"
        ).first
        if edit_link.count() == 0:
            pytest.skip(f"Realm '{self._realm_name}' not found")
        edit_link.click()
        authenticated_page.wait_for_load_state("domcontentloaded")
        record_evaluation(authenticated_page, "/admin/realms/edit", label="before-edit")
        _fill_if_visible(
            authenticated_page, "input#DisplayName, input[name='DisplayName']",
            "E2E Test Realm (Updated)",
        )
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/admin/realms", label="after-edit")


# ---------------------------------------------------------------------------
# Client CRUD
# ---------------------------------------------------------------------------


class TestClientCrud:
    _client_name = f"{E2E_PREFIX}-client"
    _client_id = f"{E2E_PREFIX}-client-id"

    def test_01_create_client(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/clients/add", wait_until="domcontentloaded")
        record_evaluation(authenticated_page, "/admin/clients/add", label="empty-form")
        _fill_if_visible(
            authenticated_page,
            "input#ClientName, input[name='ClientName'], input#Name, input[name='Name']",
            self._client_name,
        )
        _fill_if_visible(
            authenticated_page, "input#ClientId, input[name='ClientId']", self._client_id
        )
        public_opt = authenticated_page.locator(
            "input[type='radio'][value='Public'], option[value='Public']"
        )
        if public_opt.count() > 0:
            public_opt.first.click()
        record_evaluation(authenticated_page, "/admin/clients/add", label="filled-form")
        _click_submit(authenticated_page)
        body = authenticated_page.inner_text("body")
        assert (
            self._client_name in body or self._client_id in body or "success" in body.lower()
        ), "Client creation not confirmed"
        record_evaluation(authenticated_page, "/admin/clients", label="after-create")

    def test_02_edit_client(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/clients", wait_until="domcontentloaded")
        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._client_name}') a:has-text('Edit'), "
            f"tr:has-text('{self._client_id}') a:has-text('Edit')"
        ).first
        if edit_link.count() == 0:
            pytest.skip(f"Client '{self._client_name}' not found")
        edit_link.click()
        authenticated_page.wait_for_load_state("domcontentloaded")
        record_evaluation(authenticated_page, "/admin/clients/edit", label="before-edit")
        _fill_if_visible(
            authenticated_page,
            "input#ClientName, input[name='ClientName'], input#Name, input[name='Name']",
            f"{self._client_name}-updated",
        )
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/admin/clients", label="after-edit")


# ---------------------------------------------------------------------------
# Scope CRUD
# ---------------------------------------------------------------------------


class TestScopeCrud:
    _scope_name = f"{E2E_PREFIX}:read"
    _scope_display = "E2E Read Access"

    def test_01_create_scope(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/scopes/add", wait_until="domcontentloaded")
        record_evaluation(authenticated_page, "/admin/scopes/add", label="empty-form")
        _fill_if_visible(authenticated_page, "input#Name, input[name='Name']", self._scope_name)
        _fill_if_visible(
            authenticated_page, "input#DisplayName, input[name='DisplayName']", self._scope_display
        )
        record_evaluation(authenticated_page, "/admin/scopes/add", label="filled-form")
        _click_submit(authenticated_page)
        body = authenticated_page.inner_text("body")
        assert self._scope_name in body or "success" in body.lower(), "Scope creation not confirmed"
        record_evaluation(authenticated_page, "/admin/scopes", label="after-create")

    def test_02_edit_scope(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/scopes", wait_until="domcontentloaded")
        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._scope_name}') a:has-text('Edit')"
        ).first
        if edit_link.count() == 0:
            pytest.skip(f"Scope '{self._scope_name}' not found")
        edit_link.click()
        authenticated_page.wait_for_load_state("domcontentloaded")
        record_evaluation(authenticated_page, "/admin/scopes/edit", label="before-edit")
        _fill_if_visible(
            authenticated_page, "input#DisplayName, input[name='DisplayName']",
            "E2E Read Access (Updated)",
        )
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/admin/scopes", label="after-edit")


# ---------------------------------------------------------------------------
# Role CRUD
# ---------------------------------------------------------------------------


class TestRoleCrud:
    _role_name = f"{E2E_PREFIX}-role"

    def test_01_create_role(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/roles/add", wait_until="domcontentloaded")
        _fill_if_visible(authenticated_page, "input#Name, input[name='Name']", self._role_name)
        _fill_if_visible(
            authenticated_page, "input#DisplayName, input[name='DisplayName']", "E2E Test Role"
        )
        record_evaluation(authenticated_page, "/admin/roles/add", label="filled-form")
        _click_submit(authenticated_page)
        body = authenticated_page.inner_text("body")
        assert self._role_name in body or "success" in body.lower(), "Role creation not confirmed"
        record_evaluation(authenticated_page, "/admin/roles", label="after-create")

    def test_02_edit_role(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/roles", wait_until="domcontentloaded")
        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._role_name}') a:has-text('Edit')"
        ).first
        if edit_link.count() == 0:
            pytest.skip(f"Role '{self._role_name}' not found")
        edit_link.click()
        authenticated_page.wait_for_load_state("domcontentloaded")
        _fill_if_visible(
            authenticated_page, "input#DisplayName, input[name='DisplayName']",
            "E2E Test Role (Updated)",
        )
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/admin/roles", label="after-edit")


# ---------------------------------------------------------------------------
# User CRUD
# ---------------------------------------------------------------------------


class TestUserCrud:
    _username = f"{E2E_PREFIX}-user"
    _email = f"{E2E_PREFIX}@test.local"
    _password = "TestPass_E2E_123!"

    def test_01_create_user(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/users/add", wait_until="domcontentloaded")
        record_evaluation(authenticated_page, "/admin/users/add", label="empty-form")
        _fill_if_visible(authenticated_page, "input#Username, input[name='Username']", self._username)
        _fill_if_visible(authenticated_page, "input#Email, input[name='Email']", self._email)
        _fill_if_visible(
            authenticated_page,
            "input#Password, input[name='Password'], input[type='password']",
            self._password,
        )
        _fill_if_visible(
            authenticated_page, "input#ConfirmPassword, input[name='ConfirmPassword']", self._password
        )
        record_evaluation(authenticated_page, "/admin/users/add", label="filled-form")
        _click_submit(authenticated_page)
        body = authenticated_page.inner_text("body")
        assert self._username in body or self._email in body or "success" in body.lower(), (
            "User creation not confirmed"
        )
        record_evaluation(authenticated_page, "/admin/users", label="after-create")

    def test_02_edit_user(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/users", wait_until="domcontentloaded")
        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._username}') a:has-text('Edit'), "
            f"tr:has-text('{self._email}') a:has-text('Edit')"
        ).first
        if edit_link.count() == 0:
            pytest.skip(f"User '{self._username}' not found")
        edit_link.click()
        authenticated_page.wait_for_load_state("domcontentloaded")
        record_evaluation(authenticated_page, "/admin/users/edit", label="before-edit")
        _fill_if_visible(
            authenticated_page,
            "input#DisplayName, input[name='DisplayName'], input#FirstName, input[name='FirstName']",
            "E2E User",
        )
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/admin/users", label="after-edit")


# ---------------------------------------------------------------------------
# Account Profile Update (self-service)
# ---------------------------------------------------------------------------


class TestAccountProfileCrud:
    def test_update_profile(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/account/profile", wait_until="domcontentloaded")
        if "/login" in authenticated_page.url:
            pytest.skip("Redirected to login -- session expired")
        record_evaluation(authenticated_page, "/account/profile", label="before-update")
        filled = _fill_if_visible(
            authenticated_page,
            "input#DisplayName, input[name='DisplayName'], input#FirstName, input[name='FirstName']",
            "E2E Admin",
        )
        if not filled:
            pytest.skip("No suitable profile field to update")
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/account/profile", label="after-update")
        body = authenticated_page.inner_text("body")
        has_success = any(
            kw in body.lower() for kw in ("success", "saved", "updated", "profile updated")
        )
        assert has_success, "No success feedback after profile update"


# ---------------------------------------------------------------------------
# Tenant CRUD (Platform Admin)
# ---------------------------------------------------------------------------


class TestTenantCrud:
    _tenant_slug = f"{E2E_PREFIX}-tenant"
    _tenant_name = "E2E CRUD Tenant"

    def test_01_create_tenant(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/platform-admin/tenants/create", wait_until="domcontentloaded")
        if "/login" in authenticated_page.url or "accessdenied" in authenticated_page.url.lower():
            pytest.skip("Platform admin not available")
        record_evaluation(
            authenticated_page, "/platform-admin/tenants/create", label="empty-form"
        )
        _fill_if_visible(
            authenticated_page, "input#Slug, input[name='Slug']", self._tenant_slug
        )
        _fill_if_visible(
            authenticated_page,
            "input#Name, input[name='Name'], input#DisplayName, input[name='DisplayName']",
            self._tenant_name,
        )
        record_evaluation(
            authenticated_page, "/platform-admin/tenants/create", label="filled-form"
        )
        _click_submit(authenticated_page)
        body = authenticated_page.inner_text("body")
        assert (
            self._tenant_slug in body or self._tenant_name in body or "success" in body.lower()
        ), "Tenant creation not confirmed"
        record_evaluation(authenticated_page, "/platform-admin/tenants", label="after-create")

    def test_02_edit_tenant(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/platform-admin/tenants", wait_until="domcontentloaded")
        if "/login" in authenticated_page.url:
            pytest.skip("Platform admin not available")
        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._tenant_slug}') a:has-text('Edit'), "
            f"tr:has-text('{self._tenant_name}') a:has-text('Edit')"
        ).first
        if edit_link.count() == 0:
            pytest.skip(f"Tenant '{self._tenant_slug}' not found")
        edit_link.click()
        authenticated_page.wait_for_load_state("domcontentloaded")
        record_evaluation(authenticated_page, "/platform-admin/tenants/edit", label="before-edit")
        _fill_if_visible(
            authenticated_page,
            "input#Name, input[name='Name'], input#DisplayName, input[name='DisplayName']",
            "E2E CRUD Tenant (Updated)",
        )
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/platform-admin/tenants", label="after-edit")
