"""
Shared pytest fixtures for MrWhoOidc E2E tests (sync Playwright).

Architecture:
  - One Playwright browser launched for the whole session (no per-test launch).
  - Login done ONCE; storage state saved to .auth/state.json.
  - One authenticated BrowserContext shared across all authenticated tests.
  - Each test gets a new Page (tab) from that shared context -- fast.
  - Unauthenticated tests get a fresh BrowserContext+Page, closed after the test.
  - LLM evaluation is fully synchronous; no asyncio needed.
"""

from __future__ import annotations

import json
import os
import ssl
import subprocess
import time
import urllib.request
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Callable, Generator

import pytest
from dotenv import load_dotenv
from playwright.sync_api import Browser, BrowserContext, Page, sync_playwright

try:
    from filelock import FileLock

    _HAS_FILELOCK = True
except ImportError:
    _HAS_FILELOCK = False
_XDIST_WORKER = os.environ.get("PYTEST_XDIST_WORKER")  # None in serial, "gw0"/"gw1"/... in parallel

_ENV_PATH = Path(__file__).parent / ".env"
if not _ENV_PATH.exists():
    _ENV_PATH = Path(__file__).parent.parent / ".env"
load_dotenv(_ENV_PATH, override=False)

from utils.cli_helper import CliHelper
from utils.instruction_loader import InstructionLoader
from utils.llm_evaluator import EvaluationResult, LLMEvaluator
from utils.oidc_client import OidcClient
from utils.report_generator import ReportGenerator
from utils.screenshot_manager import ScreenshotManager

BASE_URL: str = os.getenv("BASE_URL", "https://localhost:8443")
UPSTREAM_BASE_URL: str = os.getenv("UPSTREAM_BASE_URL", "https://localhost:9443")
PORTAL_BASE_URL: str = os.getenv("PORTAL_BASE_URL", "http://localhost:8088")

# Navigation timeouts (ms) — increased from 30s to absorb upstream WebAuth cold-start cost.
POST_LOGIN_NAV_TIMEOUT_MS = 45_000
UPSTREAM_NAV_TIMEOUT_MS = 60_000
LICENSING_ADMIN_URL: str = os.getenv("LICENSING_ADMIN_URL", "https://localhost:7443")
EXAMPLE_RAZORCLIENT_URL: str = os.getenv(
    "EXAMPLE_RAZORCLIENT_URL", "https://localhost:5003"
)
EXAMPLE_TESTAPI_URL: str = os.getenv("EXAMPLE_TESTAPI_URL", "https://localhost:7149")
EXAMPLE_OIDCDEMO_URL: str = os.getenv("EXAMPLE_OIDCDEMO_URL", "https://localhost:5001")
EXAMPLE_REACTCLIENT_URL: str = os.getenv(
    "EXAMPLE_REACTCLIENT_URL", "http://localhost:5173"
)
ADMIN_USERNAME: str = os.getenv("ADMIN_USERNAME", "admin@mrwho.local")
ADMIN_PASSWORD: str = os.getenv("ADMIN_PASSWORD", "E2E-test-password!")
# Must match ADMIN_PASSWORD so the auto-seed creates an admin with this password.
# Also set in reset_database() via subprocess env so docker compose picks it up.
SEED_ADMIN_PASSWORD: str = os.getenv("SEED_ADMIN_PASSWORD", ADMIN_PASSWORD)
_AUTH_SUFFIX = f"-{_XDIST_WORKER}" if _XDIST_WORKER else ""
_AUTH_STATE_FILE: Path = Path(__file__).parent / ".auth" / f"state{_AUTH_SUFFIX}.json"
_AUTH_STATE_UPSTREAM_FILE: Path = Path(__file__).parent / ".auth" / f"upstream-state{_AUTH_SUFFIX}.json"
_RUN_ID: str = datetime.now().strftime("%Y%m%d_%H%M%S")
_LINKED_PROVIDER_NAME = "dev-oidc"
_LINKED_PROVIDER_DISPLAY_NAME = "Dev OIDC"
_LINKED_PROVIDER_BUTTON_BACKGROUND_COLOR = "#111827"
_LINKED_PROVIDER_BUTTON_TEXT_COLOR = "#ffffff"
_LINKED_CLIENT_ID = "e2e-linked-client"
_LINKED_CLIENT_NAME = "E2E Linked Account Client"
_UPSTREAM_PROVIDER_CLIENT_ID = "e2e-dev-oidc-upstream"
_UPSTREAM_PROVIDER_CLIENT_NAME = "E2E Dev OIDC Upstream"
_PLATFORM_PROVIDER_NAME = "platform-dev-oidc"
_PLATFORM_PROVIDER_DISPLAY_NAME = "Platform Dev OIDC"
_PLATFORM_PROVIDER_BUTTON_BACKGROUND_COLOR = "#0f766e"
_PLATFORM_PROVIDER_BUTTON_TEXT_COLOR = "#ffffff"
_PLATFORM_UPSTREAM_PROVIDER_CLIENT_ID = "e2e-platform-dev-oidc-upstream"
_PLATFORM_UPSTREAM_PROVIDER_CLIENT_NAME = "E2E Platform Dev OIDC Upstream"
_LINKED_LOCAL_USERNAME = "e2e-linked-local"
_LINKED_LOCAL_EMAIL = "e2e-linked-local@mrwho.local"
_LINKED_LOCAL_PASSWORD = "LinkedLocal123!"
_LINKED_UPSTREAM_USERNAME = "e2e-linked-upstream"
_LINKED_UPSTREAM_EMAIL = "e2e-linked-upstream@mrwho.local"
_LINKED_UPSTREAM_PASSWORD = "LinkedUpstream123!"


