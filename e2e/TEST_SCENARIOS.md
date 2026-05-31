# MrWhoOidc E2E Test Scenarios

Catalog of the end-to-end scenarios in [e2e/tests/](tests/), plus a coverage gap analysis
against the IdP's actual protocol/feature surface and concrete proposals for new tests.

> Companion to [README.md](README.md) (the canonical run guide). This file is a scenario
> inventory; it does not change how the suite is executed.

---

## 1. Scenario catalog

Each class is a scenario group. Numbered `test_NN_*` methods inside a class run in order and
share state (provision → exercise → assert → cleanup).

### Protocol flows — [tests/test_oidc_flows.py](tests/test_oidc_flows.py)

| Scenario class | What it covers |
| --- | --- |
| `TestAuthorizationCodeFlow` | provision client → authorize + capture code → exchange code → validate id_token claims → validate access_token claims → userinfo → refresh → revoke |
| `TestClientCredentialsFlow` | M2M token acquisition, claim validation, HTTP Basic client auth, wrong-secret rejection |
| `TestTokenExchangeFlow` | OBO/delegation: provision frontend+backend clients, configure OBO policy, acquire user token, exchange for backend token, validate delegated token |
| `TestDPoPFlow` | M2M with DPoP proof, `cnf` thumbprint binding, DPoP replay rejection |
| `TestOidcNegativeCases` | wrong secret, unsupported grant, missing audience, invalid code |

### Protocol security / advanced — [tests/test_oidc_advanced.py](tests/test_oidc_advanced.py)

| Scenario class | What it covers |
| --- | --- |
| `TestDiscoveryAndJwks` | discovery required fields, issuer match, no HTTP endpoints, supported values, standard scopes, JWKS validity, no private material, cache headers, kid↔id_token match |
| `TestTokenIntrospection` | active token, Basic auth, garbage/empty token, no-client-auth rejected, revoked token |
| `TestPkceEnforcement` | missing verifier, wrong verifier, `plain` method rejected |
| `TestAuthCodeReplay` | code replay rejected, wrong redirect_uri, cross-client code use |
| `TestRefreshTokenSecurity` | rotation returns new tokens, revoked refresh unusable, scope down-scoping |
| `TestTokenEndpointAbuse` | empty body, missing/unknown grant, nonexistent client, SQL injection, oversized values, unicode scope, duplicate params |
| `TestCrossClientAbuse` | client B cannot refresh/revoke client A's token |
| `TestPromptNone` | `prompt=none` with session succeeds; without session → `login_required` |
| `TestDPoPAdvanced` | wrong htm/htu, expired proof, wrong alg, missing jti, distinct keys per endpoint |
| `TestPushedAuthorizationRequests` | PAR push, request_uri usable, replay rejected, no-client-auth, wrong redirect_uri |
| `TestUserinfoEdgeCases` | no bearer, garbage token, standard claims, sub matches id_token |
| `TestAuthorizeEdgeCases` | unknown client, missing/unsupported response_type, well-known content-type |
| `TestTokenClaimValidation` | id_token required claims, access_token jti, M2M has no user sub, token type + expiry |
| `TestEndSession` | end_session without params, invalid id_token_hint, invalid post_logout_redirect |
| `TestRevocationEdgeCases` | revoke nonexistent token, no-client-auth, double-revoke idempotency |

### Public pages & discovery — [tests/test_public_pages.py](tests/test_public_pages.py)

`TestHomePage`, `TestLoginPage` (invalid creds, masked password, title), `TestQrLoginPage`
(renders QR image), `TestRootDiscoveryFlow` (root does not offer tenant providers; known email
routes to tenant login), `TestPrivacyPage`, `TestForgotPasswordPage`, `TestSelectTenantPage`,
`TestNotFoundPage`, `TestDiscoveryEndpoints` (discovery JSON, JWKS keys).

### Account self-service — [tests/test_account_pages.py](tests/test_account_pages.py)

`TestAccountDashboard`, `TestAccountProfile`, `TestAccountEmails`, `TestAccountWebAuthn`
(page + register button only), `TestAccountSessions`, `TestAccountConsents` (page load only),
`TestAccountLinkedAccounts` (link picker, link-mode start URL, full link + sign-in through
upstream provider, tenant provider picker), `TestAccountCreateTenant`, `TestAccountAccessDenied`,
`TestPasswordPage` (requires current password), `TestMfaPage` (TOTP QR render, full confirm +
login-with-TOTP-challenge).

### Admin UI — [tests/test_admin_pages.py](tests/test_admin_pages.py)

