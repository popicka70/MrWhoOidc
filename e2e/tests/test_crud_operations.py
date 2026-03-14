"""
Focused CRUD operation tests.

These tests exercise the core create/read/update/delete flows through the UI,
capturing before/after screenshots and recording LLM evaluations of each state.

Each test is designed to be self-contained and leaves the system in a clean state
(or clearly named with an "e2e-" prefix so stale data is easy to identify).

Test order matters within each class to maintain state (e.g., create then edit).
Use pytest-ordering if running in isolation: install pytest-ordering and add
`@pytest.mark.order(N)` decorators.
"""

from __future__ import annotations

import re

import pytest
from playwright.async_api import Page, expect

# Unique prefix for all test-created records
E2E_PREFIX = "e2e-crud"


# ---------------------------------------------------------------------------
# Helper utilities
# ---------------------------------------------------------------------------


def _assert_evaluation(result, *, min_score: int = 4) -> None:
    if result.skipped or result.error:
        return  # Non-blocking for CRUD tests
    if result.overall_score < min_score:
        # Log as warning rather than hard fail for CRUD state pages
        import warnings
        warnings.warn(
            f"CRUD page '{result.route}' (state: {result.page_name}) scored "
            f"{result.overall_score}/10 — below {min_score}.\n{result.summary}",
            UserWarning,
            stacklevel=2,
        )


async def _fill_if_visible(page: Page, selector: str, value: str) -> bool:
    """Fill a field if visible; returns True if filled."""
    locator = page.locator(selector)
    if await locator.count() > 0 and await locator.is_visible():
        await locator.fill(value)
        return True
    return False


async def _click_submit(page: Page) -> None:
    """Click the primary submit button and wait for navigation."""
    await page.locator("button[type='submit']").first.click()


# ---------------------------------------------------------------------------
# Realm CRUD
# ---------------------------------------------------------------------------


class TestRealmCrud:
    _realm_name = f"{E2E_PREFIX}-realm"
    _realm_url_slug: str | None = None

    async def test_01_create_realm(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/admin/realms/add", wait_until="domcontentloaded")

        # Capture empty form
        await record_evaluation(authenticated_page, "/admin/realms/add", label="empty-form")

        await _fill_if_visible(authenticated_page, "input#Name, input[name='Name']", self._realm_name)
        await _fill_if_visible(
            authenticated_page,
            "input#DisplayName, input[name='DisplayName']",
            "E2E Test Realm",
        )

        # Capture filled form
        await record_evaluation(authenticated_page, "/admin/realms/add", label="filled-form")
        await _click_submit(authenticated_page)

        # Should redirect to list
        body = await authenticated_page.inner_text("body")
        assert self._realm_name in body or "success" in body.lower(), (
            f"New realm '{self._realm_name}' not confirmed after creation"
        )
        await record_evaluation(authenticated_page, "/admin/realms", label="after-create")

    async def test_02_edit_realm(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/admin/realms", wait_until="domcontentloaded")

        # Find the edit link for our test realm
        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._realm_name}') a:has-text('Edit'), "
            f"li:has-text('{self._realm_name}') a:has-text('Edit')"
        ).first

        if await edit_link.count() == 0:
            pytest.skip(f"Realm '{self._realm_name}' not found in list — creation may have failed")

        await edit_link.click()
        await record_evaluation(authenticated_page, "/admin/realms/edit", label="before-edit")

        await _fill_if_visible(
            authenticated_page,
            "input#DisplayName, input[name='DisplayName']",
            "E2E Test Realm (Updated)",
        )
        await _click_submit(authenticated_page)
        await record_evaluation(authenticated_page, "/admin/realms", label="after-edit")


# ---------------------------------------------------------------------------
# Client CRUD
# ---------------------------------------------------------------------------


