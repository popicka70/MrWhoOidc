"""End-to-end coverage for real tenant-user impersonation.

The session is started and stopped through the platform-admin UI. A dedicated browser context is used for
every test so the shared admin session and the example-app cookies cannot leak into other tests.
"""

from __future__ import annotations

import json
import os
import re
from typing import Generator

import pytest
from playwright.sync_api import Browser, BrowserContext, Page, expect

from conftest import ADMIN_PASSWORD, ADMIN_USERNAME, BASE_URL

TARGET_USERNAME = "alice"
TARGET_NAME = "Alice Adams"
TARGET_PASSWORD = "P@ssw0rd!"
TARGET_EMAIL = "alice@example.com"

START_URL = f"{BASE_URL}/platform-admin/user-impersonation"
HISTORY_URL = f"{BASE_URL}/platform-admin/user-impersonation/history"
STOP_URL = f"{BASE_URL}/impersonation/stop"

RAZORCLIENT_URL = os.getenv("EXAMPLE_RAZORCLIENT_URL", "https://localhost:5003")
OIDCDEMO_URL = os.getenv("EXAMPLE_OIDCDEMO_URL", "https://localhost:5001")


def _login(page: Page, username: str, password: str) -> None:
    page.goto(f"{BASE_URL}/login", wait_until="domcontentloaded")
    page.locator("input#Username").fill(username)
    page.locator("input#Password").fill(password)
    page.locator("button[type='submit']").click()
    page.wait_for_url(
        lambda url: "/login" not in url and "/LoginTotp" not in url,
        timeout=45_000,
        wait_until="domcontentloaded",
    )


@pytest.fixture
def admin_context(browser_session: Browser) -> Generator[BrowserContext, None, None]:
    """A fresh admin browser context, isolated from the shared suite session."""
    context = browser_session.new_context(
        base_url=BASE_URL,
        viewport={"width": 1600, "height": 1000},
        ignore_https_errors=True,
    )
    page = context.new_page()
    try:
        _login(page, ADMIN_USERNAME, ADMIN_PASSWORD)
    finally:
        page.close()
    yield context
    context.close()


def _admin_page(context: BrowserContext) -> Page:
    page = context.new_page()
    page.goto(f"{START_URL}?q={TARGET_USERNAME}", wait_until="domcontentloaded")
    return page


def _start_impersonation(
    page: Page,
    *,
    reason: str,
    ticket: str | None = None,
) -> None:
    page.goto(f"{START_URL}?q={TARGET_USERNAME}", wait_until="domcontentloaded")
    expect(page.get_by_role("heading", name=re.compile("User Impersonation", re.I))).to_be_visible()

    rows = page.locator('[data-testid="impersonation-user-row"]')
    expect(rows.first).to_be_visible()
    target_row = rows.filter(has_text=TARGET_USERNAME).first
    expect(target_row).to_contain_text(TARGET_USERNAME)
    target_row.locator('[data-testid="impersonation-select-user"]').click()
    expect(page.locator("#selected-user")).to_contain_text(TARGET_USERNAME)

    page.locator("#reason").fill(reason)
    if ticket is not None:
        page.locator("#ticketReference").fill(ticket)
    page.locator("#expiryMinutes").fill("15")
    page.locator('[data-testid="impersonation-start-button"]').click()

    page.wait_for_url(
        lambda url: "/t/default/account" in url,
        timeout=45_000,
        wait_until="domcontentloaded",
    )
    expect(page.locator('[data-testid="user-impersonation-banner"]')).to_be_visible()


def _stop_impersonation(page: Page) -> None:
    """Stops any active impersonation, using the control surface rather than the overlaid account page."""
    if "/platform-admin" not in page.url:
        page.goto(START_URL, wait_until="domcontentloaded")
    card_button = page.locator('[data-testid="end-impersonation-card-button"]')
    if card_button.count() > 0 and card_button.first.is_visible():
        card_button.first.click()
        page.wait_for_load_state("domcontentloaded")
        return

    banner_button = page.locator('[data-testid="end-impersonation-button"]')
    if banner_button.count() > 0 and banner_button.first.is_visible():
        banner_button.first.click()
        page.wait_for_load_state("domcontentloaded")


def _claim_value(page: Page, claim: str) -> str | None:
    rows = page.locator("table tbody tr")
    for index in range(rows.count()):
        cells = rows.nth(index).locator("td")
        if cells.count() >= 2 and cells.nth(0).inner_text().strip() == claim:
            return cells.nth(1).inner_text().strip()
    return None


def _token_value(page: Page, token_name: str) -> str | None:
    rows = page.locator("table tbody tr")
    for index in range(rows.count()):
        cells = rows.nth(index).locator("td")
        if cells.count() < 2 or cells.nth(0).inner_text().strip() != token_name:
            continue
        masked = cells.nth(1).locator("code[data-token-value]")
        if masked.count() > 0:
            return masked.first.get_attribute("data-token-value")
        return cells.nth(1).inner_text().strip()
    return None