@dataclass(frozen=True)
class LinkedAccountsSetup:
    provider_name: str
    provider_display_name: str
    provider_button_background_color: str
    provider_button_text_color: str
    client_id: str
    local_username: str
    local_email: str
    local_password: str
    upstream_username: str
    upstream_email: str
    upstream_password: str
    upstream_issuer: str


@dataclass(frozen=True)
class PlatformProviderSetup:
    provider_id: str
    provider_name: str
    provider_display_name: str
    provider_button_background_color: str
    provider_button_text_color: str


def _is_headed() -> bool:
    return os.getenv("HEADED", "false").lower() in ("true", "1")


def _slow_mo() -> int:
    return int(os.getenv("SLOW_MO", "0"))


def _wait_for_url(url: str, *, timeout_seconds: int, insecure: bool = False) -> None:
    for _ in range(timeout_seconds):
        context = ssl._create_unverified_context() if insecure else None
        try:
            with urllib.request.urlopen(url, timeout=5, context=context) as response:
                if response.status < 500:
                    return
        except Exception:
            pass
        time.sleep(1)
    raise RuntimeError(
        f"Endpoint did not become ready within {timeout_seconds} s: {url}"
    )


def _tenant_issuer(base_url: str) -> str:
    return f"{base_url.rstrip('/')}/t/default"


def _payload_get(payload: dict[str, Any], key: str) -> Any:
    if key in payload:
        return payload[key]

    pascal_key = f"{key[0].upper()}{key[1:]}"
    if pascal_key in payload:
        return payload[pascal_key]

    raise KeyError(f"Missing '{key}' in payload: {payload}")


def _expect_status(response: Any, label: str, expected_statuses: set[int]) -> None:
    if response.status not in expected_statuses:
        raise RuntimeError(f"{label} failed ({response.status}): {response.text()}")


def _api_get_json(api: Any, url: str, label: str) -> Any:
    response = api.get(url)
    _expect_status(response, label, {200})
    return response.json()


def _api_post_json(
    api: Any,
    url: str,
    payload: dict[str, Any],
    label: str,
    expected_statuses: set[int],
) -> Any:
    response = api.post(
        url,
        data=json.dumps(payload),
        headers={"Content-Type": "application/json"},
    )
    _expect_status(response, label, expected_statuses)
    return response.json()


def _api_put_empty(
    api: Any,
    url: str,
    payload: dict[str, Any],
    label: str,
    expected_statuses: set[int],
) -> None:
    response = api.put(
        url,
        data=json.dumps(payload),
        headers={"Content-Type": "application/json"},
    )
    _expect_status(response, label, expected_statuses)


def _post_json(
    api: Any,
    url: str,
    payload: dict[str, Any],
    label: str,
    expected_statuses: set[int],
) -> None:
    response = api.post(
        url,
        data=json.dumps(payload),
        headers={"Content-Type": "application/json"},
    )
    _expect_status(response, label, expected_statuses)


def _find_realm_id(realms_payload: Any, realm_name: str) -> str:
    realms = realms_payload.get("items", realms_payload) if isinstance(realms_payload, dict) else realms_payload
    for realm in realms:
        if _payload_get(realm, "name") == realm_name:
            return str(_payload_get(realm, "id"))

    raise RuntimeError(f"Realm '{realm_name}' not found in payload: {realms_payload}")


