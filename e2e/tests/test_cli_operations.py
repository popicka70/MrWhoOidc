"""
CLI E2E tests — exercises mrwho-cli against a running MrWhoOidc instance.

Test order within each class is significant: read-only tests run first,
then CRUD (create → get → update → list → delete), and finally OBO / M2M
configuration.  All test data uses the ``e2e-cli`` prefix so it is easy
to identify and clean up.

Requires:
  - The app running at BASE_URL (via docker-compose.dev.yml).
  - ``mrwho-cli`` installed globally (``bash deploy-mrwho-cli.sh``).
  - CLI access enabled and device-code login completed (handled by the
    ``cli_logged_in`` session fixture in conftest.py).
"""

from __future__ import annotations

import json
import time
from pathlib import Path

import pytest

from utils.cli_helper import CliHelper

E2E_PREFIX = "e2e-cli"
_RUN_SUFFIX = str(int(time.time()))[-6:]


# ═══════════════════════════════════════════════════════════════════════════
# Read-only commands (listing / discovery / profile)
# ═══════════════════════════════════════════════════════════════════════════


class TestCliReadOnly:
    """Commands that only read data — safe to run on any instance."""

    def test_profile_show(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("profile", "show")
        assert r.ok, f"profile show failed: {r.stderr}"
        assert "ServerUrl" in r.stdout or "server" in r.stdout.lower()

    def test_profile_list(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("profile", "list")
        assert r.ok, f"profile list failed: {r.stderr}"

    def test_discovery(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("discovery")
        assert r.ok, f"discovery failed: {r.stderr}"
        assert "Issuer" in r.stdout or "issuer" in r.stdout.lower()

    def test_discovery_json(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("discovery")
        assert "Issuer" in data or "issuer" in str(data).lower()

    def test_tenant_list(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("tenant", "list")
        assert r.ok, f"tenant list failed: {r.stderr}"
        # Fresh DB always has at least the "default" tenant
        assert "default" in r.stdout.lower()

    def test_tenant_list_json(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("tenant", "list")
        assert isinstance(data, list)
        slugs = [t.get("slug", "") for t in data]
        assert "default" in slugs

    def test_realm_list(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("realm", "list")
        assert r.ok, f"realm list failed: {r.stderr}"
        # Fresh DB should have a "default" realm
        assert "default" in r.stdout.lower()

    def test_realm_list_json(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("realm", "list")
        assert isinstance(data, list)
        names = [r.get("name", "") for r in data]
        assert "default" in names

    def test_client_list(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("client", "list")
        assert r.ok, f"client list failed: {r.stderr}"
        # The CLI client itself should be listed
        assert "mrwho-cli" in r.stdout.lower()

    def test_client_list_json(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("client", "list")
        assert isinstance(data, list)
        client_ids = [c.get("clientId", "") for c in data]
        assert any("mrwho-cli" in cid for cid in client_ids)

    def test_scope_list(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("scope", "list")
        assert r.ok, f"scope list failed: {r.stderr}"
        # Standard OIDC scopes should exist
        assert "openid" in r.stdout.lower()

    def test_scope_list_json(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("scope", "list")
        assert isinstance(data, list)
        names = [s.get("name", "") for s in data]
        assert "openid" in names

    def test_user_list(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("user", "list")
        assert r.ok, f"user list failed: {r.stderr}"
        # The admin user should be listed
        assert "admin" in r.stdout.lower()

    def test_user_list_json(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("user", "list")
        # user list returns { items: [...], total: N }
        items = data.get("items", data) if isinstance(data, dict) else data
        assert isinstance(items, list)
        assert len(items) > 0


# ═══════════════════════════════════════════════════════════════════════════
# Realm CRUD
# ═══════════════════════════════════════════════════════════════════════════


class TestCliRealmCrud:
    """Create → get → update → list → delete a realm via CLI."""

    _realm_name = f"{E2E_PREFIX}-realm-{_RUN_SUFFIX}"
    _realm_display = "E2E CLI Test Realm"
    _realm_id: str | None = None

    def test_01_create_realm(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run(
            "realm", "create",
            "--name", self._realm_name,
            "--display-name", self._realm_display,
        )
        assert r.ok, f"realm create failed: {r.stderr or r.stdout}"
        assert "created" in r.stdout.lower() or self._realm_name in r.stdout

    def test_02_realm_appears_in_list(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == self._realm_name]
        assert len(match) == 1, f"Realm '{self._realm_name}' not found in list"
        TestCliRealmCrud._realm_id = str(match[0]["id"])

    def test_03_get_realm(self, cli_logged_in: CliHelper):
        if not self._realm_id:
            pytest.skip("Realm ID not captured from previous test")
        r = cli_logged_in.run("realm", "get", self._realm_id)
        assert r.ok, f"realm get failed: {r.stderr}"
        assert self._realm_name in r.stdout

    def test_04_update_realm(self, cli_logged_in: CliHelper):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        r = cli_logged_in.run(
            "realm", "update", self._realm_id,
            "--display-name", "E2E CLI Realm (Updated)",
        )
        assert r.ok, f"realm update failed: {r.stderr or r.stdout}"

    def test_05_verify_update(self, cli_logged_in: CliHelper):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        r = cli_logged_in.run("realm", "get", self._realm_id)
        assert r.ok
        assert "Updated" in r.stdout

    # Deletion is at the end of TestCliCleanup (after clients removed)


# ═══════════════════════════════════════════════════════════════════════════
# Scope CRUD
# ═══════════════════════════════════════════════════════════════════════════


class TestCliScopeCrud:
    """Create → update → list → delete a custom scope via CLI."""

    _scope_name = f"{E2E_PREFIX}.read.{_RUN_SUFFIX}"
    _scope_desc = "E2E CLI read scope"

    def test_01_create_scope(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run(
            "scope", "create",
            "--name", self._scope_name,
            "--description", self._scope_desc,
        )
        assert r.ok, f"scope create failed: {r.stderr or r.stdout}"
        assert "created" in r.stdout.lower() or self._scope_name in r.stdout

    def test_02_scope_in_list(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("scope", "list")
        names = [s.get("name", "") for s in data]
        assert self._scope_name in names

    def test_03_update_scope(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run(
            "scope", "update", self._scope_name,
            "--description", "E2E CLI read scope (updated)",
        )
        assert r.ok, f"scope update failed: {r.stderr or r.stdout}"

    def test_04_delete_scope(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("scope", "delete", self._scope_name, "--confirm")
        assert r.ok, f"scope delete failed: {r.stderr or r.stdout}"

    def test_05_scope_gone(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("scope", "list")
        names = [s.get("name", "") for s in data]
        assert self._scope_name not in names


# ═══════════════════════════════════════════════════════════════════════════
# User CRUD
# ═══════════════════════════════════════════════════════════════════════════


class TestCliUserCrud:
    """Create → get → list → delete a user via CLI."""

    _username = f"{E2E_PREFIX}-user-{_RUN_SUFFIX}"
    _email = f"{E2E_PREFIX}-{_RUN_SUFFIX}@test.local"
    _user_id: str | None = None

    def test_01_create_user(self, cli_logged_in: CliHelper, tmp_path: Path):
        cred_file = tmp_path / "user-creds.json"
        r = cli_logged_in.run(
            "user", "create",
            "--username", self._username,
            "--email", self._email,
            "--name", "E2E CLI User",
            "--password", "CliTest_Pass_123!",
            "--output", str(cred_file),
            "--overwrite",
        )
        assert r.ok, f"user create failed: {r.stderr or r.stdout}"
        assert "created" in r.stdout.lower() or self._username in r.stdout

    def test_02_user_in_list(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("user", "list", "--search", self._username)
        items = data.get("items", data) if isinstance(data, dict) else data
        match = [u for u in items if u.get("username") == self._username]
        assert len(match) == 1, f"User '{self._username}' not found"
        TestCliUserCrud._user_id = str(match[0]["id"])

    def test_03_get_user(self, cli_logged_in: CliHelper):
        if not self._user_id:
            pytest.skip("User ID not captured")
        r = cli_logged_in.run("user", "get", self._user_id)
        assert r.ok, f"user get failed: {r.stderr}"
        assert self._username in r.stdout

    def test_04_delete_user(self, cli_logged_in: CliHelper):
        if not self._user_id:
            pytest.skip("User ID not captured")
        r = cli_logged_in.run("user", "delete", self._user_id, "--confirm")
        assert r.ok, f"user delete failed: {r.stderr or r.stdout}"

    def test_05_user_gone(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("user", "list", "--search", self._username)
        items = data.get("items", data) if isinstance(data, dict) else data
        match = [u for u in items if u.get("username") == self._username]
        assert len(match) == 0, f"User '{self._username}' still present after delete"


# ═══════════════════════════════════════════════════════════════════════════
# Client CRUD
# ═══════════════════════════════════════════════════════════════════════════


class TestCliClientCrud:
    """Create → get → list → delete a client via CLI."""

    _client_id_str = f"{E2E_PREFIX}-cid-{_RUN_SUFFIX}"
    _client_name = f"{E2E_PREFIX}-client-{_RUN_SUFFIX}"
    _internal_id: str | None = None

    def test_01_get_realm_id(self, cli_logged_in: CliHelper):
        """Capture the default realm GUID needed for client creation."""
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == "default"]
        assert len(match) == 1, "Default realm not found"
        TestCliClientCrud._realm_id = str(match[0]["id"])

    def test_02_create_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not getattr(self, "_realm_id", None):
            pytest.skip("Default realm ID not captured")
        cred_file = tmp_path / "client-creds.json"
        r = cli_logged_in.run(
            "client", "create",
            "--client-id", self._client_id_str,
            "--client-name", self._client_name,
            "--realm-id", self._realm_id,
            "--scope", "openid profile",
            "--grant-types", "authorization_code",
            "--grant-types", "refresh_token",
            "--redirect-uris", "https://e2e-test.local/callback",
            "--require-pkce",
            "--create-initial-secret",
            "--output", str(cred_file),
            "--overwrite",
        )
        assert r.ok, f"client create failed: {r.stderr or r.stdout}"
        assert "created" in r.stdout.lower() or self._client_id_str in r.stdout

    def test_03_client_in_list(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("client", "list")
        match = [c for c in data if c.get("clientId") == self._client_id_str]
        assert len(match) == 1, f"Client '{self._client_id_str}' not found"
        TestCliClientCrud._internal_id = str(match[0]["id"])

    def test_04_get_client(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client internal ID not captured")
        r = cli_logged_in.run("client", "get", self._internal_id)
        assert r.ok, f"client get failed: {r.stderr}"
        assert self._client_id_str in r.stdout

    def test_05_delete_client(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client internal ID not captured")
        r = cli_logged_in.run("client", "delete", self._internal_id, "--confirm")
        assert r.ok, f"client delete failed: {r.stderr or r.stdout}"

    def test_06_client_gone(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("client", "list")
        match = [c for c in data if c.get("clientId") == self._client_id_str]
        assert len(match) == 0, f"Client '{self._client_id_str}' still present"


# ═══════════════════════════════════════════════════════════════════════════
# M2M (client_credentials) client setup via CLI
# ═══════════════════════════════════════════════════════════════════════════


class TestCliM2MSetup:
    """
    Create a machine-to-machine client using client_credentials grant,
    verify it in the listing, then clean up.
    """

    _m2m_cid = f"{E2E_PREFIX}-m2m-{_RUN_SUFFIX}"
    _m2m_name = f"E2E M2M Client {_RUN_SUFFIX}"
    _internal_id: str | None = None

    def test_01_get_realm_id(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == "default"]
        assert len(match) == 1
        TestCliM2MSetup._realm_id = str(match[0]["id"])

    def test_02_create_m2m_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not getattr(self, "_realm_id", None):
            pytest.skip("Default realm ID not captured")
        cred_file = tmp_path / "m2m-creds.json"
        r = cli_logged_in.run(
            "client", "create",
            "--client-id", self._m2m_cid,
            "--client-name", self._m2m_name,
            "--realm-id", self._realm_id,
            "--scope", "openid profile",
            "--grant-types", "client_credentials",
            "--create-initial-secret",
            "--output", str(cred_file),
            "--overwrite",
        )
        assert r.ok, f"M2M client create failed: {r.stderr or r.stdout}"
        assert "created" in r.stdout.lower() or self._m2m_cid in r.stdout

    def test_03_m2m_client_in_list(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("client", "list")
        match = [c for c in data if c.get("clientId") == self._m2m_cid]
        assert len(match) == 1, f"M2M client '{self._m2m_cid}' not found"
        TestCliM2MSetup._internal_id = str(match[0]["id"])

    def test_04_get_m2m_client(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("M2M internal ID not captured")
        r = cli_logged_in.run("client", "get", self._internal_id)
        assert r.ok, f"M2M client get failed: {r.stderr}"
        assert self._m2m_cid in r.stdout

    def test_05_delete_m2m_client(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("M2M internal ID not captured")
        r = cli_logged_in.run("client", "delete", self._internal_id, "--confirm")
        assert r.ok, f"M2M client delete failed: {r.stderr or r.stdout}"


# ═══════════════════════════════════════════════════════════════════════════
# OBO (On-Behalf-Of / Token Exchange) scenario via CLI
#
# Creates a front-end client and a back-end API client.  The back-end
# client is configured with grant_types including "urn:ietf:params:oauth:
# grant-type:token-exchange" so it can perform OBO.
# ═══════════════════════════════════════════════════════════════════════════


class TestCliOboSetup:
    """
    Provision an OBO scenario: front-end → back-end delegation chain.

    - Front-end client: authorization_code + refresh_token
    - Back-end client: client_credentials + token-exchange (OBO)
    - Verify both exist, then clean up.
    """

    _fe_cid = f"{E2E_PREFIX}-obo-fe-{_RUN_SUFFIX}"
    _be_cid = f"{E2E_PREFIX}-obo-be-{_RUN_SUFFIX}"
    _fe_internal: str | None = None
    _be_internal: str | None = None
    _realm_id: str | None = None

    def test_01_get_realm_id(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == "default"]
        assert len(match) == 1
        TestCliOboSetup._realm_id = str(match[0]["id"])

    def test_02_create_frontend_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        r = cli_logged_in.run(
            "client", "create",
            "--client-id", self._fe_cid,
            "--client-name", f"E2E OBO Frontend {_RUN_SUFFIX}",
            "--realm-id", self._realm_id,
            "--scope", "openid profile email",
            "--grant-types", "authorization_code",
            "--grant-types", "refresh_token",
            "--redirect-uris", "https://e2e-obo.local/callback",
            "--require-pkce",
            "--create-initial-secret",
            "--output", str(tmp_path / "fe-creds.json"),
            "--overwrite",
        )
        assert r.ok, f"OBO frontend create failed: {r.stderr or r.stdout}"

    def test_03_create_backend_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        r = cli_logged_in.run(
            "client", "create",
            "--client-id", self._be_cid,
            "--client-name", f"E2E OBO Backend {_RUN_SUFFIX}",
            "--realm-id", self._realm_id,
            "--scope", "openid profile",
            "--grant-types", "client_credentials",
            "--grant-types", "urn:ietf:params:oauth:grant-type:token-exchange",
            "--create-initial-secret",
            "--output", str(tmp_path / "be-creds.json"),
            "--overwrite",
        )
        assert r.ok, f"OBO backend create failed: {r.stderr or r.stdout}"

    def test_04_both_clients_in_list(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("client", "list")
        ids = {c.get("clientId", ""): str(c.get("id", "")) for c in data}
        assert self._fe_cid in ids, f"Frontend client '{self._fe_cid}' not found"
        assert self._be_cid in ids, f"Backend client '{self._be_cid}' not found"
        TestCliOboSetup._fe_internal = ids[self._fe_cid]
        TestCliOboSetup._be_internal = ids[self._be_cid]

    def test_05_get_backend_client(self, cli_logged_in: CliHelper):
        if not self._be_internal:
            pytest.skip("Backend client ID not captured")
        r = cli_logged_in.run("client", "get", self._be_internal)
        assert r.ok
        assert self._be_cid in r.stdout

    def test_06_cleanup_frontend(self, cli_logged_in: CliHelper):
        if not self._fe_internal:
            pytest.skip("Frontend client ID not captured")
        r = cli_logged_in.run("client", "delete", self._fe_internal, "--confirm")
        assert r.ok, f"OBO frontend delete failed: {r.stderr or r.stdout}"

    def test_07_cleanup_backend(self, cli_logged_in: CliHelper):
        if not self._be_internal:
            pytest.skip("Backend client ID not captured")
        r = cli_logged_in.run("client", "delete", self._be_internal, "--confirm")
        assert r.ok, f"OBO backend delete failed: {r.stderr or r.stdout}"


# ═══════════════════════════════════════════════════════════════════════════
# Full provisioning workflow (realm + scopes + users + clients, then nuke)
# ═══════════════════════════════════════════════════════════════════════════


class TestCliFullProvisioningWorkflow:
    """
    End-to-end: create a realm, custom scopes, users, and multiple clients
    (including M2M and OBO-ready), verify everything, then tear down.
    """

    _realm_name = f"{E2E_PREFIX}-full-{_RUN_SUFFIX}"
    _scope_api_read = f"{E2E_PREFIX}.api.read.{_RUN_SUFFIX}"
    _scope_api_write = f"{E2E_PREFIX}.api.write.{_RUN_SUFFIX}"
    _username = f"{E2E_PREFIX}-fulluser-{_RUN_SUFFIX}"
    _webapp_cid = f"{E2E_PREFIX}-webapp-{_RUN_SUFFIX}"
    _api_cid = f"{E2E_PREFIX}-api-{_RUN_SUFFIX}"

    _realm_id: str | None = None
    _user_id: str | None = None
    _webapp_internal: str | None = None
    _api_internal: str | None = None

    # -- Phase 1: Provision -------------------------------------------------

    def test_01_create_realm(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run(
            "realm", "create",
            "--name", self._realm_name,
            "--display-name", "E2E Full Workflow Realm",
        )
        assert r.ok, f"realm create failed: {r.stderr or r.stdout}"

    def test_02_capture_realm_id(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == self._realm_name]
        assert len(match) == 1
        TestCliFullProvisioningWorkflow._realm_id = str(match[0]["id"])

    def test_03_create_scopes(self, cli_logged_in: CliHelper):
        for scope, desc in [
            (self._scope_api_read, "API read access"),
            (self._scope_api_write, "API write access"),
        ]:
            r = cli_logged_in.run(
                "scope", "create",
                "--name", scope,
                "--description", desc,
                "--is-exposed",
            )
            assert r.ok, f"scope create '{scope}' failed: {r.stderr or r.stdout}"

    def test_04_create_user(self, cli_logged_in: CliHelper, tmp_path: Path):
        r = cli_logged_in.run(
            "user", "create",
            "--username", self._username,
            "--email", f"{self._username}@test.local",
            "--name", "E2E Full User",
            "--password", "FullWorkflow_Pass123!",
            "--output", str(tmp_path / "full-user.json"),
            "--overwrite",
        )
        assert r.ok, f"user create failed: {r.stderr or r.stdout}"

    def test_05_capture_user_id(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("user", "list", "--search", self._username)
        items = data.get("items", data) if isinstance(data, dict) else data
        match = [u for u in items if u.get("username") == self._username]
        assert len(match) == 1
        TestCliFullProvisioningWorkflow._user_id = str(match[0]["id"])

    def test_06_create_webapp_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        r = cli_logged_in.run(
            "client", "create",
            "--client-id", self._webapp_cid,
            "--client-name", "E2E Web App",
            "--realm-id", self._realm_id,
            "--scope", f"openid profile email {self._scope_api_read}",
            "--grant-types", "authorization_code",
            "--grant-types", "refresh_token",
            "--redirect-uris", "https://e2e-webapp.local/callback",
            "--logout-redirect-uris", "https://e2e-webapp.local/",
            "--require-pkce",
            "--create-initial-secret",
            "--output", str(tmp_path / "webapp-creds.json"),
            "--overwrite",
        )
        assert r.ok, f"webapp client create failed: {r.stderr or r.stdout}"

    def test_07_create_api_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        r = cli_logged_in.run(
            "client", "create",
            "--client-id", self._api_cid,
            "--client-name", "E2E API Service",
            "--realm-id", self._realm_id,
            "--scope", f"openid {self._scope_api_read} {self._scope_api_write}",
            "--grant-types", "client_credentials",
            "--grant-types", "urn:ietf:params:oauth:grant-type:token-exchange",
            "--create-initial-secret",
            "--output", str(tmp_path / "api-creds.json"),
            "--overwrite",
        )
        assert r.ok, f"API client create failed: {r.stderr or r.stdout}"

    # -- Phase 2: Verify ----------------------------------------------------

    def test_08_verify_all_clients(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("client", "list")
        ids = {c.get("clientId", ""): str(c.get("id", "")) for c in data}
        assert self._webapp_cid in ids, f"Web app client missing"
        assert self._api_cid in ids, f"API client missing"
        TestCliFullProvisioningWorkflow._webapp_internal = ids[self._webapp_cid]
        TestCliFullProvisioningWorkflow._api_internal = ids[self._api_cid]

    def test_09_verify_scopes(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("scope", "list")
        names = {s.get("name", "") for s in data}
        assert self._scope_api_read in names
        assert self._scope_api_write in names

    def test_10_verify_user(self, cli_logged_in: CliHelper):
        if not self._user_id:
            pytest.skip("User ID not captured")
        r = cli_logged_in.run("user", "get", self._user_id)
        assert r.ok
        assert self._username in r.stdout

    # -- Phase 3: Export realm -----------------------------------------------

    @pytest.mark.xfail(reason="Server export handler uses httpContext.Items['TenantId'] instead of ITenantAccessor")
    def test_11_export_realm(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        r = cli_logged_in.run(
            "export", "realm", self._realm_id,
            "--output", str(tmp_path),
            "--overwrite",
        )
        assert r.ok, f"export realm failed: {r.stderr or r.stdout}"

    # -- Phase 4: Tear down -------------------------------------------------

    def test_90_delete_clients(self, cli_logged_in: CliHelper):
        for label, iid in [
            ("webapp", self._webapp_internal),
            ("api", self._api_internal),
        ]:
            if iid:
                r = cli_logged_in.run("client", "delete", iid, "--confirm")
                assert r.ok, f"delete {label} client failed: {r.stderr or r.stdout}"

    def test_91_delete_user(self, cli_logged_in: CliHelper):
        if not self._user_id:
            pytest.skip("User ID not captured")
        r = cli_logged_in.run("user", "delete", self._user_id, "--confirm")
        assert r.ok, f"delete user failed: {r.stderr or r.stdout}"

    def test_92_delete_scopes(self, cli_logged_in: CliHelper):
        for scope in [self._scope_api_read, self._scope_api_write]:
            r = cli_logged_in.run("scope", "delete", scope, "--confirm")
            assert r.ok, f"delete scope '{scope}' failed: {r.stderr or r.stdout}"

    def test_93_delete_realm(self, cli_logged_in: CliHelper):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        r = cli_logged_in.run("realm", "delete", self._realm_id, "--confirm")
        assert r.ok, f"delete realm failed: {r.stderr or r.stdout}"

    def test_94_verify_cleanup(self, cli_logged_in: CliHelper):
        # Realm should be gone
        data = cli_logged_in.run_json("realm", "list")
        names = [r.get("name", "") for r in data]
        assert self._realm_name not in names

        # Scopes should be gone
        scopes = cli_logged_in.run_json("scope", "list")
        scope_names = {s.get("name", "") for s in scopes}
        assert self._scope_api_read not in scope_names
        assert self._scope_api_write not in scope_names
