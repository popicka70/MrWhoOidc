# E2E Test Failure Remediation Plan - 2026-07-23

> Source: `e2e/test-output-run.log`
>
> Result: **478 passed, 8 failed, 13 skipped, 105 warnings** in 22m 19s.

## Goal

Return the full suite to zero failures without hiding regressions behind longer
timeouts, broader skips, or weaker OIDC validation. The expected first green
baseline is **486 passed, 0 failed, 13 explicitly justified skips**. Skips and
TLS warnings should then be reduced as a separate coverage-quality task.

## Triage Summary

| Priority | Failure | Current classification | Confidence |
|---|---|---|---|
| P0 | MFA confirmation | Product PRG bug plus test cleanup bug | High |
| P0 | Rate-limit `Retry-After` | Competing limiter implementations | High |
| P1 | Linked-account external sign-in | External token-exchange path; cause not captured | Medium |
| P1 | Platform external sign-in | Confirmed external token-exchange failure | High |
| P1 | Logout redirect | Test does not follow a 302 to `/logout/final` | High |
| P1 | Back-channel logout | Synthetic callback DNS leaks into navigation | High |
| P1 | Tenant domain claim | Test reads stale DOM and ignores the CLI result | High |
| P2 | OIDC demo login | Likely cascade from MFA state leakage; recheck first | Medium |

Do not start by increasing Playwright timeouts. Three navigation failures occur
after form submission, but the run does not record the final URL or error page.
A longer wait only makes the suite slower if the browser is already on a
terminal error or MFA challenge page.

## Phase 1: Remove Test Cascades

### 1. Persist MFA confirmation and guarantee cleanup

**Evidence**

- [`Pages/Mfa/Index.cshtml.cs`](../MrWhoOidc.WebAuth/Pages/Mfa/Index.cshtml.cs)
  assigns `StatusMessage`, then redirects to the same page. The ordinary page
  property does not survive that post-redirect-get cycle.
- The test fails before entering the `try/finally` that disables TOTP. The
  shared admin remains MFA-enabled, so later password-only login tests can land
  on `/LoginTotp` and time out.

**Changes**

1. Persist the success notification across the redirect with Razor Pages
   `TempData`. The user should receive confirmation after the POST.
2. In [`test_account_pages.py`](../e2e/tests/test_account_pages.py), wrap the
   entire MFA mutation in `try/finally`; cleanup must run even when the first
   post-confirm assertion fails.
3. Keep the stable `data-testid="mfa-status-message"` selector and assert both
   the message and the durable `Disable TOTP` state.

**Check**

```bash
cd e2e
.venv/bin/python -m pytest tests/test_account_pages.py::TestMfaPage::test_mfa_confirm_completes_setup_and_login_uses_totp_challenge -v --tb=short
```

Run it twice. The second run must start from password-only login even if the
first run is made to fail after enabling TOTP.

### 2. Re-run the OIDC demo after MFA cleanup

[`test_example_apps.py`](../e2e/tests/test_example_apps.py) uses the same admin
credentials and executes after the MFA test leaked enabled TOTP state.

```bash
cd e2e
.venv/bin/python -m pytest \
  tests/test_account_pages.py::TestMfaPage::test_mfa_confirm_completes_setup_and_login_uses_totp_challenge \
  tests/test_example_apps.py::TestExampleOidcDemo::test_login_flow_reaches_secure_page \
  -v --tb=short
```

If the demo still fails, capture `page.url`, page title, a bounded body excerpt,
and the `oidcdemo` container logs. Inspect the demo callback and correlation
cookie only after that evidence is available.

## Phase 2: Correct Contract Mismatches

### 3. Follow both forms of the two-step logout response

The OP can return either an HTML front-channel page or a redirect whose
`Location` is `/logout/final?ref=...`. The current test follows `/logout/final`
only for HTTP 200. This run returned HTTP 302 to `/logout/final`, which the test
incorrectly treated as the final RP redirect.

In [`test_logout_flows.py`](../e2e/tests/test_logout_flows.py):

1. Extract a helper that resolves the end-session chain without automatically
   following arbitrary external redirects.
2. If either HTML or a 302/303 points to `/logout/final`, request that local URL
   once.
3. Assert that the resulting redirect targets the registered logout URI and
   preserves `state`.
4. Retain the unregistered-redirect negative test.

```bash
cd e2e
.venv/bin/python -m pytest tests/test_logout_flows.py -v --tb=short
```

### 4. Remove synthetic DNS from the back-channel enqueue test

The failure is `ERR_NAME_NOT_RESOLVED` for `e2e-proto.test` while navigating
through `/connect/endsession`. The test's purpose is to verify durable outbox
enqueue, not public DNS or the RP landing page.

In [`test_backchannel_logout.py`](../e2e/tests/test_backchannel_logout.py):

1. Register a reachable local `post_logout_redirect_uri` for this client, or
   use an explicit Playwright route whose invocation is asserted.
2. Prefer the local URI if interception remains unreliable across the
   intermediate front-channel page.
3. Keep the synthetic back-channel receiver URI. Delivery may fail, but enqueue
   is the behavior under test.
4. Replace setup-dependent skips with assertions after provisioning succeeds,
   and keep cleanup in `finally`.

```bash
cd e2e
.venv/bin/python -m pytest tests/test_backchannel_logout.py -v --tb=short
```

### 5. Observe domain verification after the CLI mutation

The CLI command exists. The test ignores `r.ok`, then asserts against the row
rendered before the mutation. A successful API update cannot alter that DOM
without a reload.

In [`test_tenant_domain_claims.py`](../e2e/tests/test_tenant_domain_claims.py):

1. Capture `tenant claim verify --domain <domain> --yes` and fail with safe
   stderr/stdout when `r.ok` is false.