def _save_login_state(
    browser_session: Browser,
    base_url: str,
    state_file: Path,
    username: str,
    password: str,
) -> Path:
    state_file.parent.mkdir(parents=True, exist_ok=True)
    ctx = browser_session.new_context(
        base_url=base_url,
        viewport={"width": 1920, "height": 1080},
        ignore_https_errors=True,
    )

    page = ctx.new_page()
    page.goto(f"{base_url}/login", wait_until="domcontentloaded")
    page.locator("input#Username").fill(username)
    page.locator("input#Password").fill(password)
    page.locator("button[type='submit']").click()
    page.wait_for_url(
        lambda url: "/login" not in url and "/LoginTotp" not in url,
        timeout=POST_LOGIN_NAV_TIMEOUT_MS,
    )
    ctx.storage_state(path=str(state_file))
    ctx.close()
    return state_file


# ---------------------------------------------------------------------------
# Session-scoped fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="session")
def run_id() -> str:
    return _RUN_ID


@pytest.fixture(scope="session")
def base_url() -> str:
    return BASE_URL


@pytest.fixture(scope="session")
def upstream_base_url() -> str:
    return UPSTREAM_BASE_URL


@pytest.fixture(scope="session")
def example_razorclient_url() -> str:
    return EXAMPLE_RAZORCLIENT_URL


@pytest.fixture(scope="session")
def example_testapi_url() -> str:
    return EXAMPLE_TESTAPI_URL


@pytest.fixture(scope="session")
def example_oidcdemo_url() -> str:
    return EXAMPLE_OIDCDEMO_URL


@pytest.fixture(scope="session")
def example_reactclient_url() -> str:
    return EXAMPLE_REACTCLIENT_URL


@pytest.fixture(scope="session")
def screenshot_mgr(run_id: str) -> ScreenshotManager:
    return ScreenshotManager(run_id=run_id)


@pytest.fixture(scope="session")
def llm_evaluator() -> LLMEvaluator:
    return LLMEvaluator()


@pytest.fixture(scope="session")
def instruction_loader() -> InstructionLoader:
    return InstructionLoader()


@pytest.fixture(scope="session")
def report_generator(run_id: str) -> ReportGenerator:
    return ReportGenerator(run_id=run_id)


@pytest.fixture(scope="session", autouse=True)
def finalize_report(report_generator: ReportGenerator):
    yield
    json_path, html_path, plan_path = report_generator.finalize()
    print(f"\n\n{'=' * 60}")
    print(f"  E2E Evaluation Report written:")
    print(f"    JSON : {json_path}")
    print(f"    HTML : {html_path}")
    print(f"    Plan : {plan_path}")
    print(f"{'=' * 60}\n")


@pytest.fixture(scope="session", autouse=True)
def reset_database(request, tmp_path_factory) -> Generator[None, None, None]:
    """Drop and recreate the postgres container + volume before every test session."""

    def _reset() -> None:
        workspace_root = Path(__file__).parent.parent
        compose_file = str(workspace_root / "docker-compose.dev.yml")
        project = "mrwhooidc"
        base_cmd = [
            "docker",
            "compose",
            "-f",
            compose_file,
            "-p",
            project,
        ]

        # Stop and remove app containers that depend on the seeded database.
        subprocess.run(
            [
                *base_cmd,
                "rm",
                "-sf",
                "reactclient",
                "oidcdemo",
                "razorclient",
                "testapi",
                "webauth",
                "webauth-upstream",
                "postgres",
                "postgres-upstream",
                "redis",
                "redis-upstream",
            ],
            check=False,
        )

        # Remove the data volumes so both stacks start from a clean state.
        subprocess.run(["docker", "volume", "rm", f"{project}_postgres-data"], check=False)
        subprocess.run(["docker", "volume", "rm", f"{project}_postgres-upstream-data"], check=False)
        subprocess.run(["docker", "volume", "rm", f"{project}_redis-data"], check=False)
        subprocess.run(["docker", "volume", "rm", f"{project}_redis-upstream-data"], check=False)

        # Start fresh databases for both authorities.
        subprocess.run([*base_cmd, "up", "-d", "postgres", "postgres-upstream"], check=True)

        # Wait up to 60 s for both databases to become healthy.
        for container in (f"{project}-postgres-1", f"{project}-postgres-upstream-1"):
            for _ in range(60):
                health = subprocess.run(
                    ["docker", "inspect", "--format={{.State.Health.Status}}", container],
                    capture_output=True,
                    text=True,
                )
                if health.stdout.strip() == "healthy":
                    break
                time.sleep(1)
            else:
                raise RuntimeError(f"{container} did not become healthy within 60 s")

        # Start both authorities from the current source — each runs EF migrations on startup.
        # Pass SEED_ADMIN_PASSWORD so the auto-seed creates a known admin password.
        webauth_env = os.environ.copy()
        webauth_env["SEED_ADMIN_PASSWORD"] = SEED_ADMIN_PASSWORD
        subprocess.run(
            [*base_cmd, "up", "-d", "--build", "webauth", "webauth-upstream"],
            check=True,
            env=webauth_env,
        )

        ready_url = f"{BASE_URL}/t/default/.well-known/openid-configuration"
        _wait_for_url(ready_url, timeout_seconds=120, insecure=True)
        upstream_ready_url = f"{UPSTREAM_BASE_URL}/t/default/.well-known/openid-configuration"
        _wait_for_url(upstream_ready_url, timeout_seconds=120, insecure=True)

        # Start the example applications from the current source.
        subprocess.run(
            [
                *base_cmd,
                "up",
                "-d",
                "--build",
                "testapi",
                "razorclient",
                "oidcdemo",
                "reactclient",
            ],
            check=True,
        )
        _wait_for_url(f"{EXAMPLE_TESTAPI_URL}/health", timeout_seconds=90, insecure=True)
        _wait_for_url(
            f"{EXAMPLE_RAZORCLIENT_URL}/health", timeout_seconds=90, insecure=True
        )
        _wait_for_url(f"{EXAMPLE_OIDCDEMO_URL}/health", timeout_seconds=90, insecure=True)
        _wait_for_url(
            f"{EXAMPLE_REACTCLIENT_URL}/health", timeout_seconds=90, insecure=True
        )

        # Clear any stale auth state so login is performed against the fresh DB
        if _AUTH_STATE_FILE.exists():
            _AUTH_STATE_FILE.unlink()
        if _AUTH_STATE_UPSTREAM_FILE.exists():
            _AUTH_STATE_UPSTREAM_FILE.unlink()

    if _XDIST_WORKER and _HAS_FILELOCK:
        # Parallel mode: only the first worker resets; the rest wait until it is ready.
        lock = tmp_path_factory.getbasetemp().parent / "e2e_reset.lock"
        ready = tmp_path_factory.getbasetemp().parent / "e2e_reset.ready"
        with FileLock(str(lock)):
            if not ready.exists():
                _reset()
                ready.touch()
        yield
        # Parallel: do NOT tear down the stack (other workers may still be running).
        return

    # Serial: original behavior unchanged.
    _reset()
    yield


