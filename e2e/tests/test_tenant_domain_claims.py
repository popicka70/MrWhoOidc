"""E2E coverage for tenant domain claim auto-enrollment."""

from __future__ import annotations

import os
import re
import subprocess
import time
import uuid
from urllib.parse import urlparse

from playwright.sync_api import Page, expect

from utils.cli_helper import CliHelper

E2E_PREFIX = "e2e-domain"
_RUN_SUFFIX = f"{str(int(time.time()))[-6:]}{os.environ.get('PYTEST_XDIST_WORKER', '0')}"
_CLAIMED_DOMAIN = f"{E2E_PREFIX}-{_RUN_SUFFIX}.example"
_CLAIMED_EMAIL = f"member@{_CLAIMED_DOMAIN}"
_CLAIMED_PASSWORD = "DomainUser_Pass123!"


def _claim_domain(page: Page, domain: str, cli: CliHelper) -> None:
    page.goto("/admin/domain-claims", wait_until="domcontentloaded")
    expect(page.get_by_role("heading", name="Domain claims")).to_be_visible()

    page.locator("input#Input_Domain, input[name='Input.Domain']").fill(domain)
    page.locator("select#Input_EnrollmentMode, select[name='Input.EnrollmentMode']").select_option("AutoJoin")
    page.get_by_role("button", name=re.compile("Claim domain", re.I)).click()
    page.wait_for_load_state("domcontentloaded")

    expect(page.get_by_text(f"Domain claim created for {domain}.")).to_be_visible()

    # Claims are created as PendingVerification; the background DNS job cannot
    # verify .example domains. Verify via CLI to unblock the test.
    r = cli.run("tenant", "claim", "verify", "--domain", domain, "--yes")
    assert r.ok, f"tenant claim verify failed: stdout={r.stdout!r} stderr={r.stderr!r}"

    # Reload the page so the UI re-renders against the freshly-verified claim.
    page.goto("/admin/domain-claims", wait_until="domcontentloaded")
    expect(page.get_by_role("heading", name="Domain claims")).to_be_visible()
    claim_row = page.locator(f"tr:has-text('{domain}')").first
    expect(claim_row).to_be_visible()
    expect(claim_row).to_contain_text("Verified")
    expect(claim_row).to_contain_text("AutoJoin")


def _assert_duplicate_rejected(page: Page, domain: str) -> None:
    page.goto("/admin/domain-claims", wait_until="domcontentloaded")
    page.locator("input#Input_Domain, input[name='Input.Domain']").fill(domain.upper())
    page.locator("select#Input_EnrollmentMode, select[name='Input.EnrollmentMode']").select_option("AutoJoin")
    page.get_by_role("button", name=re.compile("Claim domain", re.I)).click()
    page.wait_for_load_state("domcontentloaded")

    expect(page.locator("body")).to_contain_text("already claimed")


def _assert_platform_uniqueness_rejects_second_tenant(domain: str) -> None:
    tenant_id = str(uuid.uuid4())
    claim_id = str(uuid.uuid4())
    slug = f"domain-dup-{_RUN_SUFFIX}"

    create_tenant_sql = f"""
INSERT INTO "Tenants"
    ("Id", "Slug", "Name", "IssuerUri", "Status", "CreatedAt", "MaxUsers", "MaxClients", "MaxIdentityProviders", "LicenseMode")
VALUES
    ('{tenant_id}', '{slug}', 'Domain Duplicate Tenant', 'https://localhost:8443/t/{slug}', 1, now(), 10000, 100, 10, 0);
"""
    _run_psql(create_tenant_sql, expected_success=True)

    duplicate_claim_sql = f"""
INSERT INTO "TenantDomainClaims"
    ("Id", "TenantId", "Domain", "NormalizedDomain", "Status", "EnrollmentMode", "CreatedAt", "VerifiedAt")
VALUES
    ('{claim_id}', '{tenant_id}', '{domain}', '{domain}', 'Verified', 'AutoJoin', now(), now());
"""
    result = _run_psql(duplicate_claim_sql, expected_success=False)
    assert "IX_TenantDomainClaims_NormalizedDomain" in result.stderr or "duplicate key" in result.stderr.lower(), result.stderr


def _run_psql(sql: str, *, expected_success: bool) -> subprocess.CompletedProcess[str]:
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


def _assert_discovery_routes_to_tenant(page: Page, email: str) -> None:
    page.goto("/DiscoverTenant?returnUrl=/account", wait_until="domcontentloaded")
    page.locator("input#Email").fill(email)
    page.get_by_role("button", name=re.compile("Continue", re.I)).click()
    page.wait_for_url(lambda url: "/t/default/login" in url, timeout=30_000)

    parsed = urlparse(page.url)
    assert parsed.path.lower().endswith("/t/default/login"), page.url
    expect(page.locator("input#Username")).to_be_visible()


def _submit_domain_registration(page: Page, email: str) -> None:
    page.goto("/Registrations", wait_until="domcontentloaded")
    expect(page.get_by_text("Platform Account Registration")).to_be_visible()

    page.locator("input#Input_Email, input[name='Input.Email']").fill(email)
    page.locator("input#Input_FirstName, input[name='Input.FirstName']").fill("Domain")
    page.locator("input#Input_LastName, input[name='Input.LastName']").fill("Member")
    page.locator("input#Input_Password, input[name='Input.Password']").fill(_CLAIMED_PASSWORD)
    page.get_by_role("button", name=re.compile("Submit Registration", re.I)).click()
    page.wait_for_load_state("domcontentloaded")

    expect(page.get_by_role("heading", name="Registration successful")).to_be_visible()
    expect(page.locator("body")).to_contain_text("has been added to")


def _login_via_tenant_url(page: Page, email: str, password: str) -> None:
    page.goto("/t/default/login?returnUrl=/account", wait_until="domcontentloaded")

    if urlparse(page.url).path.lower().endswith("/account"):
        return

    page.locator("input#Username").fill(email)
    page.locator("input#Password").fill(password)
    page.locator("button[type='submit']").click()
    page.wait_for_url(lambda url: "/login" not in url.lower() and "/logintotp" not in url.lower(), timeout=30_000)


class TestTenantDomainClaims:
    def test_claimed_domain_auto_enrolls_new_local_user(
        self,
        authenticated_page: Page,
        page: Page,
        cli_logged_in: CliHelper,
        record_evaluation,
    ):
        _claim_domain(authenticated_page, _CLAIMED_DOMAIN, cli_logged_in)
        record_evaluation(authenticated_page, "/admin/domain-claims", label="domain-claim-created")

        _assert_duplicate_rejected(authenticated_page, _CLAIMED_DOMAIN)
        _assert_platform_uniqueness_rejects_second_tenant(_CLAIMED_DOMAIN)
        _assert_discovery_routes_to_tenant(page, _CLAIMED_EMAIL)

        _submit_domain_registration(page, _CLAIMED_EMAIL)
        record_evaluation(page, "/Registrations", label="domain-registration-success")

        _login_via_tenant_url(page, _CLAIMED_EMAIL, _CLAIMED_PASSWORD)
        page.goto("/account", wait_until="domcontentloaded")
        expect(page.locator("body")).to_contain_text(_CLAIMED_EMAIL)

        authenticated_page.goto(f"/admin/users?search={_CLAIMED_EMAIL}", wait_until="domcontentloaded")
        expect(authenticated_page.locator("body")).to_contain_text(_CLAIMED_EMAIL)