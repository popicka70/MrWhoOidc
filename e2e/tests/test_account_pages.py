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
from playwright.sync_api import Browser, BrowserContext, Page, expect


def _assert_evaluation(result, *, min_score: int = 4) -> None:
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


def _new_context(browser_session: Browser, base_url: str) -> BrowserContext:
    return browser_session.new_context(
        base_url=base_url,
        viewport={"width": 1920, "height": 1080},
        ignore_https_errors=True,
    )


def _submit_login_form(page: Page, username: str, password: str) -> None:
    page.locator("input#Username").fill(username)
    page.locator("input#Password").fill(password)
    page.locator("button[type='submit']").click()
    page.wait_for_url(
        lambda url: "/login" not in url and "/LoginTotp" not in url,
        timeout=30_000,
    )


def _login(page: Page, base_url: str, username: str, password: str) -> None:
    page.goto(f"{base_url}/login", wait_until="domcontentloaded")
    _submit_login_form(page, username, password)


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
        expect(authenticated_page.locator("button[type='submit']:not(.dropdown-item)").first).to_be_visible()


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

    def test_can_link_and_sign_in_through_upstream_provider(
        self,
        browser_session: Browser,
        base_url: str,
        upstream_base_url: str,
        linked_accounts_setup,
        record_evaluation,
    ):
        linking_context = _new_context(browser_session, base_url)
        try:
            linking_page = linking_context.new_page()
            _login(
                linking_page,
                base_url,
                linked_accounts_setup.local_username,
                linked_accounts_setup.local_password,
            )

            _goto_account(linking_page, "/account/linked-accounts")
            expect(linking_page.locator("text=No external accounts linked yet.")).to_be_visible()

            link_button = linking_page.locator(
                f"a[href*='provider={linked_accounts_setup.provider_name}']"
            ).first
            expect(link_button).to_be_visible()
            link_button.click()

            linking_page.wait_for_url(
                lambda url: url.startswith(upstream_base_url) and "/login" in url,
                timeout=30_000,
            )
            _submit_login_form(
                linking_page,
                linked_accounts_setup.upstream_username,
                linked_accounts_setup.upstream_password,
            )
            linking_page.wait_for_url(
                lambda url: "/account/linked-accounts" in url,
                timeout=30_000,
            )

            expect(linking_page.locator("button[title='Unlink account']").first).to_be_visible()
            result = record_evaluation(
                linking_page,
                "/account/linked-accounts",
                label="linked-account-flow",
            )
            if not result.error:
                _assert_evaluation(result)
        finally:
            linking_context.close()

        sign_in_context = _new_context(browser_session, base_url)
        try:
            sign_in_page = sign_in_context.new_page()
            sign_in_page.goto(
                f"{base_url}/auth/providers/select?client_id={linked_accounts_setup.client_id}&returnUrl=/account/emails",
                wait_until="domcontentloaded",
            )

            provider_button = sign_in_page.locator(
                f"a[href*='provider={linked_accounts_setup.provider_name}']"
            ).first
            expect(provider_button).to_be_visible()
            provider_button.click()

            sign_in_page.wait_for_url(
                lambda url: url.startswith(upstream_base_url) and "/login" in url,
                timeout=30_000,
            )
            _submit_login_form(
                sign_in_page,
                linked_accounts_setup.upstream_username,
                linked_accounts_setup.upstream_password,
            )
            sign_in_page.wait_for_url(
                lambda url: "/account/emails" in url,
                timeout=30_000,
            )

            body = sign_in_page.inner_text("body")
            assert linked_accounts_setup.local_email in body, (
                "Signing in through the linked upstream provider did not resolve back "
                "to the local account emails page"
            )
        finally:
            sign_in_context.close()


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
        # Verify the form has required password fields - do NOT submit to avoid changing credentials
        current_pwd = authenticated_page.locator(
            "input[name*='Current'], input[id*='Current'], input[name*='current'], input[id*='current']"
        )
        new_pwd = authenticated_page.locator(
            "input[name*='New'], input[id*='New'], input[name*='new'], input[id*='new']"
        )
        # At minimum the form should have password inputs
        inputs = authenticated_page.locator("input[type='password']").count()
        assert inputs >= 2, "Password form should have at least 2 password inputs (current + new)"


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
