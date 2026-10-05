# Security Review - MrWhoOidc (Corrected Assessment)

**Date:** 2026-10-05  
**Baseline assessed:** `master` @ `7d54822d`  
**Method:** Independent static verification of the original assessment against current source, followed by targeted QR hardening and regression tests. This is not a production configuration audit or a claim of complete vulnerability coverage.

## Executive verdict

The original assessment is useful as a hardening checklist, but does not substantiate its HIGH vulnerabilities. Several assertions contradict existing guards, protocol semantics, or basic runtime behavior. Finding identifiers below preserve traceability to the original report; the original severity labels and proposed fixes are superseded.

The supported QR concerns are LOW-severity hardening issues: state-changing completion used GET, and possession of a QR session token allowed login cancellation without proving initiator ownership. Existing initiator-secret binding, mobile authentication, and number matching already prevent the claimed account-takeover scenario.

Do **not** weaken refresh-token family revocation, require an introspecting resource server to present the token holder's certificate, remove public GUID subject support, or force global WebAuthn user verification on the basis of the original allegations.

## Original HIGH findings

| ID | Corrected verdict | Evidence and action |
|----|-------------------|---------------------|
| H1: mTLS sender-constraint bypass | Disproven as described. | [UserInfoHandler](../MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs) rejects nonempty `cnf` without `jkt`; it does not skip the check and serve an mTLS-only token. [OpaqueTokenIntrospector](../MrWhoOidc.WebAuth/Handlers/Introspection/OpaqueTokenIntrospector.cs) and [JwtTokenIntrospector](../MrWhoOidc.WebAuth/Handlers/Introspection/JwtTokenIntrospector.cs) preserve `cnf`. Returning `active: true` with the certificate binding is not a resource-access bypass: the resource server must enforce the binding against the certificate on its resource request. mTLS-only UserInfo support is a compatibility question, not evidence of unbound acceptance. |
| H2: QR completion GET | LOW hardening concern; addressed below. | [QrLoginHandler](../MrWhoOidc.WebAuth/Handlers/QrLoginHandler.cs) already checks [QrInitiatorBinding](../MrWhoOidc.WebAuth/Infrastructure/Security/QrInitiatorBinding.cs), a separate session-specific secret not included in the QR URL. A cross-site navigation can carry a Lax cookie, but no attacker access or attacker-selected identity was established. Completion now requires an antiforgery-protected POST. |
| H3: bare `admin` policy | LOW latent concern; no active escalation identified. | [AdminAuthorizationHandler](../MrWhoOidc.WebAuth/Security/Admin/AdminAuthorizationHandler.cs) lacks an explicit tenant predicate but ordinarily inherits tenant filters; no production endpoint consuming this policy was found. It is already typed to `AdminRequirement`, so the proposed own-requirement guard is unnecessary. Explicit tenant pinning or removal before future use remains reasonable; unchanged in this focused patch. |
| H4: QR cancellation / minimal API CSRF | LOW QR hardening concern; addressed below. Broader exploit not established. | Token-holder cancellation permits login disruption, not account access. `ConfirmAsync` authenticates the user internally and checks the displayed match number. [AuthenticationAuthorizationExtensions](../MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/AuthenticationAuthorizationExtensions.cs) explicitly uses `SameSite=Lax`, so ordinary cross-site POSTs do not carry the authentication cookie. Same-site hostile-origin scenarios warrant defense-in-depth. QR writes now explicitly validate antiforgery tokens; cancellation additionally requires initiator binding. No blanket antiforgery change was made to bearer-authenticated or protocol APIs. |
| H5: non-UV WebAuthn accepted as MFA | Disproven. | [WebAuthnHandler](../MrWhoOidc.WebAuth/Handlers/WebAuthnHandler.cs) assigns non-UV assertions `acr=password`, not `acr=passkey` or `amr=mfa`. [Ciba](../MrWhoOidc.WebAuth/Pages/Ciba.cshtml.cs) and [Device](../MrWhoOidc.WebAuth/Pages/Device.cshtml.cs) do not accept `amr=webauthn` alone as MFA. TOTP-enabled users enter preauthentication. No global UV-policy change is justified by this claim. |

## Original MEDIUM findings

