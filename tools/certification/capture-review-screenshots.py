from __future__ import annotations

import base64
import hashlib
import json
import os
import secrets
import time
from pathlib import Path
from urllib.parse import urlencode

import requests
from playwright.sync_api import TimeoutError as PlaywrightTimeoutError
from playwright.sync_api import sync_playwright


ISSUER = "https://mrwho.onrender.com/t/default"
DISCOVERY_URL = f"{ISSUER}/.well-known/openid-configuration"
INITIAL_ACCESS_TOKEN = "oidf-dcr-initial-access-token"
USERNAME = "oidf-cert-user"
PASSWORD = "OidfCertUser123!"
REDIRECT_URI = "https://example.com/callback"
OUTPUT_DIR = Path(__file__).resolve().parent / ".generated" / "review-screenshots" / "2026-04-20"


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def create_pkce_pair() -> tuple[str, str]:
    verifier = b64url(os.urandom(32))
    challenge = b64url(hashlib.sha256(verifier.encode("ascii")).digest())
    return verifier, challenge


def load_discovery() -> dict:
    response = requests.get(DISCOVERY_URL, timeout=30)
    response.raise_for_status()
    return response.json()


def register_client(registration_endpoint: str) -> dict:
    payload = {
        "client_name": f"MrWho review screenshot capture {int(time.time())}",
        "redirect_uris": [REDIRECT_URI],
        "grant_types": ["authorization_code"],
        "response_types": ["code"],
        "scope": "openid profile email",
    }
    response = requests.post(
        registration_endpoint,
        headers={"Authorization": f"Bearer {INITIAL_ACCESS_TOKEN}"},
        json=payload,
        timeout=30,
    )
    response.raise_for_status()
    return response.json()


def unregister_client(client_metadata: dict) -> None:
    registration_uri = client_metadata.get("registration_client_uri")
    registration_access_token = client_metadata.get("registration_access_token")
    if not registration_uri or not registration_access_token:
        return
    try:
        response = requests.delete(
            registration_uri,
            headers={"Authorization": f"Bearer {registration_access_token}"},
            timeout=30,
        )
        response.raise_for_status()
    except requests.RequestException:
        pass


def wait_for_dom(page) -> None:
    try:
        page.wait_for_load_state("domcontentloaded", timeout=15000)
    except PlaywrightTimeoutError:
        pass


def click_and_wait(page, selector: str) -> None:
    page.locator(selector).click(timeout=5000, no_wait_after=True)
    wait_for_dom(page)


def maybe_choose_local_login(page) -> bool:
    if "/auth/providers/select" not in page.url:
        return False
    try:
        if page.locator("#btn-local-login").count() == 0:
            return False
        click_and_wait(page, "#btn-local-login")
        return True
    except PlaywrightTimeoutError:
        return False


def maybe_login(page) -> bool:
    if "/login" not in page.url:
        return False
    try:
        if page.locator("input[name='Username']").count() == 0:
            return False
        page.locator("input[name='Username']").fill(USERNAME, timeout=2000)
        page.locator("input[name='Password']").fill(PASSWORD, timeout=2000)
        click_and_wait(page, "button[type='submit']")
        return True
    except PlaywrightTimeoutError:
        return False


def maybe_consent(page) -> bool:
    if "/consent" not in page.url:
        return False
    try:
        if page.locator("button[type='submit']").count() == 0:
            return False
        click_and_wait(page, "button[type='submit']")
        return True
    except PlaywrightTimeoutError:
        return False


def establish_authenticated_session(page, authorization_url: str, issuer_origin: str) -> None:
    page.goto(authorization_url, wait_until="domcontentloaded")
    for _ in range(16):
        wait_for_dom(page)
        if maybe_choose_local_login(page):
            continue
        if maybe_login(page):
            continue
        if maybe_consent(page):
            continue
        if not page.url.startswith(issuer_origin):
            return
        page.wait_for_timeout(500)
    raise RuntimeError(f"Timed out establishing session. Final URL: {page.url}")


