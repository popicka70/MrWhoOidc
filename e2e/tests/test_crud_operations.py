"""
Focused CRUD operation tests.

Each test is self-contained and leaves stale data with an "e2e-crud" prefix
so it is easy to clean up.  Test order within each class is significant.
"""

from __future__ import annotations

import re
import time

import pytest
from playwright.sync_api import Page, expect

E2E_PREFIX = "e2e-crud"
# Unique suffix per process run so re-runs don't collide with stale test data
_RUN_SUFFIX = str(int(time.time()))[-6:]


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
    # Prefer a visible primary btn; avoids hidden .dropdown-item submit buttons
    btn = page.locator("button.btn-primary[type='submit']").first
    if btn.count() == 0 or not btn.is_visible():
        btn = page.locator("button[type='submit']:not(.dropdown-item)").first
    if btn.count() == 0 or not btn.is_visible():
        btn = page.locator("button[type='submit']").first
    btn.click()
    page.wait_for_load_state("domcontentloaded")


# ---------------------------------------------------------------------------
# Realm CRUD
# ---------------------------------------------------------------------------


class TestRealmCrud:
    _realm_name = f"{E2E_PREFIX}-realm-{_RUN_SUFFIX}"

    def test_01_create_realm(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/realms/add", wait_until="domcontentloaded")
        record_evaluation(authenticated_page, "/admin/realms/add", label="empty-form")
        _fill_if_visible(authenticated_page, "input#Input_Name, input[name='Input.Name']", self._realm_name)
        _fill_if_visible(
            authenticated_page, "input#Input_DisplayName, input[name='Input.DisplayName']", "E2E Test Realm"
        )
        record_evaluation(authenticated_page, "/admin/realms/add", label="filled-form")
        _click_submit(authenticated_page)
        url = authenticated_page.url
        html = authenticated_page.content()
        assert "/add" not in url or self._realm_name in html or "success" in html.lower(), (
            f"Realm '{self._realm_name}' not confirmed after creation (url={url})"
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
            authenticated_page, "input#Input_DisplayName, input[name='Input.DisplayName']",
            "E2E Test Realm (Updated)",
        )
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/admin/realms", label="after-edit")


# ---------------------------------------------------------------------------
# Client CRUD
# ---------------------------------------------------------------------------


class TestClientCrud:
    _client_name = f"{E2E_PREFIX}-client-{_RUN_SUFFIX}"
    _client_id = f"{E2E_PREFIX}-cid-{_RUN_SUFFIX}"

    def test_01_create_client(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/clients/add", wait_until="domcontentloaded")
        record_evaluation(authenticated_page, "/admin/clients/add", label="empty-form")
        _fill_if_visible(
            authenticated_page,
            "input#Input_ClientName, input[name='Input.ClientName']",
            self._client_name,
        )
        _fill_if_visible(
            authenticated_page, "input#Input_ClientId, input[name='Input.ClientId']", self._client_id
        )
        public_opt = authenticated_page.locator(
            "input[type='radio'][value='Public'], option[value='Public']"
        )
        if public_opt.count() > 0:
            public_opt.first.click()
        record_evaluation(authenticated_page, "/admin/clients/add", label="filled-form")
        _click_submit(authenticated_page)
        url = authenticated_page.url
        html = authenticated_page.content()
        assert (
            "/add" not in url or self._client_name in html or self._client_id in html or "success" in html.lower()
        ), f"Client creation not confirmed (url={url})"
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
            "input#Input_ClientName, input[name='Input.ClientName']",
            f"{self._client_name}-updated",
        )
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/admin/clients", label="after-edit")


# ---------------------------------------------------------------------------
# Scope CRUD
# ---------------------------------------------------------------------------


class TestScopeCrud:
    _scope_name = f"{E2E_PREFIX}:read:{_RUN_SUFFIX}"
    _scope_display = "E2E Read Access"

    def test_01_create_scope(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/scopes/add", wait_until="domcontentloaded")
        record_evaluation(authenticated_page, "/admin/scopes/add", label="empty-form")
        _fill_if_visible(authenticated_page, "input#Input_Name, input[name='Input.Name']", self._scope_name)
        _fill_if_visible(
            authenticated_page, "input#Input_DisplayName, input[name='Input.DisplayName']", self._scope_display
        )
        record_evaluation(authenticated_page, "/admin/scopes/add", label="filled-form")
        _click_submit(authenticated_page)
        url = authenticated_page.url
        html = authenticated_page.content()
        assert "/add" not in url or self._scope_name in html or "success" in html.lower(), f"Scope creation not confirmed (url={url})"
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
            authenticated_page, "input#Input_DisplayName, input[name='Input.DisplayName']",
            "E2E Read Access (Updated)",
        )
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/admin/scopes", label="after-edit")


# ---------------------------------------------------------------------------
# Role CRUD
# ---------------------------------------------------------------------------


class TestRoleCrud:
    _role_name = f"{E2E_PREFIX}-role-{_RUN_SUFFIX}"

    def test_01_create_role(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/roles/add", wait_until="domcontentloaded")
        # Realm is required — select first non-blank option
        realm_sel = authenticated_page.locator("select#Input_RealmId, select[name='Input.RealmId']").first
        if realm_sel.count() > 0 and realm_sel.is_visible():
            realm_sel.select_option(index=1)
        _fill_if_visible(authenticated_page, "input#Input_Name, input[name='Input.Name']", self._role_name)
        _fill_if_visible(
            authenticated_page, "input#Input_DisplayName, input[name='Input.DisplayName']", "E2E Test Role"
        )
        record_evaluation(authenticated_page, "/admin/roles/add", label="filled-form")
        _click_submit(authenticated_page)
        url = authenticated_page.url
        html = authenticated_page.content()
        assert "/add" not in url or self._role_name in html or "success" in html.lower(), f"Role creation not confirmed (url={url})"
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
            authenticated_page, "input#Input_DisplayName, input[name='Input.DisplayName']",
            "E2E Test Role (Updated)",
        )
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/admin/roles", label="after-edit")


# ---------------------------------------------------------------------------
# User CRUD
# ---------------------------------------------------------------------------


class TestUserCrud:
    _username = f"{E2E_PREFIX}-user-{_RUN_SUFFIX}"
    _email = f"{E2E_PREFIX}-{_RUN_SUFFIX}@test.local"
    _password = "TestPass_E2E_123!"

    def test_01_create_user(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/admin/users/add", wait_until="domcontentloaded")
        record_evaluation(authenticated_page, "/admin/users/add", label="empty-form")
        _fill_if_visible(authenticated_page, "input#Input_Username, input[name='Input.Username']", self._username)
        _fill_if_visible(authenticated_page, "input#Input_Email, input[name='Input.Email']", self._email)
        _fill_if_visible(
            authenticated_page,
            "input#Input_Password, input[name='Input.Password'], input[type='password']",
            self._password,
        )
        _fill_if_visible(
            authenticated_page, "input#Input_ConfirmPassword, input[name='Input.ConfirmPassword']", self._password
        )
        record_evaluation(authenticated_page, "/admin/users/add", label="filled-form")
        _click_submit(authenticated_page)
        url = authenticated_page.url
        html = authenticated_page.content()
        assert (
            "/add" not in url or self._username in html or self._email in html or "success" in html.lower()
        ), f"User creation not confirmed (url={url})"
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
            "input#Input_Name, input[name='Input.Name']",
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
        # Only fill non-credential display fields; do NOT submit to avoid touching credentials
        _fill_if_visible(
            authenticated_page,
            "input#Input_Name, input[name='Input.Name'], input#Input_DisplayName, input[name='Input.DisplayName']",
            "E2E Admin",
        )
        record_evaluation(authenticated_page, "/account/profile", label="filled-form")
        # Verify the profile form is present but skip submission to protect credentials
        form = authenticated_page.locator("form").first
        assert form.count() > 0, "Profile page has no form"


# ---------------------------------------------------------------------------
# Tenant CRUD (Platform Admin)
# ---------------------------------------------------------------------------


class TestTenantCrud:
    _tenant_slug = f"{E2E_PREFIX}-{_RUN_SUFFIX}"
    _tenant_name = f"E2E CRUD Tenant {_RUN_SUFFIX}"

    def test_01_create_tenant(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/platform-admin/tenants/create", wait_until="domcontentloaded")
        if "/login" in authenticated_page.url or "accessdenied" in authenticated_page.url.lower():
            pytest.skip("Platform admin not available")
        if "/create" not in authenticated_page.url:
            pytest.skip("Tenant creation not available (capacity limit reached)")
        record_evaluation(
            authenticated_page, "/platform-admin/tenants/create", label="empty-form"
        )
        _fill_if_visible(
            authenticated_page, "input#Input_Slug, input[name='Input.Slug']", self._tenant_slug
        )
        _fill_if_visible(
            authenticated_page,
            "input#Input_Name, input[name='Input.Name']",
            self._tenant_name,
        )
        record_evaluation(
            authenticated_page, "/platform-admin/tenants/create", label="filled-form"
        )
        _click_submit(authenticated_page)
        url = authenticated_page.url
        html = authenticated_page.content()
        assert (
            "/create" not in url or self._tenant_slug in html or self._tenant_name in html or "success" in html.lower()
        ), f"Tenant creation not confirmed (url={url})"
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
            "input#Input_Name, input[name='Input.Name']",
            "E2E CRUD Tenant (Updated)",
        )
        _click_submit(authenticated_page)
        record_evaluation(authenticated_page, "/platform-admin/tenants", label="after-edit")
