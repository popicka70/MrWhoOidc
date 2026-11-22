"""End-to-end coverage for consent-based user assistance.

The requester grants a helper temporary access, the helper accepts from the in-app inbox or the email
link after signing in as the invited identity, and either party (or a tenant manager) can end it. The
tests use unique users and a tenant role created through the admin API so they never depend on the
seeded demo identities or on the outcome of another test.
"""

from __future__ import annotations

import html
import os
import re
import time
import uuid

import requests
from playwright.sync_api import Browser, BrowserContext, Page, expect

from conftest import BASE_URL, csrf_headers

MAILHOG_API = os.getenv("MAILHOG_API", "http://localhost:8025/api/v2")
RAZORCLIENT_URL = os.getenv("EXAMPLE_RAZORCLIENT_URL", "https://localhost:5003")
OIDCDEMO_URL = os.getenv("EXAMPLE_OIDCDEMO_URL", "https://localhost:5001")
PASSWORD = "Assistance-E2E-123!"
ACTIVE_TIMEOUT = 45_000


def _unique(prefix: str) -> str:
    return f"{prefix}-{uuid.uuid4().hex[:10]}"


def _create_user(admin_context: BrowserContext, username: str, email: str) -> dict:
    url = f"{BASE_URL}/admin/api/users"
    response = admin_context.request.post(
        url,
        data={
            "username": username,
            "email": email,
            "name": username,
            "password": PASSWORD,
        },
        headers=csrf_headers(admin_context.request, url),
    )
    assert response.status == 201, response.text()
    return response.json()


def _find_role_id(admin_context: BrowserContext, role_name: str) -> str | None:
    response = admin_context.request.get(f"{BASE_URL}/admin/api/roles")
    assert response.status == 200, response.text()
    roles = response.json()
    for role in roles:
        if role.get("name", role.get("Name")) == role_name:
            return str(role.get("id", role.get("Id")))
    return None


def _ensure_role(admin_context: BrowserContext, role_name: str) -> str:
    existing = _find_role_id(admin_context, role_name)
    if existing is not None:
        return existing

    realms_response = admin_context.request.get(f"{BASE_URL}/admin/api/realms")
    assert realms_response.status == 200, realms_response.text()
    realms = realms_response.json()
    default_realm = next(
        (realm for realm in realms if (realm.get("name", realm.get("Name")) == "default")),
        realms[0] if realms else None,
    )
    assert default_realm is not None, "The tenant has no realm to create the assistance role in"
    realm_id = str(default_realm.get("id", default_realm.get("Id")))

    url = f"{BASE_URL}/admin/api/roles"
    response = admin_context.request.post(
        url,
        data={"name": role_name, "displayName": role_name, "realmId": realm_id},
        headers=csrf_headers(admin_context.request, url),
    )
    if response.status == 409:
        role_id = _find_role_id(admin_context, role_name)
        assert role_id is not None, response.text()
        return role_id
    assert response.status == 201, response.text()
    payload = response.json()
    return str(payload.get("id", payload.get("Id")))


def _assign_role(admin_context: BrowserContext, user_id: str, role_id: str) -> None:
    url = f"{BASE_URL}/admin/api/users/{user_id}/roles"
    response = admin_context.request.post(
        url,
        data={"roleId": role_id},
        headers=csrf_headers(admin_context.request, url),
    )
    assert response.status in {200, 201, 409}, response.text()


def _assign_client(admin_context: BrowserContext, user_id: str, client_internal_id: str) -> None:
    url = f"{BASE_URL}/admin/api/users/{user_id}/clients"
    response = admin_context.request.post(
        url,
        data={"clientId": client_internal_id},
        headers=csrf_headers(admin_context.request, url),
    )
    assert response.status in {200, 201, 409}, response.text()


def _blazor_client_id(admin_context: BrowserContext) -> str:
    response = admin_context.request.get(f"{BASE_URL}/admin/api/clients")
    assert response.status == 200, response.text()
    payload = response.json()
    clients = payload.get("items", payload) if isinstance(payload, dict) else payload
    for client in clients:
        if client.get("clientId", client.get("ClientId")) == "blazor-web":
            return str(client.get("id", client.get("Id")))
    raise AssertionError("The blazor-web client is not registered")