| ID | Corrected verdict | Evidence and action |
|----|-------------------|---------------------|
| M1: refresh tokens without `offline_access` | Confirmed issuance policy, not a demonstrated vulnerability. | [AuthorizationCodeExchanger](../MrWhoOidc.Auth/Services/Token/AuthorizationCodeExchanger.cs) issues refresh tokens independently of this scope. OIDC does not prohibit refresh tokens without `offline_access`; changing this is a product/consent-policy decision. A stolen code still faces client authentication and PKCE where applicable. [RefreshTokenService](../MrWhoOidc.Auth/Services/RefreshTokenService.cs) defaults to 15-day sliding and 30-day absolute lifetime, not simply 30 days. |
| M2: code replay revokes unrelated sessions | Confirmed availability concern. | [AuthorizationCodeExchanger](../MrWhoOidc.Auth/Services/Token/AuthorizationCodeExchanger.cs) invokes user/client revocation before the consumed-code client/PKCE checks; [RevocationService](../MrWhoOidc.Auth/Services/RevocationService.cs) scopes it within the tenant. Grant-specific revocation could narrow disruption, but needs issuance-to-grant tracking and preserved compromised-grant revocation. Deferred, not an account-access vulnerability. |
| M3: refresh race revokes winning child | Intentional reuse defense; original fix rejected. | [RefreshTokenExchanger](../MrWhoOidc.Auth/Services/Token/RefreshTokenExchanger.cs) deliberately revokes the family on reuse. The server cannot reliably identify whether the legitimate client or attacker won. [RevocationService](../MrWhoOidc.Auth/Services/RevocationService.cs) acquires the family lock. Revoking ancestors only can leave an attacker-winning child usable. Clients should serialize refreshes. |
| M4: no DPoP nonce on `/token` | Optional hardening; outage bypass disproven. | Server nonces are optional under RFC 9449. [DpopValidationHelper](../MrWhoOidc.WebAuth/Infrastructure/DpopValidationHelper.cs) validates proofs and replay uniqueness. [RedisDPoPReplayCache](../MrWhoOidc.WebAuth/Infrastructure/RedisDPoPReplayCache.cs) rejects proofs on Redis failure, rather than silently falling back in-process. [DeploymentTopologyGuard](../MrWhoOidc.WebAuth/Infrastructure/Startup/DeploymentTopologyGuard.cs) checks distributed deployment requirements. |
| M5: health-path system scope | LOW defense-in-depth concern, no breach demonstrated. | [PlatformSystemScopeMiddleware](../MrWhoOidc.WebAuth/Middleware/PlatformSystemScopeMiddleware.cs) runs after authorization, and diagnostic handlers have platform-admin policies. Endpoint-specific scopes would reduce ambient privilege; no anonymous cross-tenant data disclosure sink was identified. |
| M6: unfiltered audit list | Disproven under current routing/default filters. | [ExportImportHandler](../MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs) is tenant-admin authorized, normal audit routes resolve a tenant, and [AuthDbContext](../MrWhoOidc.Auth/Persistence/AuthDbContext.cs) permits only null-tenant rows when an optional filter lacks tenant context, not all tenants. The health/platform scope does not cover audit routes. Explicit missing-context rejection remains optional robustness. |
| M7: GUID subject fast-path | No cross-tenant exploit demonstrated. | [PairwiseSubjectService](../MrWhoOidc.Auth/Services/SubjectIdentifiers/PairwiseSubjectService.cs) supports public GUID subjects intentionally. UserInfo validates a signed token and queries tenant-filtered users; [TokenExchangeService](../MrWhoOidc.Auth/Services/TokenExchangeService.cs) additionally checks persisted access-token/user identity. Removing public-subject compatibility would be a regression. |
| M8: process-wide `AsyncLocal` | Incorrect characterization. | [TenantFilterScope](../MrWhoOidc.Auth/MultiTenancy/TenantFilterScope.cs) uses an async-flow-local depth counter, not a process-wide boolean. Disposal, async isolation, and avoiding privileged-scope inheritance by background work are appropriate safeguards; no scope-leak exploitation path was demonstrated. |
| M9: committed licensing fixture key | Test fixture, not established production compromise. | [Fixture documentation](../e2e/fixtures/README.md) explicitly excludes production/staging use. [LicenseValidator](../MrWhoOidc.Auth/Licensing/Validators/LicenseValidator.cs) trusts additional keys only when explicitly configured; trust in a test assembly does not establish production trust. Verify deployed trust configuration; do not rotate reproducible test fixtures as if they were production credentials. |
| M10: deployment footguns | Mixed operational hardening, not demonstrated MEDIUM exploitation. | Digest pinning and production-equivalent staging key protection are sensible. Top-level `AllowedHosts` differs from `ForwardedHeaders:AllowedHosts`; the original report conflated them. [Dockerfile](../Dockerfile) disables TLS checks for a local health probe. [Certification script](../tools/certification/start-self-certification.ps1) disables checks against `$BaseUrl` (localhost by default), not the OIDF suite. Actual credential reuse, key-ring deployment, and conformance status need deployment evidence. |
| M11: SFTP heredoc / password argv | LOW runner-local hygiene, not demonstrated command injection. | [Deployment workflow](../.github/workflows/deploy-web-sftp.yml) places the password in argv, not the heredoc. Shell expansion does not recursively execute command syntax embedded in expanded values. `LFTP_PASSWORD` with `--env-password` could avoid argv exposure; simply quoting the heredoc delimiter would break intended path expansions. Preserve host-key pinning. |

## Original LOW findings