`TestAdminRealms`, `TestAdminClients` (+ client keys), `TestAdminProviders` (+ claim mappings,
provider keys), `TestAdminProviderMappings`, `TestAdminScopes`, `TestAdminRoles`, `TestAdminUsers`
(+ clients/emails/roles/linked sub-tabs), `TestAdminRegistrations`, `TestAdminConfigurationAudit`,
`TestAdminBackchannel` (outbox page only), `TestAdminOboSetup`, `TestAdminLegacyLicenseRedirects`,
`TestAdminBranding`, `TestAdminSettings`, `TestAdminRateLimits` (page load only). Mostly page-load
+ presence assertions with optional LLM screenshot evaluation.

### Platform admin UI — [tests/test_platform_admin_pages.py](tests/test_platform_admin_pages.py)

`TestPlatformAdminDashboard`, `TestPlatformAdminTenants` (list/create/edit/import),
`TestPlatformAdminImpersonation` (page + history), `TestPlatformAdminProviders` (list/add/edit,
root platform external-provider login), `TestPlatformAdminSettings`,
`TestLegacyPlatformLicenseRedirects`.

### CRUD via UI — [tests/test_crud_operations.py](tests/test_crud_operations.py)

`TestRealmCrud`, `TestClientCrud`, `TestScopeCrud`, `TestRoleCrud`, `TestUserCrud`,
`TestAccountProfileCrud`, `TestTenantCrud` — create + edit round-trips through the admin UI.

### CLI — [tests/test_cli_operations.py](tests/test_cli_operations.py)

~28 classes: read-only listings (profile/discovery/tenant/realm/client/scope/user),
unassigned-account lifecycle, profile management + rename validation, invitation CRUD,
realm/scope/user/client/role CRUD, M2M & OBO setup, full provisioning workflow + realm export,
client update, user-role assignment, client secrets lifecycle (create/activate/set-primary/revoke),
rotate-and-validate, client scopes, diagnostics (health/whoami/audit/rate-limits overview+events),
export/import, provider CRUD, platform provider read, tenant read.

### Tenant lifecycle

- [tests/test_tenant_enrollment.py](tests/test_tenant_enrollment.py) — `TestTenantInvitationEnrollment`: invited new user registers, signs in, appears accepted.
- [tests/test_tenant_domain_claims.py](tests/test_tenant_domain_claims.py) — `TestTenantDomainClaims`: claimed domain auto-enrolls a new local user.
- [tests/test_tenant_registration_settings.py](tests/test_tenant_registration_settings.py) — CLI configures registration settings, tenant registration page honors customization + submits, platform-only mode redirects tenant registration path.

### Example apps — [tests/test_example_apps.py](tests/test_example_apps.py)

`TestExampleRazorClient` (home + login + downstream API/OBO), `TestExampleOidcDemo`
(home + login reaches secure page), `TestExampleReactOidcClient` (home + login returns
authenticated home). Validates the dockerized sample clients stay healthy against the IdP.

---

## 2. Coverage gap analysis

Comparing the catalog above against the implemented IdP surface
(`MrWhoOidc.WebAuth` handlers, grants, middleware), the following capabilities are **implemented
but have no or only superficial E2E coverage**.

| # | Area | Implemented in | Current E2E coverage | Gap severity |
| --- | --- | --- | --- | --- |
| G1 | **Device Authorization flow** (RFC 8628) | `DeviceAuthorizationHandler`, `DeviceCodeGrantHandler`, `/device` page | None | High |
| G2 | **Dynamic Client Registration** (RFC 7591/7592) | `RegistrationHandler`, `ClientConfigurationHandler` (`/register`) | None | High |
| G3 | **Interactive consent screen** | `Consent.cshtml.cs`, `IConsentProcessor`, `prompt=consent`, `RequireConsent` | Only `/account/consents` page load | High |
| G4 | **Back-channel logout delivery** | `BackChannelLogoutEnqueuer`, outbox + retry | Only admin outbox page load | High |
| G5 | **Rate-limit enforcement (actual 429)** | `DistributedRateLimiterMiddleware`, per-endpoint policies | Only admin/CLI dashboards | High |
| G6 | **Response modes** `form_post` & `fragment` | `AuthorizeResponseGenerator` | Only implicit default (`query`) | Medium |
| G7 | **JARM** (`*.jwt` response modes) | `IJarmService` | None | Medium |
| G8 | **JAR** (signed request objects, RFC 9101) | `IJarReplayCache`, request-object validation | None | Medium |
| G9 | **Front-channel logout / `check_session_iframe`** | `CheckSessionHandler`, `FrontChannelLogoutNotifier` | None | Medium |
| G10 | **Full logout happy path** (id_token_hint + post_logout_redirect) | `EndSessionHandler`, `LogoutHandler` | Only negative cases | Medium |
| G11 | **Password reset full flow** | `ForgotPassword` / `ResetPassword` pages | Only forgot-password page load | Medium |
| G12 | **Email confirmation** | `ConfirmEmail.cshtml.cs` | None | Medium |
| G13 | **Account lockout / brute-force throttling** | login pipeline | None | Medium |
| G14 | **Security response headers** (HSTS, CSP, X-Frame-Options) | `SecurityHeadersMiddleware` | None | Medium |
| G15 | **WebAuthn full ceremony** | `WebAuthnHandler` (FIDO2) | Only page + button visibility | Medium |
| G16 | **QR login full pairing** | `QrLoginHandler`, `/auth/qr-*` | Only QR image render | Low |
| G17 | **Tenant-scoped discovery** (`/t/{slug}/.well-known`) | tenant resolution + discovery | Root discovery only | Low |
| G18 | **mTLS certificate-bound tokens** | `IMtlsThumbprintResolver` | None | Low |
| G19 | **CIBA** (feature-gated `Auth:EnableCiba`) | `CibaAuthenticationHandler`, `CibaGrantHandler` | None | Low (gated) |

