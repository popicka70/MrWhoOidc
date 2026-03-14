"""
E2E tests for authenticated user account pages.

Pages covered:
  /account              -- Account Dashboard
  /account/profile      -- Profile editing
  /account/emails       -- Email management
  /account/web-authn    -- WebAuthn/FIDO2 keys
  /account/sessions     -- Active sessions
  /account/consents     -- OAuth consent records
  /account/linked-accounts -- Linked external accounts
  /account/create-tenant   -- Create new tenant
  /account/access-denied   -- Access denied
  /password             -- Change password
  /mfa                  -- MFA / TOTP setup
"""

from __future__ import annotations

import pytest
from playwright.sync_api import Page, expect


def _assert_evaluation(result, *, min_score: int = 5) -> None:
    if result.skipped:
        return
    if result.error:
        pytest.skip(f"LLM evaluation error: {result.error}")
    assert result.overall_score >= min_score, (
        f"Page '{result.route}' scored {result.overall_score}/10 -- below {min_score}.\n"
        f"Summary: {result.summary}"
    )


def _goto_account(page: Page, path: str) -> None:
    page.goto(path, wait_until="domcontentloaded")
    if "/login" in page.url:
        pytest.skip(f"Redirected to login for {path} -- session expired")


class TestAccountDashboard:
    def test_dashboard_loads(self, authenticated_page: Page, record_evaluation):
        _goto_account(authenticated_page, "/account")
        result = record_evaluation(authenticated_page, "/account")
        _assert_evaluation(result)

    def test_dashboard_account_tabs_visible(self, authenticated_page: Page):
        _goto_account(authenticated_page, "/account")
        tab_links = authenticated_page.locator(
            "a[href*='/account'], a[href*='/password'], a[href*='/mfa']"
        )
        assert tab_links.count() >= 2, "Expected account navigation links"


class TestAccountProfile:
    def test_profile_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_account(authenticated_page, "/account/profile")
        inputs = authenticated_page.locator("form input:not([type='hidden'])").count()
        assert inputs > 0, "No editable fields on Profile page"
        result = record_evaluation(authenticated_page, "/account/profile")
        _assert_evaluation(result)

    def test_profile_form_has_save_button(self, authenticated_page: Page):
        _goto_account(authenticated_page, "/account/profile")
        expect(authenticated_page.locator("button[type='submit']").first).to_be_visible()


class TestAccountEmails:
    def test_emails_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_account(authenticated_page, "/account/emails")
        result = record_evaluation(authenticated_page, "/account/emails")
        _assert_evaluation(result)

    def test_emails_page_shows_current_email(self, authenticated_page: Page):
        _goto_account(authenticated_page, "/account/emails")
        body = authenticated_page.inner_text("body")
        assert "admin" in body.lower() or "@" in body, "Email address not visible"


class TestAccountWebAuthn:
    def test_webauthn_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_account(authenticated_page, "/account/web-authn")
        result = record_evaluation(authenticated_page, "/account/web-authn")
        _assert_evaluation(result)

    def test_webauthn_register_button_visible(self, authenticated_page: Page):
        _goto_account(authenticated_page, "/account/web-authn")
        btn = authenticated_page.locator(
            "button:has-text('Register'), button:has-text('Add'), a:has-text('Register')"
        )
        assert btn.count() > 0, "No register/add WebAuthn key button visible"


class TestAccountSessions:
    def test_sessions_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_account(authenticated_page, "/account/sessions")
        result = record_evaluation(authenticated_page, "/account/sessions")
        _assert_evaluation(result)

    def test_sessions_shows_current_session(self, authenticated_page: Page):
        _goto_account(authenticated_page, "/account/sessions")
        body = authenticated_page.inner_text("body")
        assert len(body) > 100, "Sessions page appears empty"


class TestAccountConsents:
    def test_consents_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_account(authenticated_page, "/account/consents")
        result = record_evaluation(authenticated_page, "/account/consents")
        _assert_evaluation(result)


class TestAccountLinkedAccounts:
    def test_linked_accounts_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_account(authenticated_page, "/account/linked-accounts")
        result = record_evaluation(authenticated_page, "/account/linked-accounts")
        _assert_evaluation(result)


class TestAccountCreateTenant:
    def test_create_tenant_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_account(authenticated_page, "/account/create-tenant")
        result = record_evaluation(authenticated_page, "/account/create-tenant")
        _assert_evaluation(result, min_score=3)


class TestAccountAccessDenied:
    def test_access_denied_page_loads(self, authenticated_page: Page, record_evaluation):
        authenticated_page.goto("/account/access-denied", wait_until="domcontentloaded")
        body = authenticated_page.inner_text("body")
        assert len(body) > 10, "Access denied page has no content"
        result = record_evaluation(authenticated_page, "/account/access-denied")
        _assert_evaluation(result, min_score=2)


class TestPasswordPage:
    def test_password_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_account(authenticated_page, "/password")
        inputs = authenticated_page.locator("input[type='password']").count()
        assert inputs >= 2, f"Expected at least 2 password inputs, found {inputs}"
        result = record_evaluation(authenticated_page, "/password")
        _assert_evaluation(result)

    def test_password_page_requires_current_password(self, authenticated_page: Page):
        _goto_account(authenticated_page, "/password")
        authenticated_page.locator("button[type='submit']").first.click()
        error = authenticated_page.locator(
            ".text-danger, .validation-summary-errors, .alert-danger"
        )
        assert error.count() > 0, "Expected validation errors when submitting empty password form"


class TestMfaPage:
    def test_mfa_page_loads(self, authenticated_page: Page, record_evaluation):
        _goto_account(authenticated_page, "/mfa")
        result = record_evaluation(authenticated_page, "/mfa")
        _assert_evaluation(result)

    def test_mfa_page_has_setup_controls(self, authenticated_page: Page):
        _goto_account(authenticated_page, "/mfa")
        body = authenticated_page.inner_text("body")
        has_mfa_content = any(
            kw in body.lower()
            for kw in ("authenticator", "totp", "qr", "secret", "2fa", "two-factor")
        )
        assert has_mfa_content, "MFA page does not contain TOTP/authenticator content"