@pytest.fixture(scope="session")
def portal_base_url() -> str:
    return PORTAL_BASE_URL


@pytest.fixture(scope="session")
def licensing_admin_url() -> str:
    return LICENSING_ADMIN_URL


@pytest.fixture(scope="session")
def browser_session() -> Generator[Browser, None, None]:
    """Single Playwright browser for the whole session."""
    with sync_playwright() as pw:
        browser = pw.chromium.launch(
            headless=not _is_headed(),
            slow_mo=_slow_mo(),
            args=["--ignore-certificate-errors"],
        )
        yield browser
        browser.close()


@pytest.fixture(scope="session")
def auth_state_file(browser_session: Browser, reset_database: None) -> Path:
    """Log in once and save storage state."""
    return _save_login_state(
        browser_session,
        BASE_URL,
        _AUTH_STATE_FILE,
        ADMIN_USERNAME,
        ADMIN_PASSWORD,
    )


@pytest.fixture(scope="session")
def upstream_auth_state_file(browser_session: Browser, reset_database: None) -> Path:
    """Log in once to the upstream authority and save storage state."""
    return _save_login_state(
        browser_session,
        UPSTREAM_BASE_URL,
        _AUTH_STATE_UPSTREAM_FILE,
        ADMIN_USERNAME,
        ADMIN_PASSWORD,
    )


@pytest.fixture(scope="session")
def authenticated_context(
    browser_session: Browser, auth_state_file: Path
) -> Generator[BrowserContext, None, None]:
    """Shared authenticated context for the whole session."""
    ctx = browser_session.new_context(
        base_url=BASE_URL,
        viewport={"width": 1920, "height": 1080},
        ignore_https_errors=True,
        storage_state=str(auth_state_file),
    )
    yield ctx
    ctx.close()


@pytest.fixture(scope="session")
def upstream_authenticated_context(
    browser_session: Browser, upstream_auth_state_file: Path
) -> Generator[BrowserContext, None, None]:
    """Shared authenticated upstream-admin context for the whole session."""
    ctx = browser_session.new_context(
        base_url=UPSTREAM_BASE_URL,
        viewport={"width": 1920, "height": 1080},
        ignore_https_errors=True,
        storage_state=str(upstream_auth_state_file),
    )
    yield ctx
    ctx.close()