def _impersonation_claim_context(page: Page) -> tuple[str, dict[str, str]]:
    """Returns (target subject, parsed act claim) for an application's claims table."""
    subject = _claim_value(page, "sub")
    if subject is None:
        subject = _claim_value(page, "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")
    assert subject, "Target sub claim was not rendered by the application"

    raw_act = _claim_value(page, "act")
    assert raw_act, "act claim was not rendered by the application"
    actor = json.loads(raw_act)
    assert actor.get("sub"), f"act claim has no sub: {raw_act!r}"
    assert actor["sub"] != subject, "Target subject and actor subject must differ"

    impersonation_id = _claim_value(page, "impersonation_id")
    assert impersonation_id, "impersonation_id claim was not rendered by the application"

    name = _claim_value(page, "name")
    assert name == TARGET_NAME, f"Expected target name {TARGET_NAME!r}, got {name!r}"

    return subject, actor


class TestUserImpersonation:
    def test_start_search_validation_and_account_identity(self, admin_context: BrowserContext):
        page = _admin_page(admin_context)
        try:
            page.goto(START_URL, wait_until="domcontentloaded")
            expect(page.get_by_role("heading", name=re.compile("User Impersonation", re.I))).to_be_visible()
            expect(page.locator("body")).to_contain_text("You will act as the selected user")
            expect(page.locator("#tenantId")).to_be_visible()
            expect(page.locator("#user-search")).to_be_visible()

            page.locator("#user-search").fill(TARGET_USERNAME)
            page.get_by_role("button", name=re.compile("Search", re.I)).click()
            page.wait_for_load_state("domcontentloaded")

            rows = page.locator('[data-testid="impersonation-user-row"]')
            expect(rows.first).to_be_visible()
            target_row = rows.filter(has_text=TARGET_USERNAME).first
            expect(target_row).to_contain_text(TARGET_EMAIL)
            target_row.locator('[data-testid="impersonation-select-user"]').click()
            expect(page.locator("#selected-user")).to_contain_text(TARGET_USERNAME)

            # A blank reason is rejected before the form leaves the page.
            page.locator('[data-testid="impersonation-start-button"]').click()
            page.wait_for_timeout(500)
            assert "/t/default/account" not in page.url, "Blank reason should not start impersonation"
            assert "start" not in page.url.lower() or "user-impersonation" in page.url

            page.locator("#reason").fill("E2E start validation")
            page.locator('[data-testid="impersonation-start-button"]').click()
            page.wait_for_url(
                lambda url: "/t/default/account" in url,
                timeout=45_000,
                wait_until="domcontentloaded",
            )

            banner = page.locator('[data-testid="user-impersonation-banner"]')
            expect(banner).to_be_visible()
            banner_text = banner.inner_text()
            assert TARGET_NAME in banner_text
            assert TARGET_USERNAME in banner_text
            assert "admin" in banner_text.lower()
            assert "Default Tenant" in banner_text
            assert "Expires in" in banner_text
            assert "E2E start validation" in banner_text

            expect(page.locator("body")).to_contain_text(TARGET_EMAIL)
            expect(page.locator("body")).not_to_contain_text(ADMIN_USERNAME)
        finally:
            try:
                _stop_impersonation(page)
            finally:
                page.close()

    def test_stop_restores_admin_and_history_records_lifecycle(self, admin_context: BrowserContext):
        reason = "E2E stop and history"
        ticket = "E2E-IMP-STOP-001"
        page = _admin_page(admin_context)
        try:
            _start_impersonation(page, reason=reason, ticket=ticket)
            expect(page.locator("body")).to_contain_text(ticket)

            page.locator('[data-testid="end-impersonation-button"]').click()
            page.wait_for_load_state("domcontentloaded")

            expect(page).to_have_url(re.compile(r"/platform-admin/user-impersonation$"))
            expect(page.locator('[data-testid="user-impersonation-banner"]')).to_have_count(0)
            expect(page.locator('[data-testid="active-impersonation-card"]')).to_have_count(0)
            expect(page.locator(".alert-success")).to_contain_text("Impersonation ended")

            page.goto(f"{BASE_URL}/t/default/account", wait_until="domcontentloaded")
            expect(page.locator("body")).to_contain_text(ADMIN_USERNAME)
            expect(page.locator("body")).not_to_contain_text(TARGET_EMAIL)

            page.goto(HISTORY_URL, wait_until="domcontentloaded")
            table = page.locator('[data-testid="impersonation-history-table"]')
            expect(table).to_be_visible()
            row = table.locator("tbody tr").filter(has_text=ticket).first
            expect(row).to_be_visible()
            expect(row).to_contain_text(reason)
            expect(row).to_contain_text(TARGET_USERNAME)
            expect(row).to_contain_text("default")
            expect(row).to_contain_text("Ended")

            # Legacy routes stay as permanent redirects to the real user-level pages.
            page.goto(f"{BASE_URL}/platform-admin/impersonation", wait_until="domcontentloaded")
            assert "/platform-admin/user-impersonation" in page.url
            page.goto(f"{BASE_URL}/platform-admin/impersonation-history", wait_until="domcontentloaded")
            assert "/platform-admin/user-impersonation/history" in page.url
        finally:
            try:
                _stop_impersonation(page)
            finally:
                page.close()

    def test_demo_apps_receive_target_identity_and_actor_claims(self, admin_context: BrowserContext):
        reason = "E2E demo identity"
        ticket = "E2E-IMP-DEMO-001"
        admin_page = _admin_page(admin_context)
        razor_page = admin_context.new_page()
        demo_page = admin_context.new_page()
        refresh_token: str | None = None
        try:
            _start_impersonation(admin_page, reason=reason, ticket=ticket)

            # RazorClient SSO: the admin's OP cookie is presented, the overlay yields Alice's identity.
            razor_page.goto(f"{RAZORCLIENT_URL}/Auth/Login", wait_until="domcontentloaded")
            razor_page.wait_for_url(
                lambda url: url.startswith(RAZORCLIENT_URL) and "/Auth/Login" not in url,
                timeout=45_000,
                wait_until="domcontentloaded",
            )
            expect(razor_page.locator('[data-testid="razorclient-impersonation-banner"]')).to_be_visible()
            expect(razor_page.locator('[data-testid="razorclient-impersonation-details"]')).to_be_visible()

            razor_subject, razor_actor = _impersonation_claim_context(razor_page)
            razor_details = razor_page.locator('[data-testid="razorclient-impersonation-details"]')
            expect(razor_details).to_contain_text(razor_subject)
            expect(razor_details).to_contain_text(razor_actor["sub"])
            expect(razor_details).to_contain_text(_claim_value(razor_page, "impersonation_id") or "—")
            refresh_token = _token_value(razor_page, "refresh_token")

            # OidcDemo gets the same overlaid claims through its own OIDC middleware session.
            demo_page.goto(f"{OIDCDEMO_URL}/Account/Login", wait_until="domcontentloaded")
            demo_page.wait_for_url(
                lambda url: url.startswith(OIDCDEMO_URL) and "/Account/Login" not in url,
                timeout=45_000,
                wait_until="domcontentloaded",
            )
            expect(demo_page.locator('[data-testid="oidcdemo-impersonation-banner"]')).to_be_visible()
            expect(demo_page.locator('[data-testid="oidcdemo-impersonation-details"]')).to_be_visible()

            demo_subject, demo_actor = _impersonation_claim_context(demo_page)
            demo_details = demo_page.locator('[data-testid="oidcdemo-impersonation-details"]')
            expect(demo_details).to_contain_text(demo_subject)
            expect(demo_details).to_contain_text(demo_actor["sub"])

            # Ending impersonation must revoke the target OP session and refresh tokens issued to the app.
            _stop_impersonation(admin_page)
            expect(admin_page.locator('[data-testid="user-impersonation-banner"]')).to_have_count(0)

            client_secret = os.getenv("DEV_SEED_BLAZOR_WEB_CLIENT_SECRET")
            assert refresh_token, "RazorClient did not expose a refresh token to verify revocation"
            assert client_secret, "DEV_SEED_BLAZOR_WEB_CLIENT_SECRET is required for the revocation check"
            response = razor_page.request.post(
                f"{BASE_URL}/t/default/token",
                form={
                    "grant_type": "refresh_token",
                    "refresh_token": refresh_token,
                    "client_id": "blazor-web",
                    "client_secret": client_secret,
                },
            )
            assert response.status == 400, (
                f"Refresh token from the impersonated session was still accepted: "
                f"{response.status} {response.text()}"
            )
            assert "invalid_grant" in response.text().lower(), response.text()
        finally:
            try:
                _stop_impersonation(admin_page)
            finally:
                razor_page.close()
                demo_page.close()
                admin_page.close()

    def test_non_platform_admin_cannot_open_or_post_to_impersonation(self, browser_session: Browser):
        context = browser_session.new_context(
            base_url=BASE_URL,
            viewport={"width": 1600, "height": 1000},
            ignore_https_errors=True,
        )
        page = context.new_page()
        try:
            _login(page, TARGET_EMAIL, TARGET_PASSWORD)

            page.goto(START_URL, wait_until="domcontentloaded")
            assert "/account/access-denied" in page.url, (
                f"Non-platform-admin reached the impersonation page: {page.url}"
            )

            response = page.request.post(
                STOP_URL,
                form={"reason": "E2E non-admin"},
                max_redirects=0,
            )
            assert response.status in {302, 403}, (
                f"Expected a non-platform-admin denial, got {response.status}: {response.text()}"
            )
            if response.status == 302:
                assert "access-denied" in (response.headers.get("location") or "").lower()
        finally:
            page.close()
            context.close()
