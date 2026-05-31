"""
Shared helpers for the OIDC protocol E2E tests (device flow, consent, logout,
response modes, dynamic registration, rate limiting, etc.).

These mirror the private helpers in ``test_oidc_flows.py`` but are exposed as a
reusable module so the additional protocol-coverage suites can share a single
implementation. Keep this dependency-free beyond the existing ``utils`` package.
"""

from __future__ import annotations

import html as _html
import json
import os
import re as _re
import secrets
import urllib.parse
from pathlib import Path

from utils.cli_helper import CliHelper

BASE_URL: str = os.getenv("BASE_URL", "https://localhost:8443")
ADMIN_USERNAME: str = os.getenv("ADMIN_USERNAME", "admin@mrwho.local")
ADMIN_PASSWORD: str = os.getenv("ADMIN_PASSWORD", "E2E-test-password!")

#: Prefix for all e2e protocol-coverage artifacts (clients, users, ...).
E2E_PREFIX = "e2e-proto"
#: Random per-run suffix to keep parallel/repeat runs from colliding.
RUN_SUFFIX = secrets.token_hex(3)

REDIRECT_URI = "https://e2e-proto.test/callback"
LOGOUT_REDIRECT_URI = "https://e2e-proto.test/post-logout"


def get_default_realm_id(cli: CliHelper) -> str:
    """Get the default realm GUID."""
    data = cli.run_json("realm", "list")
    match = [r for r in data if r.get("name") == "default"]
    assert len(match) == 1, "Default realm not found"
    return str(match[0]["id"])


def get_client_internal_id(cli: CliHelper, client_id: str) -> str:
    """Look up the internal GUID for a client by its client_id string."""
    data = cli.run_json("client", "list")
    match = [c for c in data if c.get("clientId") == client_id]
    assert len(match) == 1, f"Client '{client_id}' not found"
    return str(match[0]["id"])


def set_auto_approval(authenticated_context, client_internal_id: str) -> None:
    """Set AutoApprovalMode=All on a client via the admin edit page."""
    page = authenticated_context.new_page()
    try:
        page.goto(
            f"{BASE_URL}/admin/clients/edit/{client_internal_id}",
            wait_until="domcontentloaded",
        )
        page.select_option("#Input_AutoApprovalMode", "All")
        page.click("button[type='submit'].btn-primary")
        page.wait_for_load_state("domcontentloaded")
    finally:
        page.close()


def follow_authorize_redirects(authenticated_context, auth_url: str, *,
                               callback_uri: str = REDIRECT_URI) -> str:
    """Follow the /authorize redirect chain and return the callback URL.

    Uses Playwright's APIRequestContext (shares browser session cookies) to
    follow server-side redirects. The final hop may be the ``/Auth/Redirect``
    page which performs a client-side redirect — parse it from the HTML.
    """
    api = authenticated_context.request
    url = auth_url
    callback_host = callback_uri.split("//")[1].split("/")[0]
    resp = None

    for _ in range(10):
        try:
            resp = api.get(url, max_redirects=0, ignore_https_errors=True)
        except Exception:
            break

        if resp.status not in (301, 302, 303, 307, 308):
            if resp.status == 200 and "/Auth/Redirect" in url:
                body = resp.text()
                match = _re.search(r'<a\s+href="([^"]+)"', body)
                if match:
                    return _html.unescape(match.group(1))
                match = _re.search(r'content="0;url=([^"]+)"', body)
                if match:
                    return _html.unescape(match.group(1))
            break

        location = resp.headers.get("location", "")
        if callback_host in location:
            return location
        if location.startswith("/"):
            url = BASE_URL + location
        else:
            url = location

    raise AssertionError(
        f"Authorization code redirect not found. "
        f"Last status: {getattr(resp, 'status', '?')}, last URL: {url}"
    )


def create_client_with_secret(
    cli: CliHelper,
    *,
    client_id: str,
    client_name: str,
    realm_id: str,
    scope: str,
    grant_types: list[str],
    redirect_uris: list[str] | None = None,
    logout_redirect_uris: list[str] | None = None,
    require_pkce: bool = False,
    require_consent: bool = True,
    extra_args: list[str] | None = None,
    cred_path: Path,
) -> dict:
    """Create a client via CLI with --create-initial-secret, return creds dict."""
    args = [
        "client", "create",
        "--client-id", client_id,
        "--client-name", client_name,
        "--realm-id", realm_id,
        "--scope", scope,
    ]
    for gt in grant_types:
        args.extend(["--grant-types", gt])
    for uri in redirect_uris or []:
        args.extend(["--redirect-uris", uri])
    for uri in logout_redirect_uris or []:
        args.extend(["--logout-redirect-uris", uri])
    if require_pkce:
        args.append("--require-pkce")
    if not require_consent:
        args.extend(["--require-consent", "false"])
    if extra_args:
        args.extend(extra_args)
    args.extend([
        "--create-initial-secret",
        "--output", str(cred_path),
        "--overwrite",
    ])
    r = cli.run(*args)
    assert r.ok, f"client create '{client_id}' failed: {r.stderr or r.stdout}"
    with open(cred_path) as f:
        return json.load(f)


def delete_client(cli: CliHelper, client_id: str) -> None:
    """Delete a client by its client_id string (best-effort)."""
    try:
        internal_id = get_client_internal_id(cli, client_id)
        cli.run("client", "delete", internal_id, "--confirm")
    except Exception:
        pass


def delete_user(cli: CliHelper, username: str) -> None:
    """Delete a user by username (best-effort)."""
    try:
        data = cli.run_json("user", "list", "--search", username)
        items = data.get("items", data) if isinstance(data, dict) else data
        match = [u for u in items if u.get("username") == username]
        if match:
            cli.run("user", "delete", str(match[0]["id"]), "--confirm")
    except Exception:
        pass


def parse_callback(callback_url: str) -> dict[str, str]:
    """Parse a redirect callback URL's query params into a flat dict."""
    parsed = urllib.parse.urlparse(callback_url)
    params = urllib.parse.parse_qs(parsed.query)
    return {k: v[0] for k, v in params.items()}