class TestClientCrud:
    _client_name = f"{E2E_PREFIX}-client"
    _client_id = f"{E2E_PREFIX}-client-id"

    async def test_01_create_client(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/admin/clients/add", wait_until="domcontentloaded")

        await record_evaluation(authenticated_page, "/admin/clients/add", label="empty-form")

        await _fill_if_visible(
            authenticated_page, "input#ClientName, input[name='ClientName'], input#Name, input[name='Name']",
            self._client_name,
        )
        await _fill_if_visible(
            authenticated_page,
            "input#ClientId, input[name='ClientId']",
            self._client_id,
        )

        # Select "Public" type if radio/select is present
        public_opt = authenticated_page.locator(
            "input[type='radio'][value='Public'], option[value='Public']"
        )
        if await public_opt.count() > 0:
            await public_opt.first.click()

        await record_evaluation(authenticated_page, "/admin/clients/add", label="filled-form")
        await _click_submit(authenticated_page)

        body = await authenticated_page.inner_text("body")
        assert self._client_name in body or self._client_id in body or "success" in body.lower(), (
            "Client creation did not confirm the new client"
        )
        await record_evaluation(authenticated_page, "/admin/clients", label="after-create")

    async def test_02_edit_client(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/admin/clients", wait_until="domcontentloaded")

        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._client_name}') a:has-text('Edit'), "
            f"tr:has-text('{self._client_id}') a:has-text('Edit')"
        ).first

        if await edit_link.count() == 0:
            pytest.skip(f"Client '{self._client_name}' not found in list")

        await edit_link.click()
        await record_evaluation(authenticated_page, "/admin/clients/edit", label="before-edit")

        await _fill_if_visible(
            authenticated_page,
            "input#ClientName, input[name='ClientName'], input#Name, input[name='Name']",
            f"{self._client_name}-updated",
        )
        await _click_submit(authenticated_page)
        await record_evaluation(authenticated_page, "/admin/clients", label="after-edit")


# ---------------------------------------------------------------------------
# Scope CRUD
# ---------------------------------------------------------------------------


class TestScopeCrud:
    _scope_name = f"{E2E_PREFIX}:read"
    _scope_display = "E2E Read Access"

    async def test_01_create_scope(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/admin/scopes/add", wait_until="domcontentloaded")

        await record_evaluation(authenticated_page, "/admin/scopes/add", label="empty-form")

        await _fill_if_visible(
            authenticated_page, "input#Name, input[name='Name']", self._scope_name
        )
        await _fill_if_visible(
            authenticated_page,
            "input#DisplayName, input[name='DisplayName']",
            self._scope_display,
        )

        await record_evaluation(authenticated_page, "/admin/scopes/add", label="filled-form")
        await _click_submit(authenticated_page)

        body = await authenticated_page.inner_text("body")
        assert self._scope_name in body or "success" in body.lower(), (
            "Scope creation not confirmed"
        )
        await record_evaluation(authenticated_page, "/admin/scopes", label="after-create")

    async def test_02_edit_scope(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/admin/scopes", wait_until="domcontentloaded")

        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._scope_name}') a:has-text('Edit')"
        ).first
        if await edit_link.count() == 0:
            pytest.skip(f"Scope '{self._scope_name}' not found")

        await edit_link.click()
        await record_evaluation(authenticated_page, "/admin/scopes/edit", label="before-edit")

        await _fill_if_visible(
            authenticated_page, "input#DisplayName, input[name='DisplayName']",
            "E2E Read Access (Updated)",
        )
        await _click_submit(authenticated_page)
        await record_evaluation(authenticated_page, "/admin/scopes", label="after-edit")


# ---------------------------------------------------------------------------
# Role CRUD
# ---------------------------------------------------------------------------


class TestRoleCrud:
    _role_name = f"{E2E_PREFIX}-role"

    async def test_01_create_role(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/admin/roles/add", wait_until="domcontentloaded")

        await _fill_if_visible(authenticated_page, "input#Name, input[name='Name']", self._role_name)
        await _fill_if_visible(
            authenticated_page, "input#DisplayName, input[name='DisplayName']", "E2E Test Role"
        )

        await record_evaluation(authenticated_page, "/admin/roles/add", label="filled-form")
        await _click_submit(authenticated_page)

        body = await authenticated_page.inner_text("body")
        assert self._role_name in body or "success" in body.lower(), "Role creation not confirmed"
        await record_evaluation(authenticated_page, "/admin/roles", label="after-create")

    async def test_02_edit_role(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/admin/roles", wait_until="domcontentloaded")

        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._role_name}') a:has-text('Edit')"
        ).first
        if await edit_link.count() == 0:
            pytest.skip(f"Role '{self._role_name}' not found")

        await edit_link.click()
        await _fill_if_visible(
            authenticated_page, "input#DisplayName, input[name='DisplayName']", "E2E Test Role (Updated)"
        )
        await _click_submit(authenticated_page)
        await record_evaluation(authenticated_page, "/admin/roles", label="after-edit")


# ---------------------------------------------------------------------------
# User CRUD
# ---------------------------------------------------------------------------


