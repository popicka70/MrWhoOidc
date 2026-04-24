"""
Real portal-to-license end-to-end flow.

This test covers the browser path from the integrated customer portal through
the licensing backoffice and back into the WebAuth platform license UI.
"""

from __future__ import annotations

import os
import time

import pytest
from playwright.sync_api import Browser, BrowserContext, Locator, Page, expect

BASE_URL: str = os.getenv("BASE_URL", "https://localhost:8443")
PORTAL_BASE_URL: str = os.getenv("PORTAL_BASE_URL", "http://localhost:8088")
LICENSING_ADMIN_URL: str = os.getenv("LICENSING_ADMIN_URL", "https://localhost:7443")
ADMIN_USERNAME: str = os.getenv("ADMIN_USERNAME", "admin@mrwho.local")
ADMIN_PASSWORD: str = os.getenv("ADMIN_PASSWORD", "Admin123!")


def _complete_login_if_prompted(
    page: Page,
    username_value: str = ADMIN_USERNAME,
    password_value: str = ADMIN_PASSWORD,
) -> None:
    username = page.locator("input#Username")
    password = page.locator("input#Password")

    for _ in range(40):
        if username.count() > 0 and password.count() > 0 and username.is_visible() and password.is_visible():
            break
        page.wait_for_timeout(250)
    else:
        return

    username.fill(username_value)
    password.fill(password_value)
    page.locator("form").first.evaluate("form => form.requestSubmit()")


def _sign_in_to_portal(page: Page, username_value: str, password_value: str) -> None:
    page.goto(f"{PORTAL_BASE_URL}/portal.html", wait_until="domcontentloaded")
    if page.locator("#login-button").is_visible():
        page.locator("#login-button").click()
    _complete_login_if_prompted(page, username_value, password_value)

    portal_base_lower = PORTAL_BASE_URL.lower()
    for _ in range(60):
        current_url = page.url.lower()
        if current_url.startswith(portal_base_lower) and "/portal.html" in current_url and "callback" not in current_url:
            signed_in_card = page.locator("#signed-in-card")
            if signed_in_card.count() > 0 and signed_in_card.is_visible():
                return

        page.wait_for_timeout(500)

    raise AssertionError(f"Portal sign-in did not complete. Final URL: {page.url}")


def _create_portal_user(page: Page, username_value: str, email_value: str) -> str:
    page.goto(f"{BASE_URL}/admin/users/add", wait_until="domcontentloaded")
    expect(page.locator("input#Input_Username, input[name='Input.Username']").first).to_be_visible(timeout=30_000)
    page.locator("input#Input_Username, input[name='Input.Username']").first.fill(username_value)
    page.locator("input#Input_Email, input[name='Input.Email']").first.fill(email_value)
    page.locator("input#Input_Name, input[name='Input.Name']").first.fill("Portal E2E User")
    page.locator("button[type='submit']").first.click()
    page.wait_for_load_state("domcontentloaded")
    assert "/admin/users/add" not in page.url or email_value in page.content() or username_value in page.content(), (
        f"User creation was not confirmed for {email_value}."
    )

    page.goto(f"{BASE_URL}/admin/users", wait_until="domcontentloaded")
    user_row = page.locator(f"tr:has-text('{email_value}'), tr:has-text('{username_value}')").first
    expect(user_row).to_be_visible(timeout=30_000)

    edit_link = user_row.get_by_role("link", name="Edit").first
    user_edit_href = edit_link.get_attribute("href")
    assert user_edit_href, f"Could not determine user edit link for {email_value}."

    user_id = user_edit_href.rstrip("/").split("/")[-1]
    page.goto(f"{BASE_URL}/admin/users/clients/{user_id}", wait_until="domcontentloaded")

    assigned_clients = page.locator("#assignedClientsList")
    if "portal-web" not in (assigned_clients.text_content() or ""):
        portal_client = page.locator("#availableClientsList .client-item").filter(has_text="portal-web").first
        expect(portal_client).to_be_visible(timeout=30_000)
        portal_client.locator("button[title='Assign client']").click()
        page.wait_for_load_state("domcontentloaded")

    expect(page.locator("#assignedClientsList")).to_contain_text("portal-web", timeout=30_000)

    page.goto(f"{BASE_URL}/admin/users", wait_until="domcontentloaded")
    user_row = page.locator(f"tr:has-text('{email_value}'), tr:has-text('{username_value}')").first
    expect(user_row).to_be_visible(timeout=30_000)
    user_row.get_by_role("button", name="Reset").click()
    expect(page.locator("#resetPasswordModal")).to_be_visible(timeout=30_000)
    page.locator("#confirmResetBtn").click()
    page.wait_for_load_state("domcontentloaded")

    reset_alert = page.locator(".alert-success").filter(has_text="Temporary password:").first
    expect(reset_alert).to_be_visible(timeout=30_000)
    temporary_password = reset_alert.locator("code.user-select-all").first.text_content()
    assert temporary_password, f"Password reset did not expose a temporary password for {email_value}."
    return temporary_password.strip()


