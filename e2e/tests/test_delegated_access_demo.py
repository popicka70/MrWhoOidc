"""Focused client-bound Delegated Access lifecycle through the RazorClient demo."""

from __future__ import annotations

import re
import time

import requests
from playwright.sync_api import Browser, BrowserContext, Page, expect

from .oidc_helpers import BASE_URL, RUN_SUFFIX

MAILHOG_API = "http://localhost:8025/api/v2"
RAZORCLIENT_URL = "https://localhost:5003"
PASSWORD = "Delegated-E2E-123!"


def _create_user(admin_context: BrowserContext, username: str, email: str) -> dict:
    response = admin_context.request.post(
        f"{BASE_URL}/admin/api/users",
        data={
            "username": username,
            "email": email,
            "name": username,
            "password": PASSWORD,
        },
    )
    assert response.status == 201, response.text()
    return response.json()


def _assign_client(admin_context: BrowserContext, user_id: str, client_internal_id: str) -> None:
    response = admin_context.request.post(
        f"{BASE_URL}/admin/api/users/{user_id}/clients",
        data={"clientId": client_internal_id},
    )
    assert response.status in {200, 201, 409}, response.text()


def _login_context(browser: Browser, username: str) -> BrowserContext:
    context = browser.new_context(base_url=BASE_URL, ignore_https_errors=True)
    page = context.new_page()
    page.goto(f"{BASE_URL}/t/default/login", wait_until="domcontentloaded")
    page.locator("input#Username").fill(username)
    page.locator("input#Password").fill(PASSWORD)
    page.locator("button[type='submit']").click()
    page.wait_for_url(lambda url: "/login" not in url.lower(), timeout=30_000)
    page.close()
    return context


def _find_invitation(email: str, timeout_seconds: float = 20) -> str:
    pattern = re.compile(r"/account/delegated-access/invitations/[A-Za-z0-9_%-]+")
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        response = requests.get(f"{MAILHOG_API}/messages?limit=50", timeout=5)
        for message in response.json().get("items", []):
            recipients = message.get("Content", {}).get("Headers", {}).get("To", [])
            if not any(email.lower() in recipient.lower() for recipient in recipients):
                continue
            body = message.get("Content", {}).get("Body", "")
            body = body.replace("=\r\n", "").replace("=\n", "").replace("=3D", "=")
            match = pattern.search(body)
            if match:
                return match.group(0)
        time.sleep(1)
    raise AssertionError(f"Delegated access invitation for {email} was not received")


def _login_razorclient(page: Page, username: str) -> None:
    page.goto(RAZORCLIENT_URL, wait_until="domcontentloaded")
    page.get_by_role("link", name="Standard sign-in").click()
    page.wait_for_url(lambda url: url.startswith(BASE_URL), timeout=30_000)
    local_login = page.locator("#btn-local-login")
    if local_login.count() > 0:
        local_login.click()
    page.wait_for_url(lambda url: "/login" in url.lower(), timeout=30_000)
    page.locator("input#Username").fill(username)
    page.locator("input#Password").fill(PASSWORD)
    page.locator("button[type='submit']").click()
    page.wait_for_url(lambda url: url.startswith(RAZORCLIENT_URL), timeout=30_000)


