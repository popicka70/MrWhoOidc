# MrWhoOidc E2E Review Notes

> Date: 2026-06-12
> Scope: Review and correct the earlier Option B follow-up work.

## Baseline used for review

The earlier broad rerun (`r8`) covered these files:

- `tests/test_response_modes.py`
- `tests/test_logout_flows.py`
- `tests/test_webauthn.py`
- `tests/test_consent_flow.py`
- `tests/test_dynamic_registration.py`
- `tests/test_oidc_advanced.py`

That run finished at:

- 107 passed
- 6 failed
- 7 skipped

Important: that 107/6/7 result was only the 6-file sweep above. It was not a roll-up across every previously failing file.

## Findings from the review

### Confirmed good fixes

These earlier changes were sound and should stay:

1. `MrWhoOidc.Cli/Commands/ClientCommand.cs`
  Added `--backchannel-logout-uri` and `--frontchannel-logout-uri`.

2. `MrWhoOidc.WebAuth/Admin/Dto/AdminDtos.cs`
  Added `BackChannelLogoutUri` and `FrontChannelLogoutUri` to `CreateClientInput`.

3. `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/AdminApiEndpointMappingExtensions.cs`
  Persisted the two logout URIs when creating clients.

4. `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs`
  Added `authorization_signing_alg_values_supported`, which is the field JARM clients actually look for.

5. `MrWhoOidc.WebAuth/Infrastructure/DistributedRateLimiterMiddleware.cs`
  Made 429 retry timing deterministic enough for the E2E suite.

6. `MrWhoOidc.WebAuth/Handlers/DeviceAuthorizationHandler.cs`
  Used the formatted `user_code` consistently in both the response and the verification URL.

7. `e2e/tests/test_backchannel_logout.py`
  Removed the duplicate `--format` flag from `run_json` usage.

8. `e2e/tests/test_account_security.py`
  Fixed the forgot-password and confirm-email routes to match how those Razor Pages are actually exposed.

9. `e2e/tests/test_device_flow.py`
  Honored the `interval` returned by the device flow when polling after `slow_down`.

### Rejected earlier conclusion: device-code secret bypass

The earlier thread introduced this server change:

- `MrWhoOidc.Auth/Services/Authentication/ClientAuthenticationService.cs`

It allowed any device-enabled client to authenticate the `device_code` grant with only `client_id`, even when that client had a secret configured.

That was the wrong fix.

Root cause:

- `tests/test_device_flow.py` claimed to provision a public client.
- The test actually called `create_client_with_secret(...)`, which created a confidential client.
- The server-side bypass masked the bad test setup by weakening confidential-client authentication.

Correction applied:

- Reverted the server-side bypass.
- Updated `tests/test_device_flow.py` to create a real public client.

Validation:

- `dotnet build MrWhoOidc.slnx -c Release` succeeded.
- `docker compose -f docker-compose.dev.yml up -d --build webauth webauth-upstream` succeeded.
- `pytest tests/test_device_flow.py -v --tb=short` passed: 9 passed.

## Remaining issues after review

### Likely real product issue: `form_post` response mode is not implemented correctly

Failing test from `r8`:

- `tests/test_response_modes.py::TestResponseModes::test_03_form_post_returns_auto_submit_form`

What the review found:

- `MrWhoOidc.WebAuth/Services/AuthorizeResponseGenerator.cs` handles `form_post.jwt` specially.
- Plain `form_post` currently falls through to the normal `/Auth/Redirect` flow.
- That produces a redirect page, not an auto-submitting HTML form.

This looks like a genuine server-side gap.

### Likely real product issue: WebAuthn credential list does not reflect registration

Failing test from `r8`:

- `tests/test_webauthn.py::TestWebAuthnRegistration::test_02_register_passkey`

Observed in the earlier run:

- Registration succeeded.
- The new credential did not appear in the account credentials list.

This still looks like a genuine product-side issue.

## Findings that were overstated or misclassified in the earlier thread

### Consent flow was probably a test-fixture problem, not a clear server bug

The earlier thread treated these as server failures:

- `tests/test_consent_flow.py::TestConsentScreen::test_02_deny_returns_access_denied`
- `tests/test_consent_flow.py::TestConsentScreen::test_03_allow_returns_code`
- `tests/test_consent_flow.py::TestConsentScreen::test_04_consent_remembered`

That conclusion was too strong.

Why:

- The browser callback host is synthetic: `https://e2e-proto.test/callback`.
- The tests already try to stub that host with `page.route(...)`.
- The failing URLs in `r8` included an explicit `:443`, which the earlier glob pattern did not clearly cover.
- The server already has explicit `prompt=consent` handling in `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs`.

Correction applied:

- Hardened the callback interception in `tests/test_consent_flow.py` and `tests/test_backchannel_logout.py` to match `https://e2e-proto.test` with or without an explicit port.

Status:

- A focused rerun was started, but the browser-backed validation did not complete within the available tool window, so this hardening change is not fully verified yet.

### The logout 302 finding was a bad test assumption

The earlier thread classified this as a server bug:

- `tests/test_logout_flows.py::TestEndSessionHappyPath::test_04_logout_redirects_to_registered_uri`

That conclusion was also too strong.

What the review found:

- `MrWhoOidc.WebAuth/Handlers/Logout/EndSessionHandler.cs` intentionally returns an intermediate HTML page.
- `MrWhoOidc.WebAuth/Handlers/Logout/FrontChannelPageBuilder.cs` sends the browser to `/logout/final?ref=...`.
- `MrWhoOidc.WebAuth/Handlers/Logout/LogoutRedirectResolver.cs` then performs the actual validated redirect to the registered RP logout URI.

So the implementation is a two-step redirect flow, not necessarily a broken one.

Correction applied:

- Updated `tests/test_logout_flows.py` to accept either:
  - a direct 302/303, or
  - the intermediate front-channel page followed by `/logout/final`.

Status:

- A focused rerun was started, but the browser-backed validation did not complete within the available tool window, so this test correction is not fully verified yet.

## Skipped tests from `r8`

The 7 skipped tests in the 6-file sweep were not all bugs:

1. `tests/test_response_modes.py::TestJarm::test_02_query_jwt_returns_signed_response`
  Still depends on fixture support beyond the discovery alias.

2. `tests/test_dynamic_registration.py::*`
  These are gated by environment/configuration and were skipped intentionally in that run.

## Current state after this review

Validated in this review:

- Device-flow secret-bypass regression removed.
- Device flow still passes end-to-end: 9/9.

Corrected but not fully revalidated yet:

- Consent callback interception hardening for `e2e-proto.test:443`.
- Logout-flow test expectation for the two-step `/logout/final` redirect.

Still likely worth fixing in product code:

- Plain `form_post` response mode.
- WebAuthn credential-list persistence/display.
