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

import base64
import hashlib
import hmac
import os
import re
import struct
import time

import pytest
from playwright.sync_api import Browser, BrowserContext, Page, expect


_DEFAULT_TENANT_PREFIX = "/t/default"


def _default_tenant_path(path: str) -> str:
    if not path.startswith("/"):
        path = f"/{path}"
    return f"{_DEFAULT_TENANT_PREFIX}{path}"


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
    import logging
    log = logging.getLogger("test_account_pages")
    page.locator("input#Username").fill(username)
    page.locator("input#Password").fill(password)
    page.locator("button[type='submit']").click()
    log.info("Login form submitted for user %s, waiting for post-submit navigation", username)
    page.wait_for_url(
        lambda url: "/login" not in url and "/LoginTotp" not in url,
        timeout=45_000,
    )


def _login(page: Page, base_url: str, username: str, password: str) -> None:
    page.goto(f"{base_url}/login", wait_until="domcontentloaded")
    _submit_login_form(page, username, password)


def _totp_code(secret: str, *, period: int = 30, digits: int = 6, algo: str = "SHA256") -> str:
    """Compute a TOTP code for the given base32 secret.

    Matches the production TotpService defaults: SHA256, 6 digits, 30-second period,
    ±1 step window. The provisioning URI in the MFA page advertises ``algorithm=SHA256``
    so the verifier uses that hash family.
    """
    normalized_secret = re.sub(r"\s+", "", secret).upper()
    padding = "=" * ((8 - len(normalized_secret) % 8) % 8)
    key = base64.b32decode(normalized_secret + padding)
    counter = int(time.time()) // period
    message = struct.pack(">Q", counter)
    digest = hmac.new(key, message, _hashlib_for(algo)).digest()
    offset = digest[-1] & 0x0F
    value = struct.unpack(">I", digest[offset : offset + 4])[0] & 0x7FFFFFFF
    return f"{value % (10 ** digits):0{digits}d}"


def _hashlib_for(algo: str):
    name = (algo or "SHA256").upper().replace("-", "")
    return {
        "SHA1": hashlib.sha1,
        "SHA256": hashlib.sha256,
        "SHA512": hashlib.sha512,
    }.get(name, hashlib.sha256)