def _configure_assistance(
    admin_context: BrowserContext,
    *,
    read_only_roles: str,
    read_write_roles: str = "",
    rights_mode: str = "intersection",
    manager_roles: str = "tenant-admin",
) -> None:
    """Saves the tenant assistance settings through the admin UI (the same surface an operator uses)."""
    page = admin_context.new_page()
    try:
        page.goto(f"{BASE_URL}/t/default/admin/settings", wait_until="domcontentloaded")
        expect(page.get_by_role("heading", name=re.compile("Tenant Settings", re.I))).to_be_visible()

        page.locator("#assistanceEnabled").check()
        page.locator("select[name='Input.AssistanceRightsMode']").select_option(rights_mode)
        page.locator("input[name='Input.AssistanceRequesterRoles']").fill("")
        page.locator("input[name='Input.AssistanceReadOnlyHelperRoles']").fill(read_only_roles)
        page.locator("input[name='Input.AssistanceReadWriteHelperRoles']").fill(read_write_roles)
        page.locator("input[name='Input.AssistanceManagerRoles']").fill(manager_roles)
        page.locator("select[name='Input.AssistanceReadOnlyTokenPolicy']").select_option("markerOnly")
        page.locator("input[name='Input.AssistanceDefaultDurationMinutes']").fill("15")
        page.locator("input[name='Input.AssistanceMaxDurationMinutes']").fill("60")
        page.locator("input[name='Input.AssistanceRequestLifetimeMinutes']").fill("10")
        page.get_by_role("button", name="Save Settings").click()
        page.wait_for_load_state("domcontentloaded")
        expect(page.locator("body")).to_contain_text("Settings saved", timeout=ACTIVE_TIMEOUT)

        # The values persist on a fresh read, so the UI really wrote the tenant settings.
        page.goto(f"{BASE_URL}/t/default/admin/settings", wait_until="domcontentloaded")
        assert page.locator("input[name='Input.AssistanceReadOnlyHelperRoles']").input_value() == read_only_roles
        assert page.locator("select[name='Input.AssistanceRightsMode']").input_value() == rights_mode
    finally:
        page.close()


def _login_context(browser: Browser, username: str) -> BrowserContext:
    context = browser.new_context(base_url=BASE_URL, ignore_https_errors=True)
    page = context.new_page()
    page.goto(f"{BASE_URL}/t/default/login", wait_until="domcontentloaded")
    page.locator("input#Username").fill(username)
    page.locator("input#Password").fill(PASSWORD)
    page.locator("button[type='submit']").click()
    page.wait_for_url(
        lambda url: "/login" not in url.lower() and "/logintotp" not in url.lower(),
        timeout=ACTIVE_TIMEOUT,
        wait_until="domcontentloaded",
    )
    page.close()
    return context


def _create_request(
    requester_page: Page,
    *,
    helper_username: str,
    reason: str,
    mode: str = "ReadOnly",
    duration: str = "15",
) -> str:
    requester_page.goto(f"{BASE_URL}/t/default/account/assistance", wait_until="domcontentloaded")
    expect(requester_page.get_by_role("heading", name=re.compile("Assistance", re.I))).to_be_visible()

    select = requester_page.locator('[data-testid="assistance-helper-select"]')
    matching = select.locator("option").filter(has_text=helper_username)
    expect(matching).to_have_count(1, timeout=ACTIVE_TIMEOUT)
    helper_id = matching.first.get_attribute("value")
    assert helper_id
    select.select_option(helper_id)
    requester_page.locator('[data-testid="assistance-mode-select"]').select_option(mode)
    requester_page.locator('[data-testid="assistance-reason"]').fill(reason)
    requester_page.locator('[data-testid="assistance-duration"]').fill(duration)
    requester_page.locator('[data-testid="assistance-request-button"]').click()
    requester_page.wait_for_load_state("domcontentloaded")

    expect(requester_page.locator("body")).to_contain_text("Assistance request sent", timeout=ACTIVE_TIMEOUT)
    row = requester_page.locator('[data-testid="assistance-outgoing-row"]').filter(has_text=helper_username).first
    expect(row).to_be_visible(timeout=ACTIVE_TIMEOUT)
    expect(row).to_contain_text("Pending")
    request_input = row.locator("input[name='CancelRequestId']")
    expect(request_input).to_have_count(1)
    request_id = request_input.get_attribute("value")
    assert request_id
    return request_id