def _sign_in_to_licensing_admin(page: Page) -> None:
    page.goto(f"{LICENSING_ADMIN_URL}/admin/signin", wait_until="domcontentloaded")

    sign_in_button = page.get_by_role("button", name="Sign in with MrWho OIDC")
    if sign_in_button.count() > 0 and sign_in_button.is_visible():
        sign_in_button.click()

    _complete_login_if_prompted(page)
    page.wait_for_url(lambda url: "/admin" in url.lower() and "/signin" not in url.lower(), timeout=30_000)
    expect(page.get_by_role("heading", name="Organization overview")).to_be_visible(timeout=30_000)


def _open_organization(page: Page, organization_name: str) -> None:
    organization_card = page.locator("article").filter(has_text=organization_name).first
    expect(organization_card).to_be_visible(timeout=30_000)
    organization_card.get_by_role("link", name="View organization").click()
    page.wait_for_url(lambda url: "/admin/organizations/" in url.lower(), timeout=30_000)
    expect(page.get_by_role("heading", name=organization_name)).to_be_visible(timeout=30_000)


def _reload_until_portal_download_ready(page: Page) -> None:
    for _ in range(12):
        page.goto(f"{PORTAL_BASE_URL}/portal.html", wait_until="domcontentloaded")
        if page.locator("[data-download-license-id]").count() > 0:
            expect(page.locator("[data-download-license-id]").first).to_be_visible(timeout=5_000)
            return
        page.wait_for_timeout(1_000)

    raise AssertionError("Portal did not show a downloadable license after issuance.")


def _section_by_heading(page: Page, heading_name: str) -> Locator:
    return page.locator("section").filter(
        has=page.get_by_role("heading", name=heading_name, level=2)
    ).first


def _reconcile_first_payment(page: Page) -> None:
    payments_section = _section_by_heading(page, "Payments")

    mark_received_button = payments_section.get_by_role("button", name="Mark received").first
    if mark_received_button.count() > 0 and mark_received_button.is_visible():
        mark_received_button.click()
        page.wait_for_load_state("domcontentloaded")
        payments_section = _section_by_heading(page, "Payments")

    reconcile_button = payments_section.get_by_role("button", name="Reconcile").first
    if reconcile_button.count() == 0 or not reconcile_button.is_visible():
        button_labels = payments_section.locator("button").all_text_contents()
        section_text = (payments_section.text_content() or "").strip()
        raise AssertionError(
            "Expected a Reconcile button in the Payments section. "
            f"Buttons: {button_labels}. Section: {section_text}"
        )

    reconcile_button.click()
    page.wait_for_load_state("domcontentloaded")