---

## 3. Proposed new tests

Priorities below assume the existing fixtures (`cli_logged_in`, `oidc_client`,
`authenticated_context`, `authenticated_page`) and the session rebuild in
[conftest.py](conftest.py).

### P1 — Device Authorization flow (`tests/test_device_flow.py`)  ⟶ G1

```python
class TestDeviceAuthorizationFlow:
    def test_01_provision_device_client(self, cli_logged_in, tmp_path): ...
    def test_02_device_authorize_returns_codes(self, oidc_client):
        # POST /device/authorize -> device_code, user_code, verification_uri,
        # verification_uri_complete, interval, expires_in
        ...
    def test_03_poll_returns_authorization_pending(self, oidc_client):
        # token grant=device_code before approval -> error=authorization_pending
        ...
    def test_04_user_approves_at_device_page(self, authenticated_page):
        # navigate /device, enter user_code, approve
        ...
    def test_05_poll_returns_tokens_after_approval(self, oidc_client): ...
    def test_06_slow_down_on_fast_polling(self, oidc_client):
        # error=slow_down when polling faster than interval
        ...
    def test_07_expired_device_code_rejected(self, oidc_client): ...
    def test_08_denied_user_code_returns_access_denied(self, authenticated_page, oidc_client): ...
```

### P2 — Dynamic Client Registration (`tests/test_dynamic_registration.py`)  ⟶ G2

```python
class TestDynamicClientRegistration:
    def test_01_register_creates_client(self, oidc_client):
        # POST /register -> 201, client_id, registration_access_token, registration_client_uri
        ...
    def test_02_read_with_registration_token(self, oidc_client): ...      # GET  /register/{id}
    def test_03_update_metadata(self, oidc_client): ...                   # PUT  /register/{id}
    def test_04_delete_client(self, oidc_client): ...                     # DELETE /register/{id}
    def test_05_read_with_wrong_token_rejected(self, oidc_client): ...    # 401
    def test_06_invalid_redirect_uri_rejected(self, oidc_client): ...     # invalid_redirect_uri
    def test_07_unsupported_grant_combo_rejected(self, oidc_client): ...
```

### P3 — Interactive consent screen (`tests/test_consent_flow.py`)  ⟶ G3

```python
class TestConsentScreen:
    def test_01_provision_client_require_consent(self, cli_logged_in): ...
    def test_02_first_authorize_shows_consent(self, authenticated_page): ...      # scopes listed
    def test_03_deny_returns_access_denied(self, authenticated_page, oidc_client): ...
    def test_04_grant_persists_and_skips_next_time(self, authenticated_page): ...
    def test_05_prompt_consent_forces_reprompt(self, authenticated_page): ...
    def test_06_revoke_from_account_consents_reprompts(self, authenticated_page): ...
    def test_07_unrequested_scope_not_granted(self, authenticated_page, oidc_client): ...
```

### P4 — Back-channel logout delivery (`tests/test_backchannel_logout.py`)  ⟶ G4