@pytest.fixture(scope="session")
def linked_accounts_setup(
    authenticated_context: BrowserContext,
    upstream_authenticated_context: BrowserContext,
) -> LinkedAccountsSetup:
    main_api = authenticated_context.request
    upstream_api = upstream_authenticated_context.request

    main_realm_id = _find_realm_id(
        _api_get_json(main_api, f"{BASE_URL}/admin/api/realms", "Load main realms"),
        "default",
    )
    upstream_realm_id = _find_realm_id(
        _api_get_json(
            upstream_api,
            f"{UPSTREAM_BASE_URL}/admin/api/realms",
            "Load upstream realms",
        ),
        "default",
    )

    upstream_client = _api_post_json(
        upstream_api,
        f"{UPSTREAM_BASE_URL}/admin/api/clients",
        {
            "clientId": _UPSTREAM_PROVIDER_CLIENT_ID,
            "clientName": _UPSTREAM_PROVIDER_CLIENT_NAME,
            "realmId": upstream_realm_id,
            "requirePkce": True,
            "requireConsent": False,
            "autoApprovalMode": "All",
            "scope": "openid profile email",
            "grantTypes": ["authorization_code"],
            "allowedLoginRedirectUris": [
                f"{_tenant_issuer(BASE_URL)}/auth/external/callback"
            ],
            "allowedLogoutRedirectUris": [
                f"{_tenant_issuer(BASE_URL)}/logout/federated-callback"
            ],
            "createInitialSecret": True,
        },
        "Create upstream external-login client",
        {201},
    )
    upstream_client_id = str(_payload_get(upstream_client, "id"))
    upstream_client_secret = str(_payload_get(upstream_client, "initialSecret"))

    linked_client = _api_post_json(
        main_api,
        f"{BASE_URL}/admin/api/clients",
        {
            "clientId": _LINKED_CLIENT_ID,
            "clientName": _LINKED_CLIENT_NAME,
            "realmId": main_realm_id,
            "requirePkce": True,
            "requireConsent": False,
            "scope": "openid profile email",
            "grantTypes": ["authorization_code"],
            "allowedLoginRedirectUris": ["https://e2e-linked.test/callback"],
            "allowedLogoutRedirectUris": ["https://e2e-linked.test/signout"],
            "createInitialSecret": False,
        },
        "Create main linked-account test client",
        {201},
    )
    linked_client_id = str(_payload_get(linked_client, "id"))

    _api_put_empty(
        main_api,
        f"{BASE_URL}/admin/api/clients/{linked_client_id}",
        {
            "allowLocalLogin": False,
            "allowExternalIdp": True,
            "requirePkce": True,
            "requireConsent": False,
        },
        "Configure main linked-account test client",
        {204},
    )

    provider = _api_post_json(
        main_api,
        f"{BASE_URL}/admin/api/providers",
        {
            "name": _LINKED_PROVIDER_NAME,
            "displayName": _LINKED_PROVIDER_DISPLAY_NAME,
            "type": 0,
            "enabled": True,
            "isDefault": False,
            "allowRegistration": True,
            "logoUrl": None,
            "buttonBackgroundColor": _LINKED_PROVIDER_BUTTON_BACKGROUND_COLOR,
            "buttonTextColor": _LINKED_PROVIDER_BUTTON_TEXT_COLOR,
            "sortOrder": 0,
            "configJson": json.dumps(
                {
                    "Authority": _tenant_issuer(UPSTREAM_BASE_URL),
                    "ClientId": _UPSTREAM_PROVIDER_CLIENT_ID,
                    "ClientSecret": upstream_client_secret,
                    "ResponseType": "code",
                    "Scopes": ["openid", "profile", "email"],
                    "UsePKCE": True,
                    "UseJAR": False,
                    "UsePAR": False,
                    "ClockSkewSeconds": 120,
                    "TokenValidation": {
                        "ValidateIssuer": True,
                        "ValidateAudience": False,
                        "ValidateLifetime": True,
                    },
                    "BackChannelLogout": True,
                    "ExtraAuthParams": {},
                }
            ),
        },
        "Create main chained Dev OIDC provider",
        {201},
    )
    provider_id = str(_payload_get(provider, "id"))

    for order, (external_claim, local_claim) in enumerate(
        (("sub", "sub"), ("email", "email"), ("name", "name"))
    ):
        _post_json(
            main_api,
            f"{BASE_URL}/admin/api/providers/{provider_id}/claim-mappings",
            {
                "externalClaim": external_claim,
                "localClaim": local_claim,
                "transform": None,
                "order": order,
            },
            f"Create {external_claim} claim mapping",
            {201},
        )

    _post_json(
        main_api,
        f"{BASE_URL}/admin/api/clients/{linked_client_id}/providers",
        {
            "identityProviderId": provider_id,
            "enabled": True,
            "isDefaultForClient": True,
            "autoRedirectIfSingle": False,
            "requiredAcr": None,
            "order": 0,
        },
        "Map Dev OIDC provider to linked-account test client",
        {200},
    )

    main_user = _api_post_json(
        main_api,
        f"{BASE_URL}/admin/api/users",
        {
            "username": _LINKED_LOCAL_USERNAME,
            "email": _LINKED_LOCAL_EMAIL,
            "name": "E2E Linked Local User",
            "password": _LINKED_LOCAL_PASSWORD,
        },
        "Create local linked-account test user",
        {201},
    )

    upstream_user = _api_post_json(
        upstream_api,
        f"{UPSTREAM_BASE_URL}/admin/api/users",
        {
            "username": _LINKED_UPSTREAM_USERNAME,
            "email": _LINKED_UPSTREAM_EMAIL,
            "name": "E2E Linked Upstream User",
            "password": _LINKED_UPSTREAM_PASSWORD,
        },
        "Create upstream linked-account test user",
        {201},
    )
    upstream_user_id = str(_payload_get(upstream_user, "id"))

    _post_json(
        upstream_api,
        f"{UPSTREAM_BASE_URL}/admin/api/users/{upstream_user_id}/clients",
        {"clientId": upstream_client_id},
        "Assign upstream linked-account test user to upstream client",
        {201},
    )

    return LinkedAccountsSetup(
        provider_name=_LINKED_PROVIDER_NAME,
        provider_display_name=_LINKED_PROVIDER_DISPLAY_NAME,
        provider_button_background_color=_LINKED_PROVIDER_BUTTON_BACKGROUND_COLOR,
        provider_button_text_color=_LINKED_PROVIDER_BUTTON_TEXT_COLOR,
        client_id=_LINKED_CLIENT_ID,
        local_username=str(_payload_get(main_user, "username")),
        local_email=str(_payload_get(main_user, "email")),
        local_password=_LINKED_LOCAL_PASSWORD,
        upstream_username=str(_payload_get(upstream_user, "username")),
        upstream_email=str(_payload_get(upstream_user, "email")),
        upstream_password=_LINKED_UPSTREAM_PASSWORD,
        upstream_issuer=_tenant_issuer(UPSTREAM_BASE_URL),
    )


