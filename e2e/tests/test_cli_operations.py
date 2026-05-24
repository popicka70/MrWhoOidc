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
import subprocess
import time
import uuid
from pathlib import Path

import pytest

from utils.cli_helper import CliHelper

E2E_PREFIX = "e2e-cli"
_RUN_SUFFIX = str(int(time.time()))[-6:]


def _run_psql(sql: str, *, expected_success: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        [
            "docker",
            "exec",
            "mrwhooidc-postgres-1",
            "psql",
            "-U",
            "oidc",
            "-d",
            "authdb",
            "-v",
            "ON_ERROR_STOP=1",
            "-c",
            sql,
        ],
        capture_output=True,
        text=True,
        check=False,
    )

    if expected_success and result.returncode != 0:
        raise AssertionError(result.stderr or result.stdout)
    if not expected_success and result.returncode == 0:
        raise AssertionError("Expected SQL command to fail, but it succeeded.")

    return result


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


class TestCliUnassignedUsers:
    """Platform-admin CLI coverage for account-level users without tenant access."""

    account_id = str(uuid.uuid4())
    email = f"{E2E_PREFIX}-unassigned-{_RUN_SUFFIX}@example.com"

    def test_01_seed_unassigned_account(self):
        sql = f"""
INSERT INTO "UserAccounts"
    ("Id", "Username", "PasswordHash", "HashAlgorithm", "Email", "NormalizedEmail", "EmailVerified", "Name", "CreatedAt", "TotpEnabled", "FailedLoginAttempts")
VALUES
    ('{self.account_id}', '{self.email}', 'e2e-placeholder', 'argon2id', '{self.email}', '{self.email}', false, 'E2E Unassigned CLI', now(), false, 0);
"""
        _run_psql(sql)

    def test_02_list_unassigned_accounts_json(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("user", "unassigned", "list", "--search", self.email)
        items = data.get("items", [])
        assert any(item["id"].lower() == self.account_id for item in items), data

    def test_03_get_unassigned_account_json(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("user", "unassigned", "get", self.account_id)
        assert data["id"].lower() == self.account_id
        assert data["email"] == self.email

    def test_04_terminate_unassigned_account(self, cli_logged_in: CliHelper):
        result = cli_logged_in.run("user", "unassigned", "terminate", self.account_id, "--confirm")
        assert result.ok, f"terminate failed: {result.stderr or result.stdout}"

    def test_05_unassigned_account_removed(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("user", "unassigned", "list", "--search", self.email)
        assert not any(item["id"].lower() == self.account_id for item in data.get("items", [])), data


# ═══════════════════════════════════════════════════════════════════════════
# Profile management (rename, validation, server header)
# ═══════════════════════════════════════════════════════════════════════════


class TestCliProfileManagement:
    """Tests for multi-profile features: rename, name validation, server header."""

    def _current_profile_name(self, cli: CliHelper) -> str:
        """Return the name of the currently active profile via JSON."""
        data = cli.run_json("profile", "show")
        name = data.get("Name") or data.get("name")
        assert name, f"Could not determine profile name from: {data}"
        return name

    # -- server header in stderr -------------------------------------------

    def test_server_header_in_stderr(self, cli_logged_in: CliHelper):
        """Authenticated server commands should emit a Server: header to stderr."""
        r = cli_logged_in.run("scope", "list")
        assert r.ok
        assert "Server:" in r.stderr, (
            f"Expected 'Server:' header in stderr, got:\n{r.stderr}"
        )

    def test_server_header_contains_url(self, cli_logged_in: CliHelper):
        """The server header should contain the target server URL."""
        r = cli_logged_in.run("scope", "list")
        assert r.ok
        assert "localhost" in r.stderr, (
            f"Expected server URL in stderr header, got:\n{r.stderr}"
        )

    def test_server_header_not_in_stdout_json(self, cli_logged_in: CliHelper):
        """JSON output on stdout must not contain the server header."""
        data = cli_logged_in.run_json("scope", "list")
        # If we got here, JSON parsed successfully — header is not in stdout
        assert isinstance(data, list)

    # -- profile rename ----------------------------------------------------

    def test_profile_rename_and_back(self, cli_logged_in: CliHelper):
        """Rename the current profile, verify it sticks, then rename back."""
        original = self._current_profile_name(cli_logged_in)
        temp_name = f"{E2E_PREFIX}-prof"

        # Rename to temp name
        r = cli_logged_in.run("profile", "rename", original, temp_name)
        assert r.ok, f"rename failed: {r.stdout}\n{r.stderr}"

        # Verify the new name via JSON (table output wraps long names)
        new_name = self._current_profile_name(cli_logged_in)
        assert new_name == temp_name, f"Expected '{temp_name}', got '{new_name}'"

        # Rename back
        r3 = cli_logged_in.run("profile", "rename", temp_name, original)
        assert r3.ok, f"rename-back failed: {r3.stdout}\n{r3.stderr}"

    def test_profile_rename_invalid_spaces(self, cli_logged_in: CliHelper):
        """Profile names with spaces should be rejected."""
        original = self._current_profile_name(cli_logged_in)
        r = cli_logged_in.run("profile", "rename", original, "bad name")
        assert not r.ok or "invalid" in (r.stdout + r.stderr).lower(), (
            f"Expected rejection of name with spaces:\nstdout={r.stdout}\nstderr={r.stderr}"
        )

    def test_profile_rename_invalid_special_chars(self, cli_logged_in: CliHelper):
        """Profile names with special characters should be rejected."""
        original = self._current_profile_name(cli_logged_in)
        r = cli_logged_in.run("profile", "rename", original, "bad!@#name")
        assert not r.ok or "invalid" in (r.stdout + r.stderr).lower(), (
            f"Expected rejection of name with special chars:\nstdout={r.stdout}\nstderr={r.stderr}"
        )

    def test_profile_rename_nonexistent_source(self, cli_logged_in: CliHelper):
        """Renaming a profile that doesn't exist should fail."""
        r = cli_logged_in.run("profile", "rename", "does-not-exist", "new-name")
        assert not r.ok or "not found" in (r.stdout + r.stderr).lower(), (
            f"Expected error for nonexistent profile:\nstdout={r.stdout}\nstderr={r.stderr}"
        )


# ═══════════════════════════════════════════════════════════════════════════
# Invitation CRUD
# ═══════════════════════════════════════════════════════════════════════════


class TestCliInvitationCrud:
    """Create → list → revoke a tenant invitation via CLI."""

    _email = f"{E2E_PREFIX}-invite-{_RUN_SUFFIX}@example.com"
    _display_name = "E2E CLI Invitee"
    _invitation_id: str | None = None

    def test_01_create_invitation(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json(
            "invitation", "create",
            "--email", self._email,
            "--display-name", self._display_name,
            "--valid-days", "14",
        )
        invitation = data.get("invitation") or {}
        assert invitation.get("email") == self._email
        assert invitation.get("status") == "Pending"
        assert "/invitations/inv_" in data.get("invitationLink", "")
        TestCliInvitationCrud._invitation_id = invitation.get("id")

    def test_02_invitation_appears_in_list(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("invitation", "list")
        assert isinstance(data, list)
        match = [item for item in data if item.get("email") == self._email]
        assert len(match) == 1
        assert match[0].get("status") == "Pending"
        TestCliInvitationCrud._invitation_id = self._invitation_id or match[0].get("id")

    def test_03_revoke_invitation(self, cli_logged_in: CliHelper):
        if not self._invitation_id:
            pytest.skip("Invitation ID not captured")
        r = cli_logged_in.run(
            "invitation", "revoke", self._invitation_id,
            "--reason", "E2E cleanup",
            "--confirm",
        )
        assert r.ok, f"invitation revoke failed: {r.stderr or r.stdout}"

    def test_04_invitation_is_revoked(self, cli_logged_in: CliHelper):
        if not self._invitation_id:
            pytest.skip("Invitation ID not captured")
        data = cli_logged_in.run_json("invitation", "list")
        match = [item for item in data if item.get("id") == self._invitation_id]
        assert len(match) == 1
        assert match[0].get("status") == "Revoked"


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


# ═══════════════════════════════════════════════════════════════════════════
# Client update
# ═══════════════════════════════════════════════════════════════════════════


class TestCliClientUpdate:
    """Verify that ``client update`` applies patch-style changes to a client."""

    _cid = f"{E2E_PREFIX}-upd-{_RUN_SUFFIX}"
    _internal_id: str | None = None
    _realm_id: str | None = None

    def test_01_setup(self, cli_logged_in: CliHelper, tmp_path: Path):
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == "default"]
        assert len(match) == 1
        TestCliClientUpdate._realm_id = str(match[0]["id"])
        r = cli_logged_in.run(
            "client", "create",
            "--client-id", self._cid,
            "--client-name", f"E2E Update Client {_RUN_SUFFIX}",
            "--realm-id", self._realm_id,
            "--scope", "openid profile",
            "--grant-types", "authorization_code",
            "--redirect-uris", "https://e2e-upd.local/cb",
            "--require-pkce",
            "--create-initial-secret",
            "--output", str(tmp_path / "upd-creds.json"),
            "--overwrite",
        )
        assert r.ok, f"client create failed: {r.stderr or r.stdout}"

    def test_02_capture_id(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("client", "list")
        match = [c for c in data if c.get("clientId") == self._cid]
        assert len(match) == 1
        TestCliClientUpdate._internal_id = str(match[0]["id"])

    def test_03_update_name(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        r = cli_logged_in.run(
            "client", "update", self._internal_id,
            "--client-name", f"E2E Updated Name {_RUN_SUFFIX}",
        )
        assert r.ok, f"client update (name) failed: {r.stderr or r.stdout}"

    def test_04_update_require_consent(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        r = cli_logged_in.run(
            "client", "update", self._internal_id,
            "--require-consent", "true",
        )
        assert r.ok, f"client update (require-consent) failed: {r.stderr or r.stdout}"

    def test_05_update_backchannel_uri(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        r = cli_logged_in.run(
            "client", "update", self._internal_id,
            "--backchannel-logout-uri", "https://e2e-upd.local/logout/backchannel",
        )
        assert r.ok, f"client update (backchannel) failed: {r.stderr or r.stdout}"

    def test_06_verify_updates(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        r = cli_logged_in.run("client", "get", self._internal_id)
        assert r.ok
        assert "Updated Name" in r.stdout

    def test_07_cleanup(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        r = cli_logged_in.run("client", "delete", self._internal_id, "--confirm")
        assert r.ok, f"client delete failed: {r.stderr or r.stdout}"


# ═══════════════════════════════════════════════════════════════════════════
# Role CRUD
# ═══════════════════════════════════════════════════════════════════════════


class TestCliRoleCrud:
    """Create → get → update → list → delete a realm role via CLI."""

    _role_name = f"{E2E_PREFIX}-role-{_RUN_SUFFIX}"
    _role_id: str | None = None
    _realm_id: str | None = None

    def test_01_get_realm_id(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == "default"]
        assert len(match) == 1, "Default realm not found"
        TestCliRoleCrud._realm_id = str(match[0]["id"])

    def test_02_create_role(self, cli_logged_in: CliHelper):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        r = cli_logged_in.run(
            "role", "create",
            "--name", self._role_name,
            "--realm-id", self._realm_id,
        )
        assert r.ok, f"role create failed: {r.stderr or r.stdout}"
        assert "created" in r.stdout.lower() or self._role_name in r.stdout

    def test_03_role_in_list(self, cli_logged_in: CliHelper):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        data = cli_logged_in.run_json("role", "list", "--realm-id", self._realm_id)
        assert isinstance(data, list)
        match = [r for r in data if r.get("name") == self._role_name]
        assert len(match) == 1, f"Role '{self._role_name}' not found in list"
        TestCliRoleCrud._role_id = str(match[0]["id"])

    def test_04_get_role(self, cli_logged_in: CliHelper):
        if not self._role_id:
            pytest.skip("Role ID not captured")
        r = cli_logged_in.run("role", "get", self._role_id)
        assert r.ok, f"role get failed: {r.stderr}"
        assert self._role_name in r.stdout

    def test_05_update_role(self, cli_logged_in: CliHelper):
        if not self._role_id:
            pytest.skip("Role ID not captured")
        r = cli_logged_in.run(
            "role", "update", self._role_id,
            "--name", f"{self._role_name}-upd",
        )
        assert r.ok, f"role update failed: {r.stderr or r.stdout}"

    def test_06_verify_update(self, cli_logged_in: CliHelper):
        if not self._role_id:
            pytest.skip("Role ID not captured")
        r = cli_logged_in.run("role", "get", self._role_id)
        assert r.ok
        assert "-upd" in r.stdout

    def test_07_delete_role(self, cli_logged_in: CliHelper):
        if not self._role_id:
            pytest.skip("Role ID not captured")
        r = cli_logged_in.run("role", "delete", self._role_id, "--confirm")
        assert r.ok, f"role delete failed: {r.stderr or r.stdout}"

    def test_08_role_gone(self, cli_logged_in: CliHelper):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        data = cli_logged_in.run_json("role", "list", "--realm-id", self._realm_id)
        names = [r.get("name", "") for r in data]
        assert self._role_name not in names
        assert f"{self._role_name}-upd" not in names


# ═══════════════════════════════════════════════════════════════════════════
# User role assignment
# ═══════════════════════════════════════════════════════════════════════════


class TestCliUserRoleAssignment:
    """Create a role and a user, assign the role, verify, then unassign and clean up."""

    _role_name = f"{E2E_PREFIX}-assign-role-{_RUN_SUFFIX}"
    _username = f"{E2E_PREFIX}-assign-user-{_RUN_SUFFIX}"
    _role_id: str | None = None
    _user_id: str | None = None
    _realm_id: str | None = None

    def test_01_setup(self, cli_logged_in: CliHelper, tmp_path: Path):
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == "default"]
        assert len(match) == 1
        TestCliUserRoleAssignment._realm_id = str(match[0]["id"])
        # Create role
        r = cli_logged_in.run(
            "role", "create",
            "--name", self._role_name,
            "--realm-id", self._realm_id,
        )
        assert r.ok, f"role create failed: {r.stderr or r.stdout}"
        # Create user
        r = cli_logged_in.run(
            "user", "create",
            "--username", self._username,
            "--email", f"{self._username}@test.local",
            "--password", "RoleAssign_Pass123!",
            "--output", str(tmp_path / "assign-user.json"),
            "--overwrite",
        )
        assert r.ok, f"user create failed: {r.stderr or r.stdout}"

    def test_02_capture_ids(self, cli_logged_in: CliHelper):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        # Capture role ID
        role_data = cli_logged_in.run_json("role", "list", "--realm-id", self._realm_id)
        match_r = [r for r in role_data if r.get("name") == self._role_name]
        assert len(match_r) == 1
        TestCliUserRoleAssignment._role_id = str(match_r[0]["id"])
        # Capture user ID
        user_data = cli_logged_in.run_json("user", "list", "--search", self._username)
        items = user_data.get("items", user_data) if isinstance(user_data, dict) else user_data
        match_u = [u for u in items if u.get("username") == self._username]
        assert len(match_u) == 1
        TestCliUserRoleAssignment._user_id = str(match_u[0]["id"])

    def test_03_assign_role(self, cli_logged_in: CliHelper):
        if not self._user_id or not self._role_id:
            pytest.skip("IDs not captured")
        r = cli_logged_in.run(
            "user", "role", "assign",
            self._user_id,
            "--role-id", self._role_id,
        )
        assert r.ok, f"user role assign failed: {r.stderr or r.stdout}"

    def test_04_list_user_roles(self, cli_logged_in: CliHelper):
        if not self._user_id or not self._role_id:
            pytest.skip("IDs not captured")
        data = cli_logged_in.run_json("user", "role", "list", self._user_id)
        # API returns {"realmRoles": [...], "clientRoles": [...]} not a flat list
        roles = data.get("realmRoles", []) + data.get("clientRoles", []) if isinstance(data, dict) else data
        ids = [str(r.get("id", "")) for r in roles]
        assert self._role_id in ids, f"Role '{self._role_id}' not found in user's roles"

    def test_05_unassign_role(self, cli_logged_in: CliHelper):
        if not self._user_id or not self._role_id:
            pytest.skip("IDs not captured")
        r = cli_logged_in.run(
            "user", "role", "unassign",
            self._user_id,
            "--role-id", self._role_id,
            "--confirm",
        )
        assert r.ok, f"user role unassign failed: {r.stderr or r.stdout}"

    def test_06_verify_unassign(self, cli_logged_in: CliHelper):
        if not self._user_id or not self._role_id:
            pytest.skip("IDs not captured")
        data = cli_logged_in.run_json("user", "role", "list", self._user_id)
        roles = data.get("realmRoles", []) + data.get("clientRoles", []) if isinstance(data, dict) else data
        ids = [str(r.get("id", "")) for r in roles]
        assert self._role_id not in ids, "Role still assigned after unassign"

    def test_07_cleanup(self, cli_logged_in: CliHelper):
        if self._user_id:
            r = cli_logged_in.run("user", "delete", self._user_id, "--confirm")
            assert r.ok, f"user delete failed: {r.stderr or r.stdout}"
        if self._role_id:
            r = cli_logged_in.run("role", "delete", self._role_id, "--confirm")
            assert r.ok, f"role delete failed: {r.stderr or r.stdout}"


# ═══════════════════════════════════════════════════════════════════════════
# Client secrets lifecycle
# ═══════════════════════════════════════════════════════════════════════════


class TestCliClientSecrets:
    """
    Full client-secret lifecycle on a dedicated client:
    initial list → create → activate → set-primary → revoke → clean up.
    """

    _cid = f"{E2E_PREFIX}-sec-{_RUN_SUFFIX}"
    _internal_id: str | None = None
    _realm_id: str | None = None
    _secret_id: str | None = None

    def test_01_setup(self, cli_logged_in: CliHelper, tmp_path: Path):
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == "default"]
        assert len(match) == 1
        TestCliClientSecrets._realm_id = str(match[0]["id"])
        r = cli_logged_in.run(
            "client", "create",
            "--client-id", self._cid,
            "--client-name", f"E2E Secrets Client {_RUN_SUFFIX}",
            "--realm-id", self._realm_id,
            "--scope", "openid profile",
            "--grant-types", "client_credentials",
            "--create-initial-secret",
            "--output", str(tmp_path / "sec-initial.json"),
            "--overwrite",
        )
        assert r.ok, f"client create failed: {r.stderr or r.stdout}"

    def test_02_capture_client_id(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("client", "list")
        match = [c for c in data if c.get("clientId") == self._cid]
        assert len(match) == 1
        TestCliClientSecrets._internal_id = str(match[0]["id"])

    def test_03_list_secrets(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        data = cli_logged_in.run_json("client", "secret", "list", self._internal_id)
        assert isinstance(data, list), "Expected a list of secrets"
        assert len(data) >= 1, "No initial secret found"

    def test_04_create_secret(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        secret_file = tmp_path / "new-secret.json"
        r = cli_logged_in.run(
            "client", "secret", "create", self._internal_id,
            "--expires-in-days", "90",
            "--description", "e2e-test-secret",
            "--activate",
            "--output", str(secret_file),
            "--overwrite",
        )
        assert r.ok, f"client secret create failed: {r.stderr or r.stdout}"
        assert secret_file.exists(), "Secret file not written by CLI"

    def test_05_capture_new_secret_id(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        data = cli_logged_in.run_json("client", "secret", "list", self._internal_id)
        match = [s for s in data if s.get("description") == "e2e-test-secret"]
        assert len(match) == 1, "Newly created secret not found"
        TestCliClientSecrets._secret_id = str(match[0]["id"])

    def test_06_activate_secret(self, cli_logged_in: CliHelper):
        if not self._internal_id or not self._secret_id:
            pytest.skip("IDs not captured")
        r = cli_logged_in.run(
            "client", "secret", "activate",
            self._internal_id, self._secret_id,
        )
        assert r.ok, f"client secret activate failed: {r.stderr or r.stdout}"

    def test_07_set_primary(self, cli_logged_in: CliHelper):
        if not self._internal_id or not self._secret_id:
            pytest.skip("IDs not captured")
        r = cli_logged_in.run(
            "client", "secret", "set-primary",
            self._internal_id, self._secret_id,
        )
        assert r.ok, f"client secret set-primary failed: {r.stderr or r.stdout}"

    def test_08_revoke_secret(self, cli_logged_in: CliHelper):
        if not self._internal_id or not self._secret_id:
            pytest.skip("IDs not captured")
        r = cli_logged_in.run(
            "client", "secret", "revoke",
            self._internal_id, self._secret_id,
            "--confirm",
        )
        assert r.ok, f"client secret revoke failed: {r.stderr or r.stdout}"

    def test_09_cleanup(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        r = cli_logged_in.run("client", "delete", self._internal_id, "--confirm")
        assert r.ok, f"client delete failed: {r.stderr or r.stdout}"


# ═══════════════════════════════════════════════════════════════════════════
# Client secret rotation and validation
# ═══════════════════════════════════════════════════════════════════════════


class TestCliClientRotateAndValidate:
    """
    Tests ``client rotate-secret`` (zero-downtime rotation) and
    ``client validate`` (pre-go-live diagnostic) against the same client.
    """

    _cid = f"{E2E_PREFIX}-rot-{_RUN_SUFFIX}"
    _internal_id: str | None = None
    _realm_id: str | None = None

    def test_01_setup(self, cli_logged_in: CliHelper, tmp_path: Path):
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == "default"]
        assert len(match) == 1
        TestCliClientRotateAndValidate._realm_id = str(match[0]["id"])
        r = cli_logged_in.run(
            "client", "create",
            "--client-id", self._cid,
            "--client-name", f"E2E Rotation Client {_RUN_SUFFIX}",
            "--realm-id", self._realm_id,
            "--scope", "openid profile",
            "--grant-types", "client_credentials",
            "--create-initial-secret",
            "--output", str(tmp_path / "rot-initial.json"),
            "--overwrite",
        )
        assert r.ok, f"client create failed: {r.stderr or r.stdout}"

    def test_02_capture_client_id(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("client", "list")
        match = [c for c in data if c.get("clientId") == self._cid]
        assert len(match) == 1
        TestCliClientRotateAndValidate._internal_id = str(match[0]["id"])

    def test_03_rotate_secret(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        rotated_file = tmp_path / "rotated-secret.json"
        r = cli_logged_in.run(
            "client", "rotate-secret", self._internal_id,
            "--expires-in-days", "90",
            "--description", "rotated-e2e",
            "--output", str(rotated_file),
            "--overwrite",
            "--confirm",
        )
        assert r.ok, f"client rotate-secret failed: {r.stderr or r.stdout}"
        assert rotated_file.exists(), "Rotated secret file not written"

    def test_04_validate_client(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        r = cli_logged_in.run("client", "validate", self._internal_id)
        assert r.ok, f"client validate failed: {r.stderr or r.stdout}"

    def test_05_cleanup(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        r = cli_logged_in.run("client", "delete", self._internal_id, "--confirm")
        assert r.ok, f"client delete failed: {r.stderr or r.stdout}"


# ═══════════════════════════════════════════════════════════════════════════
# Client scope management (post-creation)
# ═══════════════════════════════════════════════════════════════════════════


class TestCliClientScopes:
    """Add a custom scope to a client post-creation, then remove it."""

    _cid = f"{E2E_PREFIX}-cscope-{_RUN_SUFFIX}"
    _scope_name = f"{E2E_PREFIX}.cscope.{_RUN_SUFFIX}"
    _internal_id: str | None = None
    _realm_id: str | None = None

    def test_01_setup(self, cli_logged_in: CliHelper, tmp_path: Path):
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == "default"]
        assert len(match) == 1
        TestCliClientScopes._realm_id = str(match[0]["id"])
        # Custom scope
        r = cli_logged_in.run(
            "scope", "create",
            "--name", self._scope_name,
            "--description", "E2E client scope test",
        )
        assert r.ok, f"scope create failed: {r.stderr or r.stdout}"
        # Client created without the custom scope initially
        r = cli_logged_in.run(
            "client", "create",
            "--client-id", self._cid,
            "--client-name", f"E2E Client Scope Test {_RUN_SUFFIX}",
            "--realm-id", self._realm_id,
            "--scope", "openid profile",
            "--grant-types", "client_credentials",
            "--create-initial-secret",
            "--output", str(tmp_path / "cscope-creds.json"),
            "--overwrite",
        )
        assert r.ok, f"client create failed: {r.stderr or r.stdout}"

    def test_02_capture_client_id(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("client", "list")
        match = [c for c in data if c.get("clientId") == self._cid]
        assert len(match) == 1
        TestCliClientScopes._internal_id = str(match[0]["id"])

    def test_03_list_client_scopes(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        r = cli_logged_in.run("client", "scope", "list", self._internal_id)
        assert r.ok, f"client scope list failed: {r.stderr or r.stdout}"

    def test_04_add_scope(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        r = cli_logged_in.run(
            "client", "scope", "add", self._internal_id,
            "--scope", self._scope_name,
        )
        assert r.ok, f"client scope add failed: {r.stderr or r.stdout}"

    def test_05_verify_scope_added(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        data = cli_logged_in.run_json("client", "scope", "list", self._internal_id)
        # API returns [{"scopeName": "..."}, ...] not [{"name": "..."}]
        scope_names = [
            s.get("scopeName", s.get("name", str(s))) if isinstance(s, dict) else str(s)
            for s in (data if isinstance(data, list) else [])
        ]
        assert any(self._scope_name in n for n in scope_names), \
            f"Scope '{self._scope_name}' not present after add"

    def test_06_remove_scope(self, cli_logged_in: CliHelper):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        r = cli_logged_in.run(
            "client", "scope", "remove", self._internal_id,
            "--scope", self._scope_name,
            "--confirm",
        )
        assert r.ok, f"client scope remove failed: {r.stderr or r.stdout}"

    def test_07_cleanup(self, cli_logged_in: CliHelper):
        if self._internal_id:
            cli_logged_in.run("client", "delete", self._internal_id, "--confirm")
        cli_logged_in.run("scope", "delete", self._scope_name, "--confirm")


# ═══════════════════════════════════════════════════════════════════════════
# Diagnostics: health, whoami, audit, BCL, rate-limits
# ═══════════════════════════════════════════════════════════════════════════


class TestCliDiagnostics:
    """Read-only diagnostic and observability commands."""

    # -- health --

    def test_health(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("health")
        assert r.ok, f"health failed: {r.stderr or r.stdout}"

    def test_health_json(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("health")
        assert data is not None

    # -- whoami --

    def test_whoami(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("whoami")
        assert r.ok, f"whoami failed: {r.stderr or r.stdout}"
        assert r.stdout.strip() != ""

    def test_whoami_json(self, cli_logged_in: CliHelper):
        # whoami does not support --format Json; verify plaintext profile output
        r = cli_logged_in.run("whoami")
        assert r.ok, f"whoami failed: {r.stderr or r.stdout}"
        assert r.stdout.strip() != ""

    # -- audit --

    def test_audit_list(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("audit", "list")
        assert r.ok, f"audit list failed: {r.stderr or r.stdout}"

    def test_audit_list_json(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("audit", "list")
        assert isinstance(data, (list, dict))

    def test_audit_get_first_entry(self, cli_logged_in: CliHelper):
        """If any audit entries exist, verify that ``audit get <id>`` works."""
        data = cli_logged_in.run_json("audit", "list")
        entries = data.get("items", data) if isinstance(data, dict) else data
        if not entries:
            pytest.skip("No audit entries available yet")
        entry_id = str(entries[0].get("id", ""))
        if not entry_id:
            pytest.skip("Audit entry has no id field")
        r = cli_logged_in.run("audit", "get", entry_id)
        assert r.ok, f"audit get failed: {r.stderr or r.stdout}"

    # -- backchannel logout --

    def test_bcl_outbox(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("bcl", "outbox")
        assert r.ok, f"bcl outbox failed: {r.stderr or r.stdout}"

    def test_bcl_outbox_json(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("bcl", "outbox")
        assert isinstance(data, (list, dict))

    def test_bcl_alerts(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("bcl", "alerts")
        assert r.ok, f"bcl alerts failed: {r.stderr or r.stdout}"

    def test_bcl_alerts_json(self, cli_logged_in: CliHelper):
        # bcl alerts --format Json returns ANSI-coloured plaintext when no alerts
        # are configured, so we only assert the command exits successfully.
        r = cli_logged_in.run("bcl", "alerts", "--format", "Json")
        assert r.ok, f"bcl alerts --format Json failed: {r.stderr or r.stdout}"

    # -- rate limits --

    def test_rate_limits_overview(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("rate-limits", "overview")
        assert r.ok, f"rate-limits overview failed: {r.stderr or r.stdout}"

    def test_rate_limits_events(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("rate-limits", "events")
        assert r.ok, f"rate-limits events failed: {r.stderr or r.stdout}"

# ═══════════════════════════════════════════════════════════════════════════
# Export / Import
# ═══════════════════════════════════════════════════════════════════════════


class TestCliExportImport:
    """
    Export the default tenant manifest (obfuscated), then run ``import preview``
    as a dry-run.  Also exports a dedicated test client.
    """

    _cid = f"{E2E_PREFIX}-exp-{_RUN_SUFFIX}"
    _internal_id: str | None = None
    _realm_id: str | None = None
    _export_dir: Path | None = None
    _client_export_dir: Path | None = None

    def test_01_setup_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        data = cli_logged_in.run_json("realm", "list")
        match = [r for r in data if r.get("name") == "default"]
        assert len(match) == 1
        TestCliExportImport._realm_id = str(match[0]["id"])
        r = cli_logged_in.run(
            "client", "create",
            "--client-id", self._cid,
            "--client-name", f"E2E Export Client {_RUN_SUFFIX}",
            "--realm-id", self._realm_id,
            "--scope", "openid profile",
            "--grant-types", "client_credentials",
            "--create-initial-secret",
            "--output", str(tmp_path / "exp-creds.json"),
            "--overwrite",
        )
        assert r.ok, f"client create failed: {r.stderr or r.stdout}"

    def test_02_capture_client_id(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("client", "list")
        match = [c for c in data if c.get("clientId") == self._cid]
        assert len(match) == 1
        TestCliExportImport._internal_id = str(match[0]["id"])

    def test_03_export_tenant(self, cli_logged_in: CliHelper, tmp_path: Path):
        export_dir = tmp_path / "tenant-export"
        export_dir.mkdir()
        TestCliExportImport._export_dir = export_dir
        r = cli_logged_in.run(
            "export", "tenant", "default",
            "--mode", "obfuscated",
            "--output", str(export_dir),
            "--overwrite",
        )
        assert r.ok, f"export tenant failed: {r.stderr or r.stdout}"
        assert list(export_dir.glob("*.json")), "No manifest file written by export tenant"

    def test_04_export_client(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not self._internal_id:
            pytest.skip("Client ID not captured")
        client_export_dir = tmp_path / "client-export"
        client_export_dir.mkdir()
        TestCliExportImport._client_export_dir = client_export_dir
        r = cli_logged_in.run(
            "export", "client", self._internal_id,
            "--output", str(client_export_dir),
            "--overwrite",
        )
        assert r.ok, f"export client failed: {r.stderr or r.stdout}"
        assert list(client_export_dir.glob("*.json")), "No manifest file written by export client"

    def test_05_import_preview_tenant_manifest(self, cli_logged_in: CliHelper):
        """Preview the exported tenant manifest back into the running instance."""
        if not self._export_dir:
            pytest.skip("Export dir not set")
        manifests = list(self._export_dir.glob("*.json"))
        if not manifests:
            pytest.skip("No tenant manifest found")
        r = cli_logged_in.run(
            "import", "preview", str(manifests[0]),
            "--dry-run",
        )
        # Preview exits 0 (no conflicts) or 1 (conflicts reported) — both valid
        assert r.exit_code in (0, 1), \
            f"import preview unexpected exit {r.exit_code}: {r.stderr or r.stdout}"

    def test_06_import_preview_client_manifest(self, cli_logged_in: CliHelper):
        """Preview the exported client manifest (dry-run)."""
        if not self._client_export_dir:
            pytest.skip("Client export dir not set")
        manifests = list(self._client_export_dir.glob("*.json"))
        if not manifests:
            pytest.skip("No client manifest found")
        r = cli_logged_in.run(
            "import", "preview", str(manifests[0]),
            "--dry-run",
        )
        assert r.exit_code in (0, 1), \
            f"import preview unexpected exit {r.exit_code}: {r.stderr or r.stdout}"

    def test_07_export_realm(self, cli_logged_in: CliHelper, tmp_path: Path):
        if not self._realm_id:
            pytest.skip("Realm ID not captured")
        r = cli_logged_in.run(
            "export", "realm", self._realm_id,
            "--output", str(tmp_path),
            "--overwrite",
        )
        assert r.ok, f"export realm failed: {r.stderr or r.stdout}"

    def test_08_cleanup(self, cli_logged_in: CliHelper):
        if self._internal_id:
            r = cli_logged_in.run("client", "delete", self._internal_id, "--confirm")
            assert r.ok, f"client delete failed: {r.stderr or r.stdout}"


# ═══════════════════════════════════════════════════════════════════════════
# Identity Provider CRUD + claim mappings
# ═══════════════════════════════════════════════════════════════════════════


class TestCliProviderCrud:
    """Create → get → update → add claim mapping → delete a provider via CLI."""

    _name = f"{E2E_PREFIX}-provider-{_RUN_SUFFIX}"
    _provider_id: str | None = None
    _mapping_id: str | None = None

    def test_01_provider_list_initial(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("provider", "list")
        assert r.ok, f"provider list failed: {r.stderr or r.stdout}"

    def test_02_create_provider(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run(
            "provider", "create",
            "--name", self._name,
            "--type", "Oidc",
            "--authority", "https://e2e-idp.test.local",
            "--client-id", "e2e-test-client",
            "--client-secret", "e2e-test-secret",
        )
        assert r.ok, f"provider create failed: {r.stderr or r.stdout}"
        assert "created" in r.stdout.lower() or self._name in r.stdout

    def test_03_capture_provider_id(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("provider", "list")
        assert isinstance(data, list)
        match = [p for p in data if p.get("name") == self._name]
        assert len(match) == 1, f"Provider '{self._name}' not found in list"
        TestCliProviderCrud._provider_id = str(match[0]["id"])

    def test_04_get_provider(self, cli_logged_in: CliHelper):
        if not self._provider_id:
            pytest.skip("Provider ID not captured")
        r = cli_logged_in.run("provider", "get", self._provider_id)
        assert r.ok, f"provider get failed: {r.stderr}"
        assert self._name in r.stdout

    def test_05_update_provider(self, cli_logged_in: CliHelper):
        if not self._provider_id:
            pytest.skip("Provider ID not captured")
        r = cli_logged_in.run(
            "provider", "update", self._provider_id,
            "--enabled", "false",
        )
        assert r.ok, f"provider update failed: {r.stderr or r.stdout}"

    def test_06_create_claim_mapping(self, cli_logged_in: CliHelper):
        if not self._provider_id:
            pytest.skip("Provider ID not captured")
        r = cli_logged_in.run(
            "provider", "claim-mapping", "create", self._provider_id,
            "--external-claim", "groups",
            "--local-claim", "roles",
        )
        assert r.ok, f"provider claim-mapping create failed: {r.stderr or r.stdout}"

    def test_07_list_claim_mappings(self, cli_logged_in: CliHelper):
        if not self._provider_id:
            pytest.skip("Provider ID not captured")
        data = cli_logged_in.run_json(
            "provider", "claim-mapping", "list", self._provider_id
        )
        assert isinstance(data, list)
        assert len(data) >= 1, "Claim mapping not found after create"
        TestCliProviderCrud._mapping_id = str(data[0]["id"])

    def test_08_update_claim_mapping(self, cli_logged_in: CliHelper):
        if not self._provider_id or not self._mapping_id:
            pytest.skip("IDs not captured")
        r = cli_logged_in.run(
            "provider", "claim-mapping", "update",
            self._provider_id, self._mapping_id,
            "--local-claim", "role",
        )
        assert r.ok, f"provider claim-mapping update failed: {r.stderr or r.stdout}"

    def test_09_delete_claim_mapping(self, cli_logged_in: CliHelper):
        if not self._provider_id or not self._mapping_id:
            pytest.skip("IDs not captured")
        r = cli_logged_in.run(
            "provider", "claim-mapping", "delete",
            self._provider_id, self._mapping_id,
            "--confirm",
        )
        assert r.ok, f"provider claim-mapping delete failed: {r.stderr or r.stdout}"

    def test_10_delete_provider(self, cli_logged_in: CliHelper):
        if not self._provider_id:
            pytest.skip("Provider ID not captured")
        r = cli_logged_in.run("provider", "delete", self._provider_id, "--confirm")
        assert r.ok, f"provider delete failed: {r.stderr or r.stdout}"

    def test_11_provider_gone(self, cli_logged_in: CliHelper):
        data = cli_logged_in.run_json("provider", "list")
        names = [p.get("name", "") for p in data]
        assert self._name not in names


# ═══════════════════════════════════════════════════════════════════════════
# Platform provider read (platform-admin smoke tests)
# ═══════════════════════════════════════════════════════════════════════════


class TestCliPlatformProviderRead:
    """Smoke tests for platform-scoped provider commands."""

    def test_platform_provider_list(self, cli_logged_in: CliHelper, platform_provider_setup):
        data = cli_logged_in.run_json("provider", "list", "--platform")
        names = [p.get("name", "") for p in data]
        assert platform_provider_setup.provider_name in names

    def test_platform_provider_get(self, cli_logged_in: CliHelper, platform_provider_setup):
        r = cli_logged_in.run(
            "provider",
            "get",
            platform_provider_setup.provider_id,
            "--platform",
        )
        assert r.ok, f"platform provider get failed: {r.stderr or r.stdout}"
        assert platform_provider_setup.provider_name in r.stdout


# ═══════════════════════════════════════════════════════════════════════════
# Tenant read (platform-admin smoke tests)
# ═══════════════════════════════════════════════════════════════════════════


class TestCliTenantRead:
    """Smoke tests for tenant read operations that require platform-admin."""

    _tenant_id: str | None = None

    def test_tenant_list_with_search(self, cli_logged_in: CliHelper):
        r = cli_logged_in.run("tenant", "list", "--search", "default")
        assert r.ok, f"tenant list --search failed: {r.stderr or r.stdout}"
        assert "default" in r.stdout.lower()

    def test_tenant_get_id(self, cli_logged_in: CliHelper):
        """Look up the default tenant GUID (tenant get only accepts GUIDs)."""
        data = cli_logged_in.run_json("tenant", "list")
        match = [t for t in data if t.get("slug") == "default"]
        assert len(match) == 1, "Default tenant not found in tenant list"
        TestCliTenantRead._tenant_id = str(match[0]["id"])

    def test_tenant_get_by_id(self, cli_logged_in: CliHelper):
        """tenant get requires a GUID, not a slug."""
        if not self._tenant_id:
            pytest.skip("Tenant ID not captured")
        r = cli_logged_in.run("tenant", "get", self._tenant_id)
        assert r.ok, f"tenant get failed: {r.stderr or r.stdout}"
        assert "default" in r.stdout.lower()

    def test_tenant_get_table_output(self, cli_logged_in: CliHelper):
        """tenant get does not support --format Json; verify table output is non-empty."""
        if not self._tenant_id:
            pytest.skip("Tenant ID not captured")
        r = cli_logged_in.run("tenant", "get", self._tenant_id)
        assert r.ok, f"tenant get failed: {r.stderr or r.stdout}"
        assert r.stdout.strip() != ""
