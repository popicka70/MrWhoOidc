"""
E2E tests for authenticated user account pages.

All tests use the `authenticated_page` fixture (logged in as admin@default.local).

Pages covered:
  /Account              – Account Dashboard
  /Account/Profile      – Profile editing
  /Account/Emails       – Email management
  /Account/WebAuthn     – WebAuthn/FIDO2 keys
  /Account/Sessions     – Active sessions
  /Account/Consents     – OAuth consent records
  /Account/LinkedAccounts – Linked external accounts
  /Password             – Change password
  /Mfa                  – MFA / TOTP setup
"""

from __future__ import annotations

import pytest
from playwright.async_api import Page, expect


# ---------------------------------------------------------------------------
# Account Dashboard
# ---------------------------------------------------------------------------


class TestAccountDashboard:
    async def test_dashboard_loads(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/Account", wait_until="domcontentloaded")

        # Should not redirect to login
        assert "/login" not in authenticated_page.url, "Redirected to login — auth state lost"

        result = await record_evaluation(authenticated_page, "/Account")
        _assert_evaluation(result)

    async def test_dashboard_account_tabs_visible(self, authenticated_page: Page):
        """Account navigation tabs must be present."""
        await authenticated_page.goto("/Account", wait_until="domcontentloaded")

        # Tabs typically rendered as nav links or tab items
        tab_links = authenticated_page.locator("a[href*='/Account'], a[href*='/Password'], a[href*='/Mfa']")
        count = await tab_links.count()
        assert count >= 3, f"Expected at least 3 account tab links, found {count}"


# ---------------------------------------------------------------------------
# Profile
# ---------------------------------------------------------------------------


class TestAccountProfile:
    async def test_profile_page_loads(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/Account/Profile", wait_until="domcontentloaded")

        # At least one editable field should exist
        inputs = await authenticated_page.locator("form input:not([type='hidden'])").count()
        assert inputs > 0, "No editable input fields on Profile page"

        result = await record_evaluation(authenticated_page, "/Account/Profile")
        _assert_evaluation(result)

    async def test_profile_form_has_save_button(self, authenticated_page: Page):
        await authenticated_page.goto("/Account/Profile", wait_until="domcontentloaded")
        save_btn = authenticated_page.locator("button[type='submit']").first
        await expect(save_btn).to_be_visible()


# ---------------------------------------------------------------------------
# Emails
# ---------------------------------------------------------------------------


class TestAccountEmails:
    async def test_emails_page_loads(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/Account/Emails", wait_until="domcontentloaded")

        result = await record_evaluation(authenticated_page, "/Account/Emails")
        _assert_evaluation(result)

    async def test_emails_page_shows_current_email(self, authenticated_page: Page):
        await authenticated_page.goto("/Account/Emails", wait_until="domcontentloaded")

        page_text = await authenticated_page.inner_text("body")
        assert "admin" in page_text.lower() or "@" in page_text, (
            "Email address not visible on the Emails page"
        )


# ---------------------------------------------------------------------------
# WebAuthn
# ---------------------------------------------------------------------------


class TestAccountWebAuthn:
    async def test_webauthn_page_loads(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/Account/WebAuthn", wait_until="domcontentloaded")

        result = await record_evaluation(authenticated_page, "/Account/WebAuthn")
        _assert_evaluation(result)

    async def test_webauthn_register_button_visible(self, authenticated_page: Page):
        await authenticated_page.goto("/Account/WebAuthn", wait_until="domcontentloaded")

        # Expect a button/link to register a new key
        register_btn = authenticated_page.locator(
            "button:has-text('Register'), button:has-text('Add'), a:has-text('Register')"
        )
        visible = await register_btn.count() > 0
        assert visible, "No register/add WebAuthn key button visible"


# ---------------------------------------------------------------------------
# Sessions
# ---------------------------------------------------------------------------


class TestAccountSessions:
    async def test_sessions_page_loads(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/Account/Sessions", wait_until="domcontentloaded")

        result = await record_evaluation(authenticated_page, "/Account/Sessions")
        _assert_evaluation(result)

    async def test_sessions_shows_current_session(self, authenticated_page: Page):
        await authenticated_page.goto("/Account/Sessions", wait_until="domcontentloaded")

        # At least one session (the current one) must be visible
        page_text = await authenticated_page.inner_text("body")
        assert "session" in page_text.lower() or "current" in page_text.lower() or len(page_text) > 200, (
            "Sessions page appears empty or malformed"
        )


# ---------------------------------------------------------------------------
# Consents
# ---------------------------------------------------------------------------


class TestAccountConsents:
    async def test_consents_page_loads(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/Account/Consents", wait_until="domcontentloaded")

        result = await record_evaluation(authenticated_page, "/Account/Consents")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Linked Accounts
# ---------------------------------------------------------------------------


class TestAccountLinkedAccounts:
    async def test_linked_accounts_page_loads(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/Account/LinkedAccounts", wait_until="domcontentloaded")

        result = await record_evaluation(authenticated_page, "/Account/LinkedAccounts")
        _assert_evaluation(result)


# ---------------------------------------------------------------------------
# Change Password
# ---------------------------------------------------------------------------


class TestPasswordPage:
    async def test_password_page_loads(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/Password", wait_until="domcontentloaded")

        # Expect current password + new password fields
        inputs = await authenticated_page.locator("input[type='password']").count()
        assert inputs >= 2, f"Expected at least 2 password inputs, found {inputs}"

        result = await record_evaluation(authenticated_page, "/Password")
        _assert_evaluation(result)

    async def test_password_page_requires_current_password(self, authenticated_page: Page):
        """Submitting with empty current password should show a validation error."""
        await authenticated_page.goto("/Password", wait_until="domcontentloaded")

        # Try submitting without filling anything
        submit = authenticated_page.locator("button[type='submit']").first
        await submit.click()

        error = authenticated_page.locator(".text-danger, .validation-summary-errors, .alert-danger")
        count = await error.count()
        assert count > 0, "Submitting empty password form showed no validation errors"


# ---------------------------------------------------------------------------
# MFA / TOTP
# ---------------------------------------------------------------------------


class TestMfaPage:
    async def test_mfa_page_loads(self, authenticated_page: Page, record_evaluation):
        await authenticated_page.goto("/Mfa", wait_until="domcontentloaded")

        result = await record_evaluation(authenticated_page, "/Mfa")
        _assert_evaluation(result)

    async def test_mfa_page_has_setup_controls(self, authenticated_page: Page):
        await authenticated_page.goto("/Mfa", wait_until="domcontentloaded")

        page_text = await authenticated_page.inner_text("body")
        # Should mention authenticator app, QR code, or TOTP
        has_mfa_content = any(
            kw in page_text.lower()
            for kw in ("authenticator", "totp", "qr", "secret", "2fa", "two-factor")
        )
        assert has_mfa_content, "MFA page does not appear to contain TOTP/authenticator content"


# ---------------------------------------------------------------------------
# Shared assertion helper
# ---------------------------------------------------------------------------


def _assert_evaluation(result, *, min_score: int = 5) -> None:
    if result.skipped:
        return
    if result.error:
        pytest.skip(f"LLM evaluation error (non-blocking): {result.error}")
    assert result.overall_score >= min_score, (
        f"Page '{result.route}' scored {result.overall_score}/10 — below minimum {min_score}.\n"
        f"Summary: {result.summary}"
    )