@pytest.fixture(scope="session")
def platform_provider_setup(
    authenticated_context: BrowserContext,
    upstream_authenticated_context: BrowserContext,
) -> PlatformProviderSetup:
    main_api = authenticated_context.request
    upstream_api = upstream_authenticated_context.request

    upstream_realm_id = _find_realm_id(
        _api_get_json(
            upstream_api,
            f"{UPSTREAM_BASE_URL}/admin/api/realms",
            "Load upstream realms for platform provider",
        ),
        "default",
    )

    upstream_client = _api_post_json(
        upstream_api,
        f"{UPSTREAM_BASE_URL}/admin/api/clients",
        {
            "clientId": _PLATFORM_UPSTREAM_PROVIDER_CLIENT_ID,
            "clientName": _PLATFORM_UPSTREAM_PROVIDER_CLIENT_NAME,
            "realmId": upstream_realm_id,
            "requirePkce": True,
            "requireConsent": False,
            "autoApprovalMode": "All",
            "scope": "openid profile email",
            "grantTypes": ["authorization_code"],
            # The local OP is tenant-scoped: its /auth/external/callback runs under
            # /t/{slug}/... so the registered redirect_uri on the upstream client
            # must match the tenant-prefixed path exactly.
            "allowedLoginRedirectUris": [
                f"{BASE_URL}/t/default/auth/external/callback",
                f"{BASE_URL}/auth/external/callback",
            ],
            "allowedLogoutRedirectUris": [
                f"{BASE_URL}/t/default/logout/federated-callback",
                f"{BASE_URL}/logout/federated-callback",
            ],
            "createInitialSecret": True,
        },
        "Create upstream platform external-login client",
        {201},
    )
    upstream_client_id = str(_payload_get(upstream_client, "id"))
    upstream_client_secret = str(_payload_get(upstream_client, "initialSecret"))

    users_payload = _api_get_json(
        upstream_api,
        f"{UPSTREAM_BASE_URL}/admin/api/users?search={ADMIN_USERNAME}",
        "Find upstream admin user for platform provider",
    )
    users = users_payload.get("items", users_payload)
    admin_matches = [u for u in users if str(_payload_get(u, "email")).lower() == ADMIN_USERNAME.lower()]
    if not admin_matches:
        raise RuntimeError(f"Upstream admin user {ADMIN_USERNAME} not found: {users_payload}")
    upstream_admin_user_id = str(_payload_get(admin_matches[0], "id"))

    _post_json(
        upstream_api,
        f"{UPSTREAM_BASE_URL}/admin/api/users/{upstream_admin_user_id}/clients",
        {"clientId": upstream_client_id},
        "Assign upstream admin to platform external-login client",
        {201, 409},
    )

    provider = _api_post_json(
        main_api,
        f"{BASE_URL}/platform-admin/api/providers",
        {
            "name": _PLATFORM_PROVIDER_NAME,
            "displayName": _PLATFORM_PROVIDER_DISPLAY_NAME,
            "type": 0,
            "enabled": True,
            "isDefault": True,
            "allowRegistration": False,
            "buttonBackgroundColor": _PLATFORM_PROVIDER_BUTTON_BACKGROUND_COLOR,
            "buttonTextColor": _PLATFORM_PROVIDER_BUTTON_TEXT_COLOR,
            "sortOrder": 0,
            "configJson": json.dumps(
                {
                    "Authority": _tenant_issuer(UPSTREAM_BASE_URL),
                    "ClientId": _PLATFORM_UPSTREAM_PROVIDER_CLIENT_ID,
                    "ClientSecret": upstream_client_secret,
                    "ResponseType": "code",
                    "Scopes": ["openid", "profile", "email"],
                    "UsePKCE": True,
                    "UseJAR": False,
                    "UsePAR": False,
                    "ClockSkewSeconds": 120,
                    "TokenValidation": {
                        "ValidateIssuer": True,
                        "ValidateAudience": False,
                        "ValidateLifetime": True,
                    },
                    "BackChannelLogout": True,
                    "ExtraAuthParams": {},
                }
            ),
        },
        "Create platform Dev OIDC provider",
        {201},
    )

    return PlatformProviderSetup(
        provider_id=str(_payload_get(provider, "id")),
        provider_name=_PLATFORM_PROVIDER_NAME,
        provider_display_name=_PLATFORM_PROVIDER_DISPLAY_NAME,
        provider_button_background_color=_PLATFORM_PROVIDER_BUTTON_BACKGROUND_COLOR,
        provider_button_text_color=_PLATFORM_PROVIDER_BUTTON_TEXT_COLOR,
    )