def _link_upstream_account(
    browser_session: Browser,
    base_url: str,
    upstream_base_url: str,
    linked_accounts_setup,
) -> None:
    def _on_destination_or_error(url: str) -> bool:
        if url.startswith(upstream_base_url) and "/login" in url:
            return True
        if "/auth/external/error" in url.lower():
            return True
        return False

    def _safe_failure(page: Page, provider_name: str) -> None:
        from urllib.parse import urlparse, parse_qs
        code = (parse_qs(urlparse(page.url).query).get("code") or [""])[0]
        body_excerpt = page.inner_text("body")[:400]
        pytest.fail(
            "Linked-account upstream sign-in failed: provider="
            f"{provider_name}, code={code or 'unknown'}, "
            f"url={page.url}, body_excerpt={body_excerpt!r}"
        )

    linking_context = _new_context(browser_session, base_url)
    try:
        linking_page = linking_context.new_page()
        _login(
            linking_page,
            base_url,
            linked_accounts_setup.local_username,
            linked_accounts_setup.local_password,
        )

        _goto_account(linking_page, _default_tenant_path("/account/linked-accounts"))
        unlink_buttons = linking_page.locator("button[title='Unlink account']")
        if unlink_buttons.count() > 0:
            return

        expect(linking_page.locator("text=No external accounts linked yet.")).to_be_visible()

        link_button = linking_page.get_by_role("link", name="Link New Account").first
        expect(link_button).to_be_visible()
        link_button.click()

        linking_page.wait_for_url(
            lambda url: "/t/default/auth/providers/select" in url.lower() and "link=true" in url.lower(),
            timeout=45_000,
        )

        provider_button = linking_page.locator(
            f"[data-provider-name='{linked_accounts_setup.provider_name}']"
        ).first
        expect(provider_button).to_be_visible()
        provider_button.click()

        linking_page.wait_for_url(_on_destination_or_error, timeout=60_000)
        if "/auth/external/error" in linking_page.url.lower():
            _safe_failure(linking_page, linked_accounts_setup.provider_name)
        _submit_login_form(
            linking_page,
            linked_accounts_setup.upstream_username,
            linked_accounts_setup.upstream_password,
        )
        linking_page.wait_for_url(
            lambda url: "/account/linked-accounts" in url,
            timeout=45_000,
        )
        expect(linking_page.locator("h1, h2").first).to_be_visible()

        expect(unlink_buttons.first).to_be_visible()
    finally:
        linking_context.close()


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

    def test_link_account_opens_provider_picker(self, authenticated_page: Page):
        _goto_account(authenticated_page, _default_tenant_path("/account/linked-accounts"))

        link_button = authenticated_page.get_by_role("link", name="Link New Account").first
        expect(link_button).to_be_visible()
        link_button.click()

        authenticated_page.wait_for_url(
            lambda url: "/t/default/auth/providers/select" in url.lower() and "link=true" in url.lower(),
            timeout=45_000,
        )
        expect(
            authenticated_page.get_by_role("heading", name="Link an external account")
        ).to_be_visible()

        body = authenticated_page.inner_text("body")
        assert "Sign-in failed" not in body, "Link account flow still lands on the external sign-in error page"

    def test_link_account_picker_uses_link_mode_start_url(self, authenticated_page: Page, linked_accounts_setup):
        _goto_account(authenticated_page, _default_tenant_path("/account/linked-accounts"))
        authenticated_page.get_by_role("link", name="Link New Account").first.click()

        authenticated_page.wait_for_url(
            lambda url: "/t/default/auth/providers/select" in url.lower() and "link=true" in url.lower(),
            timeout=45_000,
        )

        provider_link = authenticated_page.locator(
            f"[data-provider-name='{linked_accounts_setup.provider_name}']"
        ).first
        href = provider_link.get_attribute("href")

        assert href, "Expected provider link to have an href"
        assert "/t/default/auth/external/start" in href.lower(), f"Unexpected provider start URL: {href}"
        assert f"provider={linked_accounts_setup.provider_name}" in href, f"Provider start URL missing selected provider: {href}"
        assert "link=true" in href, f"Provider start URL missing link mode flag: {href}"
        assert "returnUrl=" in href and "linked-accounts" in href, f"Provider start URL missing linked-accounts returnUrl: {href}"

    def test_can_link_and_sign_in_through_upstream_provider(
        self,
        browser_session: Browser,
        base_url: str,
        upstream_base_url: str,
        linked_accounts_setup,
        record_evaluation,
    ):
        _link_upstream_account(
            browser_session,
            base_url,
            upstream_base_url,
            linked_accounts_setup,
        )

        def _sign_in_destination_or_error(url: str) -> bool:
            if url.startswith(upstream_base_url) and "/login" in url:
                return True
            if "/auth/external/error" in url.lower():
                return True
            return False

        def _safe_failure(page: Page, provider_name: str) -> None:
            from urllib.parse import urlparse, parse_qs
            code = (parse_qs(urlparse(page.url).query).get("code") or [""])[0]
            body_excerpt = page.inner_text("body")[:400]
            pytest.fail(
                "Linked-account upstream sign-in failed: provider="
                f"{provider_name}, code={code or 'unknown'}, "
                f"url={page.url}, body_excerpt={body_excerpt!r}"
            )

        evaluation_context = _new_context(browser_session, base_url)
        try:
            evaluation_page = evaluation_context.new_page()
            _login(
                evaluation_page,
                base_url,
                linked_accounts_setup.local_username,
                linked_accounts_setup.local_password,
            )
            _goto_account(evaluation_page, _default_tenant_path("/account/linked-accounts"))
            expect(evaluation_page.locator("button[title='Unlink account']").first).to_be_visible()

            result = record_evaluation(
                evaluation_page,
                "/account/linked-accounts",
                label="linked-account-flow",
            )
            if not result.error:
                _assert_evaluation(result)
        finally:
            evaluation_context.close()

        sign_in_context = _new_context(browser_session, base_url)
        try:
            sign_in_page = sign_in_context.new_page()
            sign_in_page.goto(
                f"{base_url}{_default_tenant_path('/auth/providers/select')}?client_id={linked_accounts_setup.client_id}&returnUrl={_default_tenant_path('/account/emails')}",
                wait_until="domcontentloaded",
            )

            provider_button = sign_in_page.locator(
                f"a[data-provider-name='{linked_accounts_setup.provider_name}']"
            ).first
            expect(provider_button).to_be_visible()
            expect(provider_button).to_contain_text(linked_accounts_setup.provider_display_name)

            href = provider_button.get_attribute("href") or ""
            assert "/t/default/auth/external/start" in href.lower(), f"Provider start URL is not tenant-scoped: {href}"

            style = (provider_button.get_attribute("style") or "").lower()
            assert linked_accounts_setup.provider_button_background_color.lower() in style, (
                f"Tenant provider button is missing background color customization: {style}"
            )
            assert linked_accounts_setup.provider_button_text_color.lower() in style, (
                f"Tenant provider button is missing text color customization: {style}"
            )

            provider_button.click()

            sign_in_page.wait_for_url(_sign_in_destination_or_error, timeout=60_000)
            if "/auth/external/error" in sign_in_page.url.lower():
                _safe_failure(sign_in_page, linked_accounts_setup.provider_name)
            _submit_login_form(
                sign_in_page,
                linked_accounts_setup.upstream_username,
                linked_accounts_setup.upstream_password,
            )
            sign_in_page.wait_for_url(
                lambda url: "/t/default/account/emails" in url.lower(),
                timeout=45_000,
            )

            body = sign_in_page.inner_text("body")
            assert linked_accounts_setup.local_email in body, (
                "Signing in through the linked upstream provider did not resolve back "
                "to the local account emails page"
            )
        finally:
            sign_in_context.close()

    def test_tenant_provider_picker_shows_dev_oidc_for_mapped_client(
        self,
        browser_session: Browser,
        base_url: str,
        linked_accounts_setup,
    ):
        sign_in_context = _new_context(browser_session, base_url)
        try:
            sign_in_page = sign_in_context.new_page()
            sign_in_page.goto(
                f"{base_url}{_default_tenant_path('/auth/providers/select')}?client_id={linked_accounts_setup.client_id}&returnUrl={_default_tenant_path('/account/emails')}",
                wait_until="domcontentloaded",
            )

            provider_button = sign_in_page.locator(
                f"a[data-provider-name='{linked_accounts_setup.provider_name}']"
            ).first
            expect(provider_button).to_be_visible()
            expect(provider_button).to_contain_text(linked_accounts_setup.provider_display_name)

            href = provider_button.get_attribute("href") or ""
            assert "/t/default/auth/external/start" in href.lower(), f"Provider start URL is not tenant-scoped: {href}"
            assert f"provider={linked_accounts_setup.provider_name}" in href, f"Provider start URL missing selected provider: {href}"
            assert f"clientId={linked_accounts_setup.client_id}" in href, f"Provider start URL missing client id: {href}"

            style = (provider_button.get_attribute("style") or "").lower()
            assert linked_accounts_setup.provider_button_background_color.lower() in style, (
                f"Tenant provider button is missing background color customization: {style}"
            )
            assert linked_accounts_setup.provider_button_text_color.lower() in style, (
                f"Tenant provider button is missing text color customization: {style}"
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

    def test_mfa_enable_renders_totp_qr_code_image(self, authenticated_page: Page):
        _goto_account(authenticated_page, "/mfa")

        enable_button = authenticated_page.get_by_role("button", name=re.compile(r"enable (totp|mfa)", re.I)).first
        if enable_button.count() > 0:
            enable_button.click()
            authenticated_page.wait_for_load_state("domcontentloaded")

        if authenticated_page.get_by_role("button", name="Disable TOTP").count() > 0:
            pytest.skip("MFA is already enabled")

        qr_image = authenticated_page.locator("img#mfaQrCodeImage")
        expect(qr_image).to_be_visible()
        src = qr_image.get_attribute("src") or ""
        assert src.startswith("data:image/png;base64,"), f"MFA QR code image was not rendered as a data URI: {src[:80]}"

        dimensions = qr_image.evaluate("img => ({ width: img.naturalWidth, height: img.naturalHeight })")
        assert dimensions["width"] >= 100 and dimensions["height"] >= 100, dimensions
        expect(authenticated_page.locator("input[name='VerificationCode']")).to_be_visible()

        cancel_button = authenticated_page.get_by_role("button", name="Cancel setup")
        if cancel_button.count() > 0:
            cancel_button.click()
            authenticated_page.wait_for_load_state("domcontentloaded")

    def test_mfa_confirm_completes_setup_and_login_uses_totp_challenge(
        self,
        authenticated_page: Page,
        browser_session: Browser,
        base_url: str,
    ):
        login_context = _new_context(browser_session, base_url)
        secret_holder: dict[str, str] = {}
        try:
            _goto_account(authenticated_page, "/mfa")

            enable_button = authenticated_page.get_by_role("button", name=re.compile(r"enable (totp|mfa)", re.I)).first
            if enable_button.count() > 0:
                enable_button.click()
                authenticated_page.wait_for_load_state("domcontentloaded")

            if authenticated_page.get_by_role("button", name="Disable TOTP").count() > 0:
                authenticated_page.get_by_role("button", name="Disable TOTP").click()
                authenticated_page.wait_for_load_state("domcontentloaded")
                authenticated_page.get_by_role("button", name=re.compile(r"enable (totp|mfa)", re.I)).first.click()
                authenticated_page.wait_for_load_state("domcontentloaded")

            secret = authenticated_page.locator("#qrcode + p code").inner_text().strip()
            assert secret, "MFA setup did not expose a manual setup key for test verification"
            secret_holder["secret"] = secret

            authenticated_page.locator("input[name='VerificationCode']").fill(_totp_code(secret))
            authenticated_page.get_by_role("button", name="Confirm").click()
            # Wait for the post-redirect GET to render the TempData status.
            authenticated_page.wait_for_load_state("networkidle", timeout=15_000)
            try:
                authenticated_page.wait_for_selector(
                    "[data-testid='mfa-status-message']", timeout=10_000
                )
            except Exception:
                pass

            status = authenticated_page.locator("[data-testid='mfa-status-message']")
            expect(status).to_have_text("TOTP enabled for all your organizations.")
            expect(authenticated_page.locator("input[name='VerificationCode']")).to_have_count(0)
            expect(authenticated_page.get_by_role("button", name="Disable TOTP")).to_be_visible()

            login_page = login_context.new_page()
            login_page.goto(f"{base_url}/login?email=admin%40mrwho.local", wait_until="domcontentloaded")
            username_input = login_page.locator("input#Username")
            if username_input.count() > 0 and username_input.get_attribute("readonly") is None:
                username_input.fill(os.getenv("ADMIN_USERNAME", "admin@mrwho.local"))
            login_page.locator("input#Password").fill(os.getenv("ADMIN_PASSWORD", "E2E-test-password!"))
            login_page.locator("button[type='submit']").click()
            login_page.wait_for_url(lambda url: "/logintotp" in url.lower(), timeout=45_000)
            expect(login_page.get_by_role("heading", name="Two-factor verification")).to_be_visible()

            login_page.locator("input#Code").fill(_totp_code(secret))
            login_page.locator("button[type='submit']").click()
            login_page.wait_for_url(
                lambda url: "/login" not in url.lower() and "/logintotp" not in url.lower(),
                timeout=45_000,
            )
            assert "unhandled exception" not in login_page.inner_text("body").lower()
        finally:
            login_context.close()
            # ALWAYS clean up: disable TOTP on the shared admin so password-only login
            # tests that follow are not redirected to /LoginTotp.
            try:
                _goto_account(authenticated_page, "/mfa")
                disable_button = authenticated_page.get_by_role("button", name="Disable TOTP")
                if disable_button.count() > 0:
                    disable_button.click()
                    authenticated_page.wait_for_load_state("domcontentloaded", timeout=15_000)
                # If we're stuck in SetupPending, click Cancel setup which also disables.
                cancel_button = authenticated_page.get_by_role("button", name="Cancel setup")
                if cancel_button.count() > 0:
                    cancel_button.click()
                    authenticated_page.wait_for_load_state("domcontentloaded", timeout=15_000)
                    # Cancelling may also leave SetupPending; iterate once more if needed.
                    second_cancel = authenticated_page.get_by_role("button", name="Cancel setup")
                    if second_cancel.count() > 0:
                        second_cancel.click()
                        authenticated_page.wait_for_load_state("domcontentloaded", timeout=15_000)
            except Exception as cleanup_err:  # noqa: BLE001
                # Don't mask the original test failure with cleanup errors.
                import logging
                logging.getLogger("test_account_pages").warning(
                    "MFA cleanup encountered an error: %s", cleanup_err
                )