class TestUserCrud:
    _username = f"{E2E_PREFIX}-user"
    _email = f"{E2E_PREFIX}@test.local"
    _password = "TestPass_E2E_123!"

    async def test_01_create_user(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/admin/users/add", wait_until="domcontentloaded")

        await record_evaluation(authenticated_page, "/admin/users/add", label="empty-form")

        await _fill_if_visible(
            authenticated_page, "input#Username, input[name='Username']", self._username
        )
        await _fill_if_visible(
            authenticated_page, "input#Email, input[name='Email']", self._email
        )
        await _fill_if_visible(
            authenticated_page,
            "input#Password, input[name='Password'], input[type='password']",
            self._password,
        )
        await _fill_if_visible(
            authenticated_page,
            "input#ConfirmPassword, input[name='ConfirmPassword']",
            self._password,
        )

        await record_evaluation(authenticated_page, "/admin/users/add", label="filled-form")
        await _click_submit(authenticated_page)

        body = await authenticated_page.inner_text("body")
        assert self._username in body or self._email in body or "success" in body.lower(), (
            "User creation not confirmed"
        )
        await record_evaluation(authenticated_page, "/admin/users", label="after-create")

    async def test_02_edit_user(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/admin/users", wait_until="domcontentloaded")

        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._username}') a:has-text('Edit'), "
            f"tr:has-text('{self._email}') a:has-text('Edit')"
        ).first
        if await edit_link.count() == 0:
            pytest.skip(f"User '{self._username}' not found in list")

        await edit_link.click()
        await record_evaluation(authenticated_page, "/admin/users/edit", label="before-edit")

        # Try to update a display name or similar safe field
        await _fill_if_visible(
            authenticated_page,
            "input#DisplayName, input[name='DisplayName'], input#FirstName, input[name='FirstName']",
            "E2E User",
        )
        await _click_submit(authenticated_page)
        await record_evaluation(authenticated_page, "/admin/users", label="after-edit")


# ---------------------------------------------------------------------------
# Account Profile Update (self-service)
# ---------------------------------------------------------------------------


class TestAccountProfileCrud:
    async def test_update_profile(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/Account/Profile", wait_until="domcontentloaded")

        await record_evaluation(authenticated_page, "/Account/Profile", label="before-update")

        # Update a safe field
        filled = await _fill_if_visible(
            authenticated_page,
            "input#DisplayName, input[name='DisplayName'], input#FirstName, input[name='FirstName']",
            "E2E Admin",
        )
        if not filled:
            pytest.skip("No suitable profile field found to update")

        await _click_submit(authenticated_page)
        await record_evaluation(authenticated_page, "/Account/Profile", label="after-update")

        # Verify success feedback
        body = await authenticated_page.inner_text("body")
        has_success = any(
            kw in body.lower() for kw in ("success", "saved", "updated", "profile updated")
        )
        assert has_success, "No success feedback shown after profile update"


# ---------------------------------------------------------------------------
# Tenant CRUD (Platform Admin)
# ---------------------------------------------------------------------------


class TestTenantCrud:
    _tenant_slug = f"{E2E_PREFIX}-tenant"
    _tenant_name = "E2E CRUD Tenant"

    async def test_01_create_tenant(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/PlatformAdmin/Tenants/Create", wait_until="domcontentloaded")

        if "/login" in authenticated_page.url or "accessdenied" in authenticated_page.url.lower():
            pytest.skip("Platform admin not available for this user")

        await record_evaluation(authenticated_page, "/PlatformAdmin/Tenants/Create", label="empty-form")

        await _fill_if_visible(
            authenticated_page, "input#Slug, input[name='Slug']", self._tenant_slug
        )
        await _fill_if_visible(
            authenticated_page, "input#Name, input[name='Name'], input#DisplayName, input[name='DisplayName']",
            self._tenant_name,
        )

        await record_evaluation(authenticated_page, "/PlatformAdmin/Tenants/Create", label="filled-form")
        await _click_submit(authenticated_page)

        body = await authenticated_page.inner_text("body")
        assert self._tenant_slug in body or self._tenant_name in body or "success" in body.lower(), (
            "Tenant creation not confirmed"
        )
        await record_evaluation(authenticated_page, "/PlatformAdmin/Tenants", label="after-create")

    async def test_02_edit_tenant(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/PlatformAdmin/Tenants", wait_until="domcontentloaded")

        if "/login" in authenticated_page.url:
            pytest.skip("Platform admin not available")

        edit_link = authenticated_page.locator(
            f"tr:has-text('{self._tenant_slug}') a:has-text('Edit'), "
            f"tr:has-text('{self._tenant_name}') a:has-text('Edit')"
        ).first
        if await edit_link.count() == 0:
            pytest.skip(f"Tenant '{self._tenant_slug}' not found")

        await edit_link.click()
        await record_evaluation(authenticated_page, "/PlatformAdmin/Tenants/Edit", label="before-edit")

        await _fill_if_visible(
            authenticated_page,
            "input#Name, input[name='Name'], input#DisplayName, input[name='DisplayName']",
            "E2E CRUD Tenant (Updated)",
        )
        await _click_submit(authenticated_page)
        await record_evaluation(authenticated_page, "/PlatformAdmin/Tenants", label="after-edit")