# ---------------------------------------------------------------------------
# Function-scoped page fixtures
# ---------------------------------------------------------------------------


@pytest.fixture
def page(browser_session: Browser) -> Generator[Page, None, None]:
    """Fresh unauthenticated context+page per test."""
    ctx = browser_session.new_context(
        base_url=BASE_URL,
        viewport={"width": 1920, "height": 1080},
        ignore_https_errors=True,
    )
    p = ctx.new_page()
    yield p
    ctx.close()


@pytest.fixture
def authenticated_page(
    authenticated_context: BrowserContext,
) -> Generator[Page, None, None]:
    """New tab in the shared authenticated context (fast -- no browser re-launch)."""
    p = authenticated_context.new_page()
    yield p
    p.close()


# ---------------------------------------------------------------------------
# Evaluation helper
# ---------------------------------------------------------------------------


@pytest.fixture
def record_evaluation(
    screenshot_mgr: ScreenshotManager,
    llm_evaluator: LLMEvaluator,
    instruction_loader: InstructionLoader,
    report_generator: ReportGenerator,
) -> Callable[..., EvaluationResult]:
    """Returns a sync callable: ``record_evaluation(page, route[, label])``."""

    def _impl(page: Page, route: str, label: str | None = None) -> EvaluationResult:
        screenshot_path = screenshot_mgr.capture(page, route, label=label)
        page_instructions = instruction_loader.load(route)
        instructions_content = page_instructions.content if page_instructions else None
        result = llm_evaluator.evaluate(
            screenshot_path=screenshot_path,
            route=route,
            instructions_content=instructions_content,
        )
        report_generator.add(result)
        return result

    return _impl


# ---------------------------------------------------------------------------
# CLI fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="session")
def cli_helper() -> CliHelper:
    """Session-scoped CliHelper pointing at the running WebAuth instance."""
    return CliHelper(server_url=f"{BASE_URL}/t/default")


