"""E2E coverage for tenant enrollment flows."""

from __future__ import annotations

import os
import re
import time
from urllib.parse import urlparse

from playwright.sync_api import Page, expect

E2E_PREFIX = "e2e-invite"
_RUN_SUFFIX = f"{str(int(time.time()))[-6:]}{os.environ.get('PYTEST_XDIST_WORKER', '0')}"
_INVITE_EMAIL = f"{E2E_PREFIX}-{_RUN_SUFFIX}@test.local"
_INVITE_NAME = f"E2E Invite {_RUN_SUFFIX}"
_INVITE_PASSWORD = "InviteUser_Pass123!"


def _create_invitation(page: Page, email: str, display_name: str) -> str:
    page.goto("/admin/invitations", wait_until="domcontentloaded")
    expect(page.get_by_role("heading", name="Invitations")).to_be_visible()

    page.locator("input#Input_Email, input[name='Input.Email']").fill(email)
    page.locator("input#Input_DisplayName, input[name='Input.DisplayName']").fill(display_name)
    page.locator("input#Input_ValidDays, input[name='Input.ValidDays']").fill("7")
    page.get_by_role("button", name=re.compile("Create invitation", re.I)).click()
    page.wait_for_load_state("domcontentloaded")

    expect(page.get_by_text(f"Invitation created for {email}.")).to_be_visible()
    link_input = page.locator("div[role='status'] input[readonly]").first
    expect(link_input).to_be_visible()
    invitation_link = link_input.input_value()
    assert "/invitations/inv_" in invitation_link, invitation_link
    return invitation_link


def _submit_invited_registration(page: Page, invitation_link: str, email: str) -> None:
    page.goto(invitation_link, wait_until="domcontentloaded")
    expect(page.get_by_text("You have been invited to join")).to_be_visible()
    sign_in_href = page.get_by_role("link", name=re.compile("Sign in to accept", re.I)).get_attribute("href") or ""
    assert "/t/default/login" in sign_in_href, sign_in_href

    page.get_by_role("link", name=re.compile("Register with this invitation", re.I)).click()
    page.wait_for_load_state("domcontentloaded")

    expect(page.get_by_text("Register with")).to_be_visible()
    expect(page.locator("input#Input_Email")).to_have_value(email)
    expect(page.locator("input#Input_Email")).to_have_attribute("readonly", re.compile(".*"))
    expect(page.locator("input#createTenantCheck")).to_be_hidden()

    page.locator("input#Input_FirstName, input[name='Input.FirstName']").fill("E2E")
    page.locator("input#Input_LastName, input[name='Input.LastName']").fill("Invite")
    page.locator("input#Input_Password, input[name='Input.Password']").fill(_INVITE_PASSWORD)
    page.get_by_role("button", name=re.compile("Submit Registration", re.I)).click()
    page.wait_for_load_state("domcontentloaded")

    expect(page.get_by_role("heading", name="Registration successful")).to_be_visible()


def _login_via_tenant_url(page: Page, email: str, password: str) -> None:
    page.goto("/t/default/login?returnUrl=/account", wait_until="domcontentloaded")

    if urlparse(page.url).path.lower().endswith("/account"):
        return

    page.locator("input#Username").fill(email)
    page.locator("input#Password").fill(password)
    page.locator("button[type='submit']").click()
    page.wait_for_url(lambda url: "/login" not in url.lower() and "/logintotp" not in url.lower(), timeout=30_000)


class TestTenantInvitationEnrollment:
    def test_invited_new_user_can_register_sign_in_and_appear_accepted(
        self,
        authenticated_page: Page,
        page: Page,
        record_evaluation,
    ):
        invitation_link = _create_invitation(authenticated_page, _INVITE_EMAIL, _INVITE_NAME)
        record_evaluation(authenticated_page, "/admin/invitations", label="created-invitation")

        _submit_invited_registration(page, invitation_link, _INVITE_EMAIL)
        record_evaluation(page, "/Registrations", label="invited-registration-success")

        _login_via_tenant_url(page, _INVITE_EMAIL, _INVITE_PASSWORD)
        page.goto("/account", wait_until="domcontentloaded")
        expect(page.locator("body")).to_contain_text(_INVITE_EMAIL)

        authenticated_page.goto("/admin/invitations", wait_until="domcontentloaded")
        invitation_row = authenticated_page.locator(f"tr:has-text('{_INVITE_EMAIL}')").first
        expect(invitation_row).to_be_visible()
        expect(invitation_row).to_contain_text("Accepted")

        authenticated_page.goto(f"/admin/users?search={_INVITE_EMAIL}", wait_until="domcontentloaded")
        expect(authenticated_page.locator("body")).to_contain_text(_INVITE_EMAIL)