def navigate_to_login_screen(page, authorization_url: str) -> None:
    page.goto(authorization_url, wait_until="domcontentloaded")
    for _ in range(10):
        wait_for_dom(page)
        if maybe_choose_local_login(page):
            continue
        if "/login" in page.url and page.locator("input[name='Username']").count() > 0:
            return
        page.wait_for_timeout(500)
    raise RuntimeError(f"Expected login screen but ended at {page.url}")


def build_authorization_url(authorization_endpoint: str, client_id: str, redirect_uri: str, extra_params: dict | None = None) -> str:
    _, code_challenge = create_pkce_pair()
    params = {
        "client_id": client_id,
        "redirect_uri": redirect_uri,
        "response_type": "code",
        "scope": "openid profile email",
        "state": secrets.token_urlsafe(12),
        "nonce": secrets.token_urlsafe(12),
        "code_challenge": code_challenge,
        "code_challenge_method": "S256",
    }
    if extra_params:
        params.update(extra_params)
    return f"{authorization_endpoint}?{urlencode(params)}"


def capture_prompt_login(page, authorization_endpoint: str, client_id: str, issuer_origin: str) -> Path:
    first_request = build_authorization_url(authorization_endpoint, client_id, REDIRECT_URI)
    establish_authenticated_session(page, first_request, issuer_origin)
    second_request = build_authorization_url(
        authorization_endpoint,
        client_id,
        REDIRECT_URI,
        {"prompt": "login"},
    )
    navigate_to_login_screen(page, second_request)
    output_path = OUTPUT_DIR / "oidcc-prompt-login.png"
    page.screenshot(path=str(output_path), full_page=True)
    return output_path


def capture_max_age(page, authorization_endpoint: str, client_id: str, issuer_origin: str) -> Path:
    first_request = build_authorization_url(authorization_endpoint, client_id, REDIRECT_URI)
    establish_authenticated_session(page, first_request, issuer_origin)
    page.wait_for_timeout(3000)
    second_request = build_authorization_url(
        authorization_endpoint,
        client_id,
        REDIRECT_URI,
        {"max_age": "1"},
    )
    navigate_to_login_screen(page, second_request)
    output_path = OUTPUT_DIR / "oidcc-max-age-1.png"
    page.screenshot(path=str(output_path), full_page=True)
    return output_path


def capture_bad_redirect_uri(page, authorization_endpoint: str, client_id: str) -> Path:
    bad_request = build_authorization_url(
        authorization_endpoint,
        client_id,
        f"{REDIRECT_URI}/bad",
    )
    page.goto(bad_request, wait_until="domcontentloaded")
    wait_for_dom(page)
    output_path = OUTPUT_DIR / "oidcc-ensure-registered-redirect-uri.png"
    page.screenshot(path=str(output_path), full_page=True)
    return output_path


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    discovery = load_discovery()
    client_metadata = register_client(discovery["registration_endpoint"])
    client_id = client_metadata["client_id"]
    issuer_origin = discovery["issuer"]

    outputs = {}
    try:
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(headless=True)

            prompt_context = browser.new_context(ignore_https_errors=True)
            prompt_page = prompt_context.new_page()
            outputs["oidcc-prompt-login"] = str(
                capture_prompt_login(prompt_page, discovery["authorization_endpoint"], client_id, issuer_origin)
            )
            prompt_context.close()

            max_age_context = browser.new_context(ignore_https_errors=True)
            max_age_page = max_age_context.new_page()
            outputs["oidcc-max-age-1"] = str(
                capture_max_age(max_age_page, discovery["authorization_endpoint"], client_id, issuer_origin)
            )
            max_age_context.close()

            redirect_context = browser.new_context(ignore_https_errors=True)
            redirect_page = redirect_context.new_page()
            outputs["oidcc-ensure-registered-redirect-uri"] = str(
                capture_bad_redirect_uri(redirect_page, discovery["authorization_endpoint"], client_id)
            )
            redirect_context.close()

            browser.close()
    finally:
        unregister_client(client_metadata)

    print(json.dumps(outputs, indent=2))


if __name__ == "__main__":
    main()