def _accept_in_app(helper_page: Page, *, requester_username: str, reason: str) -> None:
    helper_page.goto(f"{BASE_URL}/t/default/account/assistance", wait_until="domcontentloaded")
    row = helper_page.locator('[data-testid="assistance-incoming-row"]').filter(has_text=requester_username).first
    expect(row).to_be_visible(timeout=ACTIVE_TIMEOUT)
    expect(row).to_contain_text(reason)

    row.locator('[data-testid="assistance-review-link"]').click()
    helper_page.wait_for_load_state("domcontentloaded")
    expect(helper_page.locator('[data-testid="accept-assistance-button"]')).to_be_visible()
    expect(helper_page.locator("body")).to_contain_text(reason)
    helper_page.locator('[data-testid="accept-assistance-button"]').click()
    helper_page.wait_for_url(
        lambda url: "/account/assistance/accept" not in url and url.rstrip("/").endswith("/account"),
        timeout=ACTIVE_TIMEOUT,
        wait_until="domcontentloaded",
    )
    expect(helper_page.locator('[data-testid="user-assistance-banner"]')).to_be_visible(timeout=ACTIVE_TIMEOUT)
    expect(helper_page.locator('[data-testid="user-assistance-banner"]')).to_contain_text(
        requester_username, timeout=ACTIVE_TIMEOUT
    )


def _find_assistance_link(email: str, timeout_seconds: float = 25) -> str:
    pattern = re.compile(
        r"https?://[^\"'<>\s]+/account/assistance/accept\?[^\"'<>\s]+",
        re.IGNORECASE,
    )
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        response = requests.get(f"{MAILHOG_API}/messages?limit=80", timeout=5)
        for message in response.json().get("items", []):
            recipients = message.get("Content", {}).get("Headers", {}).get("To", [])
            if not any(email.lower() in recipient.lower() for recipient in recipients):
                continue
            body = message.get("Content", {}).get("Body", "")
            body = body.replace("=\r\n", "").replace("=\n", "").replace("=3D", "=")
            match = pattern.search(html.unescape(body))
            if match:
                return match.group(0).rstrip(".,)")
        time.sleep(1)
    raise AssertionError(f"The assistance email for {email} was not received")


def _end_from_helper(helper_page: Page) -> None:
    helper_page.locator('[data-testid="end-assistance-button"]').click()
    helper_page.wait_for_load_state("domcontentloaded")
    expect(helper_page.locator('[data-testid="user-assistance-banner"]')).to_have_count(0, timeout=ACTIVE_TIMEOUT)


def _assert_no_overlay(page: Page, username: str) -> None:
    page.goto(f"{BASE_URL}/t/default/account", wait_until="domcontentloaded")
    expect(page.locator('[data-testid="user-assistance-banner"]')).to_have_count(0, timeout=ACTIVE_TIMEOUT)
    expect(page.locator("body")).to_contain_text(username, timeout=ACTIVE_TIMEOUT)