```python
class TestBackChannelLogout:
    # Register a client with backchannel_logout_uri pointing at a captured endpoint
    # (testapi receiver or a local aiohttp sink fixture).
    def test_01_login_creates_session(self, authenticated_page, oidc_client): ...
    def test_02_logout_enqueues_logout_token(self, authenticated_page): ...
    def test_03_logout_token_delivered_and_valid(self, logout_sink):
        # decode JWT: aud, iss, sid/sub, events claim, jti, no nonce
        ...
    def test_04_failed_delivery_retried_from_outbox(self, logout_sink, cli_logged_in): ...
```

### P5 — Rate-limit enforcement (`tests/test_rate_limiting.py`)  ⟶ G5

```python
class TestRateLimitEnforcement:
    def test_01_token_endpoint_returns_429_when_flooded(self, oidc_client):
        # burst N requests within the window -> at least one 429 + Retry-After header
        ...
    def test_02_429_body_is_safe(self, oidc_client): ...        # no stack trace / leakage
    def test_03_separate_clients_isolated(self, oidc_client): ...
    def test_04_window_resets_after_retry_after(self, oidc_client): ...
```

> Needs a dedicated low-limit policy or env override so the flood threshold is reachable
> without destabilizing other tests. Coordinate with the rate-limit config in
> `RateLimitingExtensions`.

### P6 — Response modes + JARM + JAR (`tests/test_response_modes.py`)  ⟶ G6, G7, G8

```python
class TestResponseModes:
    def test_form_post_returns_self_submitting_form(self, authenticated_context, oidc_client): ...
    def test_fragment_mode_places_code_in_fragment(self, authenticated_context, oidc_client): ...

class TestJarm:
    def test_query_jwt_wraps_response_in_signed_jwt(self, ...): ...
    def test_jarm_jwt_validates_against_jwks(self, ...): ...

class TestJar:
    def test_signed_request_object_accepted(self, ...): ...
    def test_jar_replay_rejected(self, ...): ...
    def test_unsigned_request_when_required_rejected(self, ...): ...
```

### P7 — Logout completeness (`tests/test_logout_flows.py`)  ⟶ G9, G10

```python
class TestEndSessionHappyPath:
    def test_logout_with_id_token_hint_and_post_logout_redirect(self, authenticated_page, oidc_client): ...
    def test_session_cleared_after_logout(self, authenticated_page): ...   # prompt=none -> login_required

class TestCheckSession:
    def test_check_session_iframe_served(self, page): ...
    def test_front_channel_logout_notifies_clients(self, ...): ...
```

### P8 — Account security (`tests/test_account_security.py`)  ⟶ G11, G12, G13

```python
class TestPasswordReset:
    def test_full_reset_flow_with_token(self, page, cli_logged_in): ...   # request -> token -> set -> login
    def test_expired_or_used_token_rejected(self, page): ...

class TestEmailConfirmation:
    def test_confirm_email_with_valid_token(self, page): ...

class TestAccountLockout:
    def test_repeated_bad_passwords_lock_account(self, page): ...
    def test_correct_password_blocked_while_locked(self, page): ...
```

### P9 — Security headers (`tests/test_security_headers.py`)  ⟶ G14

```python
class TestSecurityHeaders:
    def test_hsts_present_on_https(self, oidc_client): ...
    def test_x_frame_options_or_csp_frame_ancestors(self, oidc_client): ...
    def test_no_server_version_leak(self, oidc_client): ...
    def test_content_type_options_nosniff(self, oidc_client): ...
```

### Lower priority

- **P10 WebAuthn full ceremony** (G15): drive `CDP Authenticator` virtual authenticator via
  Playwright to register a passkey and authenticate end-to-end.
- **P11 QR pairing** (G16): exercise `/auth/qr` → `/auth/qr-mobile` → `/auth/qr-confirm` polling.
- **P12 Tenant-scoped discovery** (G17): assert `/t/{slug}/.well-known/openid-configuration`
  issuer differs from root and endpoints are tenant-prefixed.
- **P13 CIBA** (G19): only if `Auth:EnableCiba` is turned on in the e2e compose stack.
- **P14 mTLS bound tokens** (G18): requires client-cert plumbing in the test HTTP client.

---

## 4. Recommended sequencing

1. **G1, G2, G3, G4, G5** (High) — these are whole protocol features / security controls with
   zero behavioral coverage today. Start with P1 (device flow) and P3 (consent) since fixtures
   already exist for both the CLI provisioning and the authenticated browser context.
2. **G6–G14** (Medium) — response modes, JARM/JAR, logout completeness, account security,
   security headers. P9 (security headers) is the cheapest (pure HTTP assertions, no new fixtures).
3. **G15–G19** (Low) — heavier infrastructure (virtual authenticator, mTLS, CIBA gating).