| ID | Corrected verdict |
|----|-------------------|
| L1: implicit signed-token default | [TokenValidator](../MrWhoOidc.Auth/Services/TokenValidator.cs) uses IdentityModel's secure signed-token default and pins asymmetric algorithms. Explicit `RequireSignedTokens=true` would clarify intent, not repair unsigned-token acceptance. |
| L2: DPoP excludes query | [DPoP](../MrWhoOidc.Security/DPoP.cs) follows RFC 9449. DPoP does not promise query/body integrity; there is no inferred body-only endpoint restriction. |
| L3: eight-digit TOTP overflow | Disproven: `10^8` and the masked 31-bit HOTP value fit in `Int32`. [TotpService](../MrWhoOidc.Auth/Services/TotpService.cs) does not overflow for eight digits. Validation of unsupported digit counts is a separate question. |
| L4: replay-cache remove/add race | LOW robustness concern; valid-proof replay not demonstrated. The removal path is for expired entries, and retention exceeds proof validity. JAR retention includes expiration plus skew. Compare-and-remove and concurrency regressions could harden this without treating a concurrent dictionary as inherently unsafe. |
| L5: icon headers | Original claim overstated. [EndpointMappingExtensions](../MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs) already sets `nosniff`, derives content type from bytes, and supplies a download filename. [ImageContentType](../MrWhoOidc.Auth/Utils/ImageContentType.cs) permits raster types, not SVG. No script-execution path demonstrated. |
| L6: public runtime metadata | LOW informational exposure, intentionally public. Optional minimization should preserve anonymous liveness/readiness. |
| L7: historical development credentials/certificate | Removed historical material is not proof of current production compromise. Verify reuse and rotate if reused; do not reproduce historical secret values. |
| L8: tenant redirect | No open redirect established. [TenantAwareRedirectMiddleware](../MrWhoOidc.WebAuth/Middleware/TenantAwareRedirectMiddleware.cs) prefixes the request path with `/t/{slug}`; path content cannot replace the destination authority. |
| L9: React example storage/CSP | Demo hardening, not demonstrated XSS. [React example](../Examples/ReactOidcClient) uses `sessionStorage` and lacks nginx CSP; no injection source was established. Production-use guidance/CSP is reasonable separate work. |

## Other corrected claims

- Authorization metadata on export/import minimal endpoints is enforced; those endpoints are not unauthenticated.
- DPoP explicitly requires signed proofs; TOTP replay monotonicity is enforced.
- KeyGen requires authentication and fails closed without configuration outside Development.
- Certificate token binding has its own `CnfX5tS256` column, separate from `CnfJkt`.
- Contrary to the original "disproven" list, `/health` **does publish diagnostic endpoint paths**. Their results remain authorization-protected; route-name publication is not itself a meaningful breach.

## Implemented QR hardening

- `/auth/qr-complete` is POST-only and explicitly validates ASP.NET Core antiforgery tokens before reading the session or signing in. Initiator binding, authenticated session, active user, local return URL, and expiration checks remain enforced.
- Desktop polling returns `completionRequired` for platform logins; [Qr.cshtml](../MrWhoOidc.WebAuth/Pages/Auth/Qr.cshtml) submits a token-bearing form. OAuth QR flows still redirect to the RP callback with the authorization code.
- `/api/qr/confirm` and `/api/qr/cancel` validate antiforgery tokens. Mobile confirmation retains authentication, number matching, assignment, consent, and active-user checks.
- Desktop cancellation requires the initiator cookie as well as a valid antiforgery token. Missing sessions return 404; failed updates return 409 rather than success.
- Mobile Cancel is intentionally a **local decline**, not server-side session cancellation. No confirmation is sent; the initiating desktop can cancel, or the session expires. The UI explains this distinction.
- No blanket changes to admin/protocol endpoints, refresh-token revocation, subject identifiers, or WebAuthn policy were made.

## Verification

Focused coverage resides in:

- [QrInitiatorBindingTests](../MrWhoOidc.UnitTests/Security/QrInitiatorBindingTests.cs): missing/forged antiforgery tokens on all three writes, completion GET refusal, cancellation ownership, expiration, valid completion/confirmation, and platform-versus-OAuth status behavior.
- [QrConfirmIssuanceGateTests](../MrWhoOidc.UnitTests/Security/QrConfirmIssuanceGateTests.cs): existing assignment, consent, and deactivated-user gates exercised with valid antiforgery tokens.
- [QrHttpSecurityTests](../MrWhoOidc.UnitTests/Security/QrHttpSecurityTests.cs): real WebAuth endpoint metadata, rendered desktop token/form, GET 405, unprotected POST rejection, and token-bearing desktop cancellation.

Run:

```bash
dotnet test MrWhoOidc.UnitTests/MrWhoOidc.UnitTests.csproj --no-restore \
  --filter 'FullyQualifiedName~QrInitiatorBindingTests|FullyQualifiedName~QrConfirmIssuanceGateTests|FullyQualifiedName~QrHttpSecurityTests'
```

These focused tests do not constitute full browser/device or production-topology validation. Deployment-only concerns remain subject to operational verification. The [2026-10-04 assessment](oidc-idp-assessment-2026-10-04.md) remains the separate tracker for its previously recorded work.

**Result:** 36 focused tests passed, none failed or skipped. The test command builds the changed WebAuth host (including Razor Pages) and its referenced projects. Existing warnings remain in `ExternalOidcHandler`, `ExternalEmailLinkConfirmationRequiredTests`, `ClientScopeDefaultDenyTests`, and `CliFileOutputTests`; none originate in the changed code.