class TestUserAssistance:
    def test_email_link_requires_matching_signed_in_helper_and_read_only_is_enforced(
        self,
        browser_session: Browser,
        authenticated_context: BrowserContext,
    ) -> None:
        suffix = uuid.uuid4().hex[:8]
        requester_name = _unique("e2e-assist-req")
        helper_name = _unique("e2e-assist-help")
        requester = _create_user(
            authenticated_context, requester_name, f"{requester_name}@e2e.local"
        )
        helper = _create_user(authenticated_context, helper_name, f"{helper_name}@e2e.local")
        role_name = _unique("e2e-assistance-helper")
        role_id = _ensure_role(authenticated_context, role_name)
        _assign_role(authenticated_context, str(helper.get("id", helper.get("Id"))), role_id)
        _configure_assistance(authenticated_context, read_only_roles=role_name)

        requester_context = _login_context(browser_session, requester_name)
        helper_context = _login_context(browser_session, helper_name)
        try:
            requester_page = requester_context.new_page()
            reason = f"Audit my profile {suffix}"
            request_id = _create_request(
                requester_page, helper_username=helper_name, reason=reason
            )

            # The email link alone is not a bearer grant: another signed-in identity cannot accept it.
            link = _find_assistance_link(f"{helper_name}@e2e.local")
            requester_page.goto(link, wait_until="domcontentloaded")
            expect(requester_page.locator("body")).to_contain_text(
                "addressed to a different signed-in user", timeout=ACTIVE_TIMEOUT
            )
            expect(requester_page.locator('[data-testid="accept-assistance-button"]')).to_have_count(0)

            # The invited identity can accept through the same link.
            helper_page = helper_context.new_page()
            helper_page.goto(link, wait_until="domcontentloaded")
            expect(helper_page.locator('[data-testid="accept-assistance-button"]')).to_be_visible(
                timeout=ACTIVE_TIMEOUT
            )
            expect(helper_page.locator("body")).to_contain_text(reason)
            helper_page.locator('[data-testid="accept-assistance-button"]').click()
            helper_page.wait_for_url(
                lambda url: "/account/assistance/accept" not in url and url.rstrip("/").endswith("/account"),
                timeout=ACTIVE_TIMEOUT,
                wait_until="domcontentloaded",
            )
            expect(helper_page.locator('[data-testid="user-assistance-banner"]')).to_be_visible(
                timeout=ACTIVE_TIMEOUT
            )
            expect(helper_page.locator('[data-testid="user-assistance-banner"]')).to_contain_text(
                requester_name, timeout=ACTIVE_TIMEOUT
            )
            # The overlay really presents the requester's account page.
            expect(helper_page.locator("body")).to_contain_text(requester_name, timeout=ACTIVE_TIMEOUT)

            # Read-only rejects unsafe methods before endpoint code runs.
            denied = helper_context.request.post(f"{BASE_URL}/t/default/account/profile")
            assert denied.status == 403, denied.text()
            assert "read-only" in denied.text().lower()

            # The requester sees the session and can always end it.
            requester_page.goto(f"{BASE_URL}/t/default/account", wait_until="domcontentloaded")
            expect(requester_page.locator('[data-testid="user-assistance-requester-banner"]')).to_be_visible(
                timeout=ACTIVE_TIMEOUT
            )
            expect(requester_page.locator('[data-testid="user-assistance-requester-banner"]')).to_contain_text(
                helper_name, timeout=ACTIVE_TIMEOUT
            )

            # The helper can end the session from the banner and returns to their own identity.
            _end_from_helper(helper_page)
            _assert_no_overlay(helper_page, helper_name)
            requester_page.goto(f"{BASE_URL}/t/default/account", wait_until="domcontentloaded")
            expect(requester_page.locator('[data-testid="user-assistance-requester-banner"]')).to_have_count(
                0, timeout=ACTIVE_TIMEOUT
            )

            # The manager history records the completed session.
            manager_page = authenticated_context.new_page()
            try:
                manager_page.goto(f"{BASE_URL}/t/default/admin/assistance", wait_until="domcontentloaded")
                row = manager_page.locator('[data-testid="admin-assistance-row"]').filter(
                    has_text=requester_name
                ).first
                expect(row).to_be_visible(timeout=ACTIVE_TIMEOUT)
                expect(row).to_contain_text("Ended")
                expect(row).to_contain_text(helper_name)
                expect(row).to_contain_text(reason)
            finally:
                manager_page.close()
        finally:
            requester_context.close()
            helper_context.close()

    def test_in_app_accept_requester_end_and_demo_claims(
        self,
        browser_session: Browser,
        authenticated_context: BrowserContext,
    ) -> None:
        suffix = uuid.uuid4().hex[:8]
        requester_name = _unique("e2e-assist-req")
        helper_name = _unique("e2e-assist-help")
        requester = _create_user(
            authenticated_context, requester_name, f"{requester_name}@e2e.local"
        )
        helper = _create_user(authenticated_context, helper_name, f"{helper_name}@e2e.local")
        requester_id = str(requester.get("id", requester.get("Id")))
        helper_id = str(helper.get("id", helper.get("Id")))
        role_name = _unique("e2e-assistance-helper")
        role_id = _ensure_role(authenticated_context, role_name)
        _assign_role(authenticated_context, requester_id, role_id)
        _assign_role(authenticated_context, helper_id, role_id)
        blazor_id = _blazor_client_id(authenticated_context)
        _assign_client(authenticated_context, requester_id, blazor_id)
        _assign_client(authenticated_context, helper_id, blazor_id)
        _configure_assistance(authenticated_context, read_only_roles=role_name)

        requester_context = _login_context(browser_session, requester_name)
        helper_context = _login_context(browser_session, helper_name)
        try:
            requester_page = requester_context.new_page()
            reason = f"Check my account {suffix}"
            request_id = _create_request(
                requester_page, helper_username=helper_name, reason=reason
            )

            helper_page = helper_context.new_page()
            _accept_in_app(helper_page, requester_username=requester_name, reason=reason)

            # RazorClient and OidcDemo expose the assistance metadata on the RP side.
            razor_page = helper_context.new_page()
            razor_page.goto(f"{RAZORCLIENT_URL}/Auth/Login", wait_until="domcontentloaded")
            razor_page.wait_for_url(
                lambda url: url.startswith(RAZORCLIENT_URL) and "/Auth/Login" not in url,
                timeout=ACTIVE_TIMEOUT,
                wait_until="domcontentloaded",
            )
            expect(razor_page.locator('[data-testid="razorclient-assistance-details"]')).to_be_visible(
                timeout=ACTIVE_TIMEOUT
            )
            details = razor_page.locator('[data-testid="razorclient-assistance-details"]')
            expect(details).to_contain_text("Requester subject")
            expect(details).to_contain_text(request_id)
            expect(details).to_contain_text("read_only")

            demo_page = helper_context.new_page()
            demo_page.goto(f"{OIDCDEMO_URL}/Account/Login", wait_until="domcontentloaded")
            demo_page.wait_for_url(
                lambda url: url.startswith(OIDCDEMO_URL) and "/Account/Login" not in url,
                timeout=ACTIVE_TIMEOUT,
                wait_until="domcontentloaded",
            )
            expect(demo_page.locator('[data-testid="oidcdemo-assistance-details"]')).to_be_visible(
                timeout=ACTIVE_TIMEOUT
            )
            expect(demo_page.locator('[data-testid="oidcdemo-assistance-details"]')).to_contain_text(
                request_id
            )

            # The requester can end immediately from the banner.
            requester_page.goto(f"{BASE_URL}/t/default/account", wait_until="domcontentloaded")
            expect(requester_page.locator('[data-testid="user-assistance-requester-banner"]')).to_be_visible(
                timeout=ACTIVE_TIMEOUT
            )
            requester_page.locator('[data-testid="end-requester-assistance-button"]').click()
            requester_page.wait_for_load_state("domcontentloaded")
            expect(requester_page.locator('[data-testid="user-assistance-requester-banner"]')).to_have_count(
                0, timeout=ACTIVE_TIMEOUT
            )

            _assert_no_overlay(helper_page, helper_name)
        finally:
            requester_context.close()
            helper_context.close()

    def test_manager_can_revoke_an_active_session(
        self,
        browser_session: Browser,
        authenticated_context: BrowserContext,
    ) -> None:
        suffix = uuid.uuid4().hex[:8]
        requester_name = _unique("e2e-assist-req")
        helper_name = _unique("e2e-assist-help")
        requester = _create_user(
            authenticated_context, requester_name, f"{requester_name}@e2e.local"
        )
        helper = _create_user(authenticated_context, helper_name, f"{helper_name}@e2e.local")
        role_name = _unique("e2e-assistance-helper")
        role_id = _ensure_role(authenticated_context, role_name)
        _assign_role(authenticated_context, str(helper.get("id", helper.get("Id"))), role_id)
        _configure_assistance(
            authenticated_context,
            read_only_roles=role_name,
            rights_mode="intersection",
            manager_roles="tenant-admin",
        )

        requester_context = _login_context(browser_session, requester_name)
        helper_context = _login_context(browser_session, helper_name)
        try:
            requester_page = requester_context.new_page()
            reason = f"Investigate billing {suffix}"
            _create_request(requester_page, helper_username=helper_name, reason=reason)

            helper_page = helper_context.new_page()
            _accept_in_app(helper_page, requester_username=requester_name, reason=reason)

            manager_page = authenticated_context.new_page()
            try:
                manager_page.goto(f"{BASE_URL}/t/default/admin/assistance", wait_until="domcontentloaded")
                row = manager_page.locator('[data-testid="admin-assistance-row"]').filter(
                    has_text=requester_name
                ).first
                expect(row).to_be_visible(timeout=ACTIVE_TIMEOUT)
                expect(row).to_contain_text("Active")
                row.locator("input[name='Reason']").fill("E2E manager revocation")
                row.locator('[data-testid="admin-assistance-revoke-button"]').click()
                manager_page.wait_for_load_state("domcontentloaded")
                expect(manager_page.locator("body")).to_contain_text(
                    "Assistance session revoked", timeout=ACTIVE_TIMEOUT
                )
                expect(row).to_contain_text("Revoked", timeout=ACTIVE_TIMEOUT)
            finally:
                manager_page.close()

            _assert_no_overlay(helper_page, helper_name)
            requester_page.goto(f"{BASE_URL}/t/default/account", wait_until="domcontentloaded")
            expect(requester_page.locator('[data-testid="user-assistance-requester-banner"]')).to_have_count(
                0, timeout=ACTIVE_TIMEOUT
            )
        finally:
            requester_context.close()
            helper_context.close()

    def test_read_write_session_allows_writes_and_revokes_on_role_loss(
        self,
        browser_session: Browser,
        authenticated_context: BrowserContext,
    ) -> None:
        suffix = uuid.uuid4().hex[:8]
        requester_name = _unique("e2e-assist-req")
        helper_name = _unique("e2e-assist-help")
        requester = _create_user(
            authenticated_context, requester_name, f"{requester_name}@e2e.local"
        )
        helper = _create_user(authenticated_context, helper_name, f"{helper_name}@e2e.local")
        helper_id = str(helper.get("id", helper.get("Id")))
        role_name = _unique("e2e-assistance-writer")
        role_id = _ensure_role(authenticated_context, role_name)
        _assign_role(authenticated_context, helper_id, role_id)
        _configure_assistance(
            authenticated_context,
            read_only_roles=role_name,
            read_write_roles=role_name,
            rights_mode="fullTarget",
        )

        requester_context = _login_context(browser_session, requester_name)
        helper_context = _login_context(browser_session, helper_name)
        try:
            requester_page = requester_context.new_page()
            reason = f"Update my display name {suffix}"
            _create_request(
                requester_page,
                helper_username=helper_name,
                reason=reason,
                mode="ReadWrite",
            )

            helper_page = helper_context.new_page()
            _accept_in_app(helper_page, requester_username=requester_name, reason=reason)

            # A read-write overlay can submit a real form as the requester.
            helper_page.goto(f"{BASE_URL}/t/default/account/profile", wait_until="domcontentloaded")
            expect(helper_page.locator("body")).to_contain_text(requester_name)
            updated_name = f"Assisted {suffix}"
            helper_page.locator("input[name='Input.Name']").fill(updated_name)
            helper_page.get_by_role("button", name="Save Changes").click()
            helper_page.wait_for_load_state("domcontentloaded")

            helper_page.goto(f"{BASE_URL}/t/default/account/profile", wait_until="domcontentloaded")
            assert helper_page.locator("input[name='Input.Name']").input_value() == updated_name

            requester_page.goto(f"{BASE_URL}/t/default/account", wait_until="domcontentloaded")
            expect(requester_page.locator("body")).to_contain_text(updated_name, timeout=ACTIVE_TIMEOUT)

            # Losing the helper role fails closed the next time the overlay is resolved.
            delete_url = f"{BASE_URL}/admin/api/users/{helper_id}/roles/{role_id}"
            deleted = authenticated_context.request.delete(
                delete_url,
                headers=csrf_headers(authenticated_context.request, delete_url),
            )
            assert deleted.status in {200, 204}, deleted.text()

            _assert_no_overlay(helper_page, helper_name)

            requester_page.goto(f"{BASE_URL}/t/default/account", wait_until="domcontentloaded")
            expect(requester_page.locator('[data-testid="user-assistance-requester-banner"]')).to_have_count(
                0, timeout=ACTIVE_TIMEOUT
            )
        finally:
            requester_context.close()
            helper_context.close()