class TestClientBoundDelegatedAccessDemo:
    def test_delegated_exchange_is_bound_to_razorclient(
        self,
        browser_session: Browser,
        authenticated_context: BrowserContext,
    ) -> None:
        suffix = RUN_SUFFIX.lower()
        delegator_username = f"e2e-delegator-{suffix}"
        delegate_username = f"e2e-delegate-{suffix}"
        delegator_email = f"{delegator_username}@e2e.local"
        delegate_email = f"{delegate_username}@e2e.local"

        delegator = _create_user(authenticated_context, delegator_username, delegator_email)
        delegate = _create_user(authenticated_context, delegate_username, delegate_email)
        delegate_id = delegate.get("id") or delegate.get("Id")
        delegator_id = delegator.get("id") or delegator.get("Id")
        assert delegate_id
        assert delegator_id

        clients_response = authenticated_context.request.get(f"{BASE_URL}/admin/api/clients")
        assert clients_response.status == 200, clients_response.text()
        clients_payload = clients_response.json()
        clients = clients_payload.get("items", clients_payload) if isinstance(clients_payload, dict) else clients_payload
        blazor_client = next(
            client for client in clients
            if client.get("clientId", client.get("ClientId")) == "blazor-web"
        )
        blazor_internal_id = str(blazor_client.get("id", blazor_client.get("Id")))
        _assign_client(authenticated_context, str(delegator_id), blazor_internal_id)
        _assign_client(authenticated_context, str(delegate_id), blazor_internal_id)

        delegator_context = _login_context(browser_session, delegator_username)
        delegate_context = _login_context(browser_session, delegate_username)
        try:
            delegator_page = delegator_context.new_page()
            delegator_page.goto(
                f"{BASE_URL}/t/default/account/delegated-access/create",
                wait_until="domcontentloaded",
            )
            expect(delegator_page.get_by_role("heading", name="Create Delegated Access Grant")).to_be_visible()
            client_option = delegator_page.locator("select[name='clientId'] option").filter(
                has_text="blazor-web"
            ).first
            client_value = client_option.get_attribute("value")
            assert client_value
            delegator_page.locator("select[name='clientId']").select_option(client_value)
            delegator_page.locator("select[name='delegateId']").select_option(str(delegate_id))
            delegator_page.locator("input[value='profile.read']").check()
            delegator_page.locator("input[name='purpose']").fill("RazorClient delegated profile demo")
            delegator_page.locator("input[name='expiryDays']").fill("1")
            delegator_page.get_by_role("button", name="Create grant").click()
            delegator_page.wait_for_load_state("domcontentloaded")
            expect(delegator_page.locator("body")).to_contain_text("Grant created successfully")

            invitation_path = _find_invitation(delegate_email)
            delegate_page = delegate_context.new_page()
            delegate_page.goto(f"{BASE_URL}/t/default{invitation_path}", wait_until="domcontentloaded")
            expect(delegate_page.locator("body")).to_contain_text("Blazor Web Frontend")
            expect(delegate_page.locator("body")).to_contain_text("blazor-web")
            delegate_page.get_by_role("button", name="Accept grant").click()
            delegate_page.wait_for_load_state("domcontentloaded")

            delegate_page.goto(
                f"{BASE_URL}/t/default/account/delegated-access/delegated-to-me",
                wait_until="domcontentloaded",
            )
            grant_item = delegate_page.locator(".list-group-item").filter(
                has_text="RazorClient delegated profile demo"
            ).first
            expect(grant_item).to_be_visible()
            activate_href = grant_item.get_by_role("link", name="Activate").get_attribute("href")
            assert activate_href
            grant_match = re.search(r"/([0-9a-f-]{36})/activate", activate_href, re.I)
            assert grant_match, activate_href
            grant_id = grant_match.group(1)

            razor_context = browser_session.new_context(ignore_https_errors=True)
            try:
                razor_page = razor_context.new_page()
                _login_razorclient(razor_page, delegate_username)
                razor_page.goto(f"{RAZORCLIENT_URL}/Delegated", wait_until="domcontentloaded")
                razor_page.locator("input[name='DelegationId']").fill(grant_id)
                razor_page.get_by_role("button", name="Exchange and call API").click()
                razor_page.wait_for_load_state("domcontentloaded")

                expect(razor_page.locator("#delegator-subject")).to_contain_text(
                    str(delegator_id)
                )
                expect(razor_page.locator("#delegate-actor")).to_contain_text(str(delegate_id))
                expect(razor_page.locator("#delegation-id")).to_contain_text(grant_id)
                expect(razor_page.locator("#authorized-client")).to_contain_text("blazor-web")
                expect(razor_page.get_by_role("alert")).to_have_count(0)
            finally:
                razor_context.close()
        finally:
            delegator_context.close()
            delegate_context.close()