@pytest.fixture(scope="session")
def cli_logged_in(
    cli_helper: CliHelper,
    authenticated_context: BrowserContext,
    auth_state_file: Path,
) -> CliHelper:
    """
    Enable CLI access for the default tenant, perform device-code login via
    browser approval, and return the ready-to-use CliHelper.

    This fixture is session-scoped so the login happens only once.
    """
    import logging

    log = logging.getLogger("cli_login")

    # Step 1: Enable CLI access via the admin settings page
    page = authenticated_context.new_page()
    try:
        page.goto(f"{cli_helper.server_url}/admin/settings", wait_until="domcontentloaded")
        cli_checkbox = page.locator("#cliAccessEnabled")
        if cli_checkbox.count() > 0 and cli_checkbox.is_visible():
            if not cli_checkbox.is_checked():
                cli_checkbox.check()
                # Submit the settings form
                btn = page.locator("button.btn-primary[type='submit']").first
                if btn.count() > 0 and btn.is_visible():
                    btn.click()
                    page.wait_for_load_state("domcontentloaded")
                    log.info("CLI access enabled via admin settings")
            else:
                log.info("CLI access already enabled")
        else:
            log.warning("CLI access checkbox not found on settings page")
    finally:
        page.close()

    # Step 2: Start the CLI login process (device-code flow)
    proc = cli_helper.start_login()
    verification_url, user_code = CliHelper.parse_device_login_output(
        proc, read_timeout=30
    )

    if not verification_url:
        # Dump what we got for debugging
        proc.kill()
        try:
            remaining = proc.communicate(timeout=5)[0] if proc.stdout else ""
        except subprocess.TimeoutExpired:
            remaining = ""
        raise RuntimeError(
            f"Failed to parse device login output. "
            f"URL={verification_url}, code={user_code}, remaining={remaining}"
        )

    log.info("Device code flow: URL=%s, code=%s", verification_url, user_code)

    # Step 3: Approve the device in the browser
    approval_page = authenticated_context.new_page()
    try:
        approval_page.goto(verification_url, wait_until="domcontentloaded")

        # If we landed on the confirmation page, click Authorize
        approve_btn = approval_page.locator("button[value='approve']").first
        if approve_btn.count() > 0 and approve_btn.is_visible():
            approve_btn.click()
            approval_page.wait_for_load_state("domcontentloaded")
            log.info("Device authorization approved")
        else:
            # Maybe user_code input is shown — enter it manually
            code_input = approval_page.locator("#user_code").first
            if code_input.count() > 0 and code_input.is_visible():
                code_input.fill(user_code or "")
                approval_page.locator("button[type='submit']").first.click()
                approval_page.wait_for_load_state("domcontentloaded")
                # Now the confirmation page should appear
                approve_btn = approval_page.locator("button[value='approve']").first
                if approve_btn.count() > 0 and approve_btn.is_visible():
                    approve_btn.click()
                    approval_page.wait_for_load_state("domcontentloaded")
                    log.info("Device authorization approved (after code entry)")
    finally:
        approval_page.close()

    # Step 4: Wait for the CLI login to complete
    try:
        proc.wait(timeout=30)
    except subprocess.TimeoutExpired:
        proc.kill()
        raise RuntimeError("CLI login did not complete within 30 s after approval")

    if proc.returncode != 0:
        raise RuntimeError(f"CLI login failed with exit code {proc.returncode}")

    # Verify login succeeded
    result = cli_helper.run("profile", "show")
    if not result.ok:
        raise RuntimeError(f"CLI profile show failed after login: {result.stderr}")

    log.info("CLI login successful")
    return cli_helper


@pytest.fixture(scope="session")
def oidc_client(reset_database: None) -> OidcClient:
    """Session-scoped OidcClient pointing at the default tenant, with discovery cached."""
    client = OidcClient(f"{BASE_URL}/t/default")
    client.discover()
    return client


@pytest.fixture(scope="session")
def install_enterprise_license(
    authenticated_context: BrowserContext,
) -> str:
    """
    Generate an Enterprise+ tenant-scoped license and install it via the admin API.

    Returns the signed JWT string.  Opt-in fixture — only tests that need
    license-gated features should request it.
    """
    import logging

    from utils.license_generator import LicenseGenerator, ALL_FEATURES

    log = logging.getLogger("license_install")

    # Use tenant scope so the tenant-admin can install it.
    # Exclude multi_tenancy which is a platform-only feature.
    tenant_features = [f for f in ALL_FEATURES if f != "multi_tenancy"]

    generator = LicenseGenerator()
    license_jwt = generator.generate(
        tier="enterprise+",
        organization="E2E Test Organization",
        scope="tenant",
        deployment_mode="",
        features=tenant_features,
        valid_seconds=86400,  # 24h
    )
    log.info("Generated Enterprise+ tenant-scoped test license")

    # Use Playwright's API request context which shares the authenticated cookies.
    api = authenticated_context.request
    response = api.post(
        f"{BASE_URL}/admin/api/license",
        data={"licenseKey": license_jwt, "notes": "E2E test license"},
        headers={"Content-Type": "application/json"},
    )

    if response.status != 200:
        body = response.text()
        raise RuntimeError(f"License install failed ({response.status}): {body}")

    log.info("Enterprise+ license installed successfully")
    return license_jwt
