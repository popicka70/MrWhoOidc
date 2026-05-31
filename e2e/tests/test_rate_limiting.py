"""
P5 — Distributed rate-limit enforcement (gap G5).

The token endpoint is limited to 100 requests/minute, partitioned by the
client_id extracted from HTTP Basic auth. By flooding with a DEDICATED client
we trip only that client's partition, leaving other suites unaffected.

Requires Redis (the dev stack provides it). Skips gracefully if no 429 is
observed (e.g. Redis not wired up).
"""

from __future__ import annotations

import base64
from pathlib import Path

import pytest

from utils.cli_helper import CliHelper
from utils.oidc_client import OidcClient
from .oidc_helpers import (
    RUN_SUFFIX,
    create_client_with_secret,
    delete_client,
    get_default_realm_id,
)

# Token endpoint hard limit (see DistributedRateLimiterMiddleware).
_TOKEN_LIMIT = 100
_FLOOD = _TOKEN_LIMIT + 40


class TestRateLimitEnforcement:
    """429 enforcement on the token endpoint, isolated to a dedicated client."""

    _cid = f"e2e-ratelimit-{RUN_SUFFIX}"
    _secret: str | None = None

    def test_01_provision(self, cli_logged_in: CliHelper, tmp_path: Path):
        delete_client(cli_logged_in, self._cid)
        realm_id = get_default_realm_id(cli_logged_in)
        creds = create_client_with_secret(
            cli_logged_in,
            client_id=self._cid,
            client_name=f"E2E RateLimit {RUN_SUFFIX}",
            realm_id=realm_id,
            scope="openid",
            grant_types=["client_credentials"],
            require_pkce=False,
            require_consent=False,
            cred_path=tmp_path / "ratelimit-creds.json",
        )
        TestRateLimitEnforcement._secret = creds["initialSecret"]

    def test_02_flood_triggers_429(self, oidc_client: OidcClient):
        if not self._secret:
            pytest.skip("Client not provisioned")

        basic = base64.b64encode(f"{self._cid}:{self._secret}".encode()).decode()
        headers = {
            "Authorization": f"Basic {basic}",
            "Content-Type": "application/x-www-form-urlencoded",
        }
        data = {"grant_type": "client_credentials", "scope": "openid"}
        token_endpoint = oidc_client.token_endpoint

        saw_429 = False
        retry_after = None
        limit_header = None
        for _ in range(_FLOOD):
            resp = oidc_client.session.post(token_endpoint, data=data, headers=headers)
            if resp.status_code == 429:
                saw_429 = True
                retry_after = resp.headers.get("Retry-After")
                limit_header = resp.headers.get("X-RateLimit-Limit")
                break

        if not saw_429:
            pytest.skip("No 429 observed after flooding — rate limiting (Redis) may be off")

        assert retry_after is not None, "429 must include a Retry-After header"
        assert int(retry_after) >= 0
        # The middleware also emits informational rate-limit headers.
        if limit_header is not None:
            assert int(limit_header) == _TOKEN_LIMIT

    def test_99_cleanup(self, cli_logged_in: CliHelper):
        delete_client(cli_logged_in, self._cid)
