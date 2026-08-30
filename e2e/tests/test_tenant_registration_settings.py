"""E2E coverage for tenant-specific user registration settings."""

from __future__ import annotations

import os
import re
import time
from urllib.parse import parse_qs, urlparse

from playwright.sync_api import Page, expect

from utils.cli_helper import CliHelper

E2E_PREFIX = "e2e-tenant-reg"
_RUN_SUFFIX = f"{str(int(time.time()))[-6:]}{os.environ.get('PYTEST_XDIST_WORKER', '0')}"
_HEADLINE = f"Join Default Tenant {_RUN_SUFFIX}"
_INTRO = "Tenant admins can tailor this registration page for their users."
_HERO_IMAGE_URL = "https://example.com/e2e-registration.png"
_TENANT_EMAIL = f"{E2E_PREFIX}-{_RUN_SUFFIX}@test.local"
_TENANT_PASSWORD = "TenantReg_Pass123!"


def _set_registration_mode(cli: CliHelper, mode: str, *, customize: bool = False) -> dict:
    args = ["registration", "set", "--mode", mode]
    if customize:
        args.extend([
            "--headline", _HEADLINE,
            "--intro", _INTRO,
            "--hero-image-url", _HERO_IMAGE_URL,
        ])
    data = cli.run_json(*args)
    assert data.get("mode") == mode
    return data


class TestTenantRegistrationSettings:
    def test_01_cli_can_configure_and_observe_registration_settings(self, cli_logged_in: CliHelper):
        updated = _set_registration_mode(cli_logged_in, "both", customize=True)
        assert updated.get("headline") == _HEADLINE
        assert updated.get("introText") == _INTRO
        assert updated.get("heroImageUrl") == _HERO_IMAGE_URL
        assert "/t/default/Registrations" in updated.get("tenantRegistrationUrl", "")

        observed = cli_logged_in.run_json("registration", "get")
        assert observed.get("mode") == "both"
        assert observed.get("headline") == _HEADLINE

    def test_02_tenant_registration_page_uses_customization_and_submits(self, cli_logged_in: CliHelper, page: Page):
        _set_registration_mode(cli_logged_in, "both", customize=True)

        page.goto("/t/default/Registrations", wait_until="domcontentloaded")
        expect(page.get_by_role("heading", name=re.compile(re.escape(_HEADLINE), re.I))).to_be_visible()
        expect(page.get_by_text(_INTRO)).to_be_visible()
        expect(page.locator(f"img[src='{_HERO_IMAGE_URL}']")).to_be_visible()
        expect(page.locator("input#createTenantCheck")).to_be_hidden()

        page.locator("input#Input_Email, input[name='Input.Email']").fill(_TENANT_EMAIL)
        page.locator("input#Input_FirstName, input[name='Input.FirstName']").fill("Tenant")
        page.locator("input#Input_LastName, input[name='Input.LastName']").fill("Registrant")
        page.locator("input#Input_Password, input[name='Input.Password']").fill(_TENANT_PASSWORD)
        page.get_by_role("button", name=re.compile("Submit Registration", re.I)).click()
        page.wait_for_load_state("domcontentloaded")

        assert urlparse(page.url).path == "/t/default/Registrations/Accepted"
        expect(page.locator("body")).to_contain_text("Registration submitted")
        expect(page.get_by_role("link", name=re.compile("Back to Login", re.I))).to_be_visible()
        expect(page.get_by_role("link", name=re.compile("New Registration", re.I))).to_be_visible()

    def test_03_platform_only_redirects_tenant_registration_path(self, cli_logged_in: CliHelper, page: Page):
        _set_registration_mode(cli_logged_in, "platform-only")

        page.goto("/t/default/Registrations?returnUrl=%2Faccount", wait_until="domcontentloaded")
        parsed_url = urlparse(page.url)
        assert parsed_url.path == "/Registrations"
        assert parse_qs(parsed_url.query).get("returnUrl") == ["/account"]
        expect(page.locator("body")).not_to_contain_text("Tenant-specific registration is not enabled")
        expect(page.get_by_role("button", name=re.compile("Submit Registration", re.I))).to_be_visible()

        observed = cli_logged_in.run_json("registration", "get")
        assert observed.get("mode") == "platform-only"