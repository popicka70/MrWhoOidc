from __future__ import annotations

import os

from playwright.sync_api import Page, expect

BASE_URL: str = os.getenv("BASE_URL", "https://localhost:8443")
EXAMPLE_RAZORCLIENT_URL: str = os.getenv("EXAMPLE_RAZORCLIENT_URL", "https://localhost:5003")
ADMIN_USERNAME: str = os.getenv("ADMIN_USERNAME", "admin@mrwho.local")
ADMIN_PASSWORD: str = os.getenv("ADMIN_PASSWORD", "Admin123!")


def _login_through_webauth(page: Page) -> None:
    page.get_by_label("Username").fill(ADMIN_USERNAME)
    page.get_by_label("Password").fill(ADMIN_PASSWORD)
    page.locator("button[type='submit']").click()


def _continue_to_local_login(page: Page) -> None:
    page.wait_for_url(lambda url: url.startswith(BASE_URL), timeout=30_000)

    local_login = page.locator("#btn-local-login")
    if local_login.count():
        local_login.click()

    page.wait_for_url(lambda url: url.startswith(BASE_URL) and "/login" in url.lower(), timeout=30_000)


class TestExampleRazorClient:
    def test_home_page_loads(self, page: Page, example_razorclient_url: str, record_evaluation) -> None:
        page.goto(example_razorclient_url, wait_until="domcontentloaded")

        expect(page.get_by_role("heading", name="MrWhoOidc Razor Client")).to_be_visible()
        expect(page.get_by_role("link", name="Standard sign-in")).to_be_visible()
        expect(page.get_by_text("Discovery snapshot")).to_be_visible()

        record_evaluation(page, "example-razorclient-home")

    def test_login_and_downstream_api_flow(self, page: Page, example_razorclient_url: str, record_evaluation) -> None:
        page.goto(example_razorclient_url, wait_until="domcontentloaded")
        page.get_by_role("link", name="Standard sign-in").click()

        _continue_to_local_login(page)
        _login_through_webauth(page)

        page.wait_for_url(lambda url: url.startswith(example_razorclient_url), timeout=30_000)
        expect(page.get_by_text("Welcome,")).to_be_visible()

        page.goto(f"{example_razorclient_url}/Secure", wait_until="domcontentloaded")

        expect(page.get_by_role("heading", name="Secure area")).to_be_visible()
        expect(page.get_by_text("Downstream API Call (On-Behalf-Of)")).to_be_visible()
        expect(page.get_by_role("alert")).to_have_count(0)

        subject_row = page.locator("tr", has_text="Subject")
        audience_row = page.locator("tr", has_text="Audience")
        actor_row = page.locator("tr", has_text="Delegating Client")
        scopes_row = page.locator("tr", has_text="Scopes")

        expect(subject_row.locator("td").first).not_to_be_empty()
        expect(audience_row).to_contain_text("api")
        expect(actor_row).to_contain_text("blazor-web")
        expect(scopes_row).to_contain_text("api.read")

        record_evaluation(page, "example-razorclient-secure")