2. Reload `/admin/domain-claims` after success and reacquire `claim_row`.
3. Poll only if the admin API is genuinely eventually consistent.
4. Keep `.example` and explicit admin verification; do not add a production
   bypass for reserved test domains.

```bash
cd e2e
.venv/bin/python -m pytest tests/test_tenant_domain_claims.py -v --tb=short
```

## Phase 3: Unify Token Rate Limiting

The token endpoint has two independent enforcement layers:

- [`DistributedRateLimiterMiddleware.cs`](../MrWhoOidc.WebAuth/Infrastructure/DistributedRateLimiterMiddleware.cs)
  permits 100 normal token requests or 40 exchanges per minute and writes
  `Retry-After` plus `X-RateLimit-*` headers.
- [`EndpointMappingExtensions.cs`](../MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs)
  attaches both `rl-token` and `rl-token-exchange` policies.
  [`RateLimitingExtensions.cs`](../MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/RateLimitingExtensions.cs)
  gives `rl-token` a 30-request limit and has no rejection callback that writes
  `Retry-After`.

The first 429 can therefore come from ASP.NET rate limiting at request 31,
before the distributed limiter reaches 100. That explains the observed 429
without `Retry-After`.

**Changes**

1. Make one implementation authoritative for `/token`, selecting the normal or
   token-exchange budget from `grant_type`.
2. Preserve Redis-backed enforcement and a secure in-memory fallback. Do not
   leave Redis-free deployments unlimited.
3. Remove the contradictory double `.RequireRateLimiting(...)` metadata.
4. Emit a positive integer `Retry-After` and consistent limit, remaining, and
   reset headers for every rejection.
5. Add integration tests for both grant classes, per-client partitioning,
   headers, and Redis-unavailable fallback.
6. Make [`test_rate_limiting.py`](../e2e/tests/test_rate_limiting.py) verify the
   supported contract instead of duplicating a competing limit.

```bash
cd e2e
.venv/bin/python -m pytest tests/test_rate_limiting.py -v --tb=short
```

Acceptance requires the first 429 to occur at the configured budget and carry
all documented headers.

## Phase 4: Diagnose External Token Exchange Once

The linked-account and platform-provider tests both submit credentials to the
same upstream authority and fail while returning to the local OP. The platform
test is known to land on `/auth/external/error?code=token_exchange_failed`; the
linked-account timeout occurs at the analogous post-login wait.

Treat these as one investigation until evidence separates them.

1. Make both waits terminate on either the destination or
   `/auth/external/error`.
2. On error, report final URL, safe visible error code, and provider name. Do
   not log codes, secrets, ID tokens, or access tokens.
3. Correlate with the structured warning in
   [`ExternalOidcTokenExchangeService.cs`](../MrWhoOidc.WebAuth/Handlers/External/ExternalOidcTokenExchangeService.cs),
   which records upstream status and body.
4. Reproduce both tests with the same shared fixture:

```bash
cd e2e
.venv/bin/python -m pytest \
  tests/test_account_pages.py::TestAccountLinkedAccounts::test_can_link_and_sign_in_through_upstream_provider \
  tests/test_platform_admin_pages.py::TestPlatformAdminProviders::test_root_platform_external_provider_login \
  -v --tb=short
```

Fix according to the captured response:

- `invalid_grant`: compare authorization and token-request `redirect_uri`
  values byte-for-byte and verify one-time code consumption.
- `invalid_client`: verify the provisioned secret and supported authentication
  method.
- discovery/connectivity: fix container back-channel addressing while
  preserving the externally advertised issuer.
- ID-token validation: correct fixture metadata or noncompliant validation.
  Never treat `localhost` and container DNS names as interchangeable issuers.
- throttling: resolve Phase 3 and isolate the upstream client partition.

Add a unit or integration regression test at the failing boundary before the
product fix. Keep issuer, audience, signature, nonce, state, and PKCE checks
strict.

## Phase 5: Audit Skips and Warnings

The 13 skips are not green coverage:

- 2 password-reset continuations, likely downstream of MailHog/reset setup.
- 1 tenant CRUD edit, downstream of tenant creation or lookup.
- 6 dynamic-registration tests gated by discovery or an initial access token.
- 1 mTLS certificate-bound flow gated by environment support.
- 2 public-page tests allowed to skip for multi-tenant/minimal-404 behavior.
- 1 JARM query-JWT test gated by dynamic client provisioning.

Record exact reasons with `pytest -rs`. Keep skips only for optional features
intentionally disabled in the tested profile. Once setup runs, dependent tests
should fail with its diagnostic instead of silently skipping. Stateful ordered
CRUD tests should move toward fixtures with explicit setup and teardown.

The 105 warnings are `InsecureRequestWarning` messages for localhost. Prefer
trusting the development CA. Otherwise use a narrowly scoped filter and
document why verification is disabled for this local stack.

## Delivery Order

| Change | Scope | Gate |
|---|---|---|
| PR 1 | MFA PRG and cleanup; logout; BCL callback; domain refresh | Focused files, then MFA + OIDC demo sequence |
| PR 2 | Single token limiter and header contract | Integration tests + focused E2E |
| PR 3 | External-provider diagnostics and evidence-based fix | Both provider tests together |
| PR 4 | Skip policy and localhost TLS warning cleanup | Full suite with `-rs` |

After each PR, run focused checks first. After PRs 1-3, run:

```bash
cd e2e
.venv/bin/python -m pytest -v --tb=short -rs
```

Final acceptance criteria:

- 0 failed tests.
- No test-state leakage after a mid-test failure.
- Every 429 carries documented retry metadata.
- External-provider errors expose a safe, specific reason without weakening
  protocol validation.
- Every remaining skip maps to an intentionally disabled capability.