class TestPortalLicensingFlow:
    def test_portal_request_issue_download_and_install(
        self,
        browser_session: Browser,
        authenticated_context: BrowserContext,
    ) -> None:
        suffix = str(int(time.time()))
        portal_username = f"portal-e2e-{suffix}"
        portal_user_email = f"{portal_username}@test.local"
        organization_name = f"Portal E2E {suffix}"
        external_reference = f"portal-e2e-{suffix}"
        request_reason = f"portal-e2e-request-{suffix}"

        portal_context = browser_session.new_context(
            base_url=PORTAL_BASE_URL,
            viewport={"width": 1600, "height": 1200},
            accept_downloads=True,
            ignore_https_errors=True,
        )

        try:
            admin_setup_page = authenticated_context.new_page()
            portal_user_password = _create_portal_user(admin_setup_page, portal_username, portal_user_email)

            portal_page = portal_context.new_page()
            _sign_in_to_portal(portal_page, portal_user_email, portal_user_password)

            if portal_page.locator("#onboarding-form").is_visible():
                portal_page.locator("#organizationName").fill(organization_name)
                portal_page.locator("#billingEmail").fill(portal_user_email)
                portal_page.locator("#externalReference").fill(external_reference)
                with portal_page.expect_response(
                    lambda response: response.url.endswith("/api/portal/onboarding")
                    and response.request.method == "POST",
                    timeout=30_000,
                ) as onboarding_response_info:
                    portal_page.get_by_role("button", name="Create Organization").click()

                onboarding_response = onboarding_response_info.value
                assert onboarding_response.ok, onboarding_response.text()
                portal_page.wait_for_function(
                    "!document.getElementById('dashboard-card').classList.contains('d-none')",
                    timeout=30_000,
                )
                expect(portal_page.locator("#org-name")).to_have_text(organization_name, timeout=30_000)
            else:
                organization_name = portal_page.locator("#org-name").text_content() or organization_name

            expect(portal_page.locator("#dashboard-card")).to_be_visible(timeout=30_000)
            expect(portal_page.locator("#org-name")).to_have_text(organization_name, timeout=30_000)

            portal_page.locator("#productKey").select_option("mrwhooidc")
            portal_page.wait_for_function(
                "document.getElementById('planKey').options.length > 0 && Array.from(document.getElementById('planKey').options).some(option => option.value === 'oidc-enterprise')"
            )
            portal_page.locator("#planKey").select_option("oidc-enterprise")
            portal_page.locator("#changeType").select_option("NewLicense")
            portal_page.locator("#requestReason").fill(request_reason)
            with portal_page.expect_response(
                lambda response: response.url.endswith("/api/portal/license-requests")
                and response.request.method == "POST",
                timeout=30_000,
            ) as request_response_info:
                portal_page.get_by_role("button", name="Submit Registration Request").click()

            request_response = request_response_info.value
            assert request_response.ok, request_response.text()
            expect(portal_page.locator("#license-requests-body")).to_contain_text(request_reason, timeout=30_000)

            licensing_page = authenticated_context.new_page()
            _sign_in_to_licensing_admin(licensing_page)
            _open_organization(licensing_page, organization_name)

            request_card = licensing_page.locator("article").filter(has_text=request_reason).first
            expect(request_card).to_be_visible(timeout=30_000)
            request_card.get_by_role("button", name="Approve").click()
            licensing_page.wait_for_load_state("domcontentloaded")

            _reconcile_first_payment(licensing_page)

            ready_to_issue_section = _section_by_heading(licensing_page, "Ready to issue")
            expect(ready_to_issue_section.get_by_role("button", name="Issue license").first).to_be_visible(timeout=30_000)
            ready_to_issue_section.get_by_role("button", name="Issue license").first.click()
            licensing_page.wait_for_load_state("domcontentloaded")

            expect(licensing_page.get_by_role("link", name="Download JWT")).to_be_visible(timeout=30_000)

            _reload_until_portal_download_ready(portal_page)
            download_button = portal_page.locator("[data-download-license-id]").first

            with portal_page.expect_download(timeout=30_000) as download_info:
                download_button.click()

            download = download_info.value
            download_path = download.path()
            assert download_path, "The portal did not produce a downloadable license file."

            with open(download_path, "r", encoding="utf-8") as downloaded_license_file:
                license_key = downloaded_license_file.read().strip()

            assert license_key.startswith("eyJ"), "Expected a signed JWT license key from the portal download."

            install_page = authenticated_context.new_page()
            install_page.goto(f"{BASE_URL}/admin/license/platform/install", wait_until="domcontentloaded")
            expect(install_page.get_by_role("heading", name="Install platform license")).to_be_visible(timeout=30_000)
            install_page.locator("textarea[name='Input.LicenseKey']").fill(license_key)
            install_page.locator("textarea[name='Input.Notes']").fill(f"Portal E2E install {suffix}")
            install_page.get_by_role("button", name="Install license").click()

            install_page.wait_for_url(lambda url: url.rstrip("/").endswith("/admin/license/platform"), timeout=30_000)
            expect(install_page.locator("body")).to_contain_text(
                "Platform license installed successfully.", timeout=30_000
            )
            expect(install_page.locator("body")).to_contain_text(organization_name, timeout=30_000)

            install_page.close()
            licensing_page.close()
            admin_setup_page.close()
            portal_page.close()
        finally:
            portal_context.close()