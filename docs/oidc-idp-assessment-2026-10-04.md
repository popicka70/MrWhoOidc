# MrWhoOidc — OIDC IdP Assessment & Implementation Proposals

**Date:** 2026-10-04  
**Baseline:** `master` @ `a1df8f4e`  
**Method:** Static code review across five areas: authorize/token, discovery/keys/crypto, sessions/user auth, secondary endpoints, and ops/CI. No tests were run. Findings marked **✔ verified** were re-read in source by the reviewer. The other findings come with file:line evidence and should be confirmed with a failing test before they are fixed.  
**This is the single source of truth for open findings and the roadmap.** Earlier reviews, assessments and plans were retired on 2026-10-04 (they remain in git history). Their still-open findings were re-verified against the code and carried over in §2.4.

> **Phase 0 status (2026-10-04, branch `fix/phase0-security`):** C1–C18 fixed, one commit per item, each with regression tests.
> Not yet done from the Phase 0 notes:
> - Key rotation has no cross-replica advisory lock (C5).
> - Flipping the `AllowClientCredentials` default to `false` (C8) needs a migration; registered `grant_types` are enforced now.
> - The CIBA approval page binding (C11) and ID-token JWE fail-closed (C17) have no direct unit test; the surrounding logic is covered.
> - The tenant query filter still fails open when no tenant is set (D17).
> - The Medium list in §2, the carried-over findings in §2.4 (R1 is High) and Phases 1–4 are open.
> - **§2.5 (post-Phase-0 review):** 2 Critical and 9 High new findings, including gaps in C9 and C14. These go into Phase 0b.
>   - Fixed on branch `fix/security-review-2026-10-04`, one commit each with a test that fails without the fix: K1, K2 (admin create/edit, Profile), H2, H5 (also fixes the second-tenant sign-out bug in the Medium list), H6, H1 (a central `PlatformRealmWriteGuard` SaveChanges interceptor) and S-L10.
>   - Added the `Users.UserAccountId` FK (migration `LinkUserToUserAccount`, backfill tested on Postgres 16). It is the durable fix for K1/K2: account pages and login now resolve through it.
>   - Still open: H3 + R1 (admin token audience; design on its own branch), H4 (on its own branch), H7, H8, H9 and the §2.5 Medium/Low lists.
>   - Rows the backfill left unlinked keep the legacy email lookup until they are linked. A refused platform-realm write returns 500, not 403.
> - **§2.6 (third review, `97750351`):** 3 Critical and 3 High new findings. H1 is bypassed by V1 (client-credentials `sub` impersonation), and K1 is bypassed by V3 and V4 (`EnsureAsync` still adopts foreign accounts). These go into Phase 0c, starting with V1 + V2.
>   - Fixed on branch `fix/security-review-3`, one commit each, every fix with a test that fails without it: V1 + V2, V3 + V4 (+ tenant seeding, K2 residue), H8 (+ a TOTP code is required to disable MFA), V5, V6, and from the §2.6 Medium list: the `/health/*` leak, the H5 and C14 gaps, device/CIBA realm roles, `claims`-parameter email/roles, and the obfuscated provider-key export.
> - **Phase 0c remainder (branch `fix/phase0c-remaining`)**, one commit per item, with tests unless noted:
>   - **H3 (ADR-0010 phase 1):** admin APIs accept only `aud=urn:mrwho:admin-api` + scope `mrwho:admin` tokens from clients with the new `AllowAdminApi` flag (system clients only; migration `AdminApiClientFlag` enables it for `mrwho-cli-*`). `typ=at+jwt` and the tenant's issuer are enforced, and `/platform-admin` accepts only platform-tenant tokens. Refresh tokens keep their granted audience; a different resource on refresh is `invalid_target` (C9 refresh gap). The CLI requests the admin resource. `AuthOptions.AdminApiAcceptLegacyTokens` defaults to **false** (the ADR planned one release with `true`); existing CLI users must log in again.
>   - **R1:** ApiService takes its tenant only from configuration, requires access tokens, and its admin routes need the admin audience and scope. It no longer returns secret hashes. *No automated test (no ApiService test project).*
>   - **H7:** KeyGen requires OIDC sign-in (code + PKCE) and the `platform-admin` role through a fallback policy. Startup fails closed without `KeyGen:Auth` outside Development. Issuers are stamped from the signed-in user, and secret pages are `no-store`.
>   - **H9:** QR login is bound to the initiating browser (`__Host-` cookie; hash stored, migration `BindQrLoginToInitiator`), uses number matching, shows initiator context, and never renders a QR image from the query string.
>   - **Device/CIBA approval tests** (V5, C11, H8, admin gate), each confirmed to fail without its check.
>   - **`claims` parameter:** `/authorize` adds the covering scope (profile/email/roles) for claims-parameter claims when the client may request it, so the scope allow-list and consent apply. Claims are released by scope only.
>   - **Upstream IdP keys encrypted at rest** (`dp:v1:`), with an idempotent startup backfill for plaintext rows.
>   - **§2.6 Low:** `EnableDeviceAuthorizationGrant` enforced (default now `true`, the existing behaviour); DPoP `jwk` refuses private members, non-P-256 EC and RSA < 2048; client assertions capped at 10 min; admin API client writes invalidate the client cache; delegated token exchange, Account/Index and LinkedAccounts resolve accounts by FK; only `/t/{slug}/notfound` skips tenant resolution; config-audit uses the platform-admin policy; duplicate WebAuthn credential IDs are refused; password reset tokens are claimed atomically (*no unit test, relational-only path*); the CLI requires https (or loopback) and same-origin discovery endpoints; reset links use the configured public base URL (*no unit test*).
>   - **Ops:** `OIDC_PUBLIC_BASE_URL` required in `docker-compose.yml`; MailHog bound to loopback in the dev compose; `dotnet.yml` read-only token; image provenance + SBOM; no DB credentials in ApiService base config; build artifacts untracked.
> - **Phase 0d (2026-10-05, same branch): closure round.** A fresh audit of every §2 Medium, §2.4, §2.5 Medium/Low and §2.6 item against the code, then two waves of fixes. Each fix is its own commit with a regression test, except where a commit says why it has none.
>   - **New Critical, found and fixed:** `TenantAdminAuthorizationHandler` derived from `AuthorizationHandler<IAuthorizationRequirement>`, so it succeeded *every* requirement of every policy for a tenant admin. Any tenant admin passed `platform-admin`. It now handles only the tenant-admin requirements (`TenantAdminHandlerScopeTests`). The `WithOperation` markers were also inert endpoint metadata; they are now enforced requirements.
>   - **Protocol:**
>     - PKCE downgrade and verifier format.
>     - RFC 9207 `iss` on error responses.
>     - Real `response_mode=fragment`.
>     - JAR carries all signed parameters and accepts `aud=issuer`; `jwks_uri` is https-only with a size cap.
>     - Missing essential claims are omitted.
>     - OIDC step-up errors.
>     - `client_id` in access tokens; `cnf`/`act` as JSON objects.
>     - `id_token_hint` accepts ID tokens only.
>     - Userinfo encryption with alg only fails closed.
>     - Discovery advertises only `sig` algorithms.
>     - PAR uses the strict validator.
>     - Redirect URIs with userinfo, fragments or dot-segments never match.
>     - Consent Deny is a server-side POST.
>     - Prompt/JAR/PAR resume after login without loops or replay false positives.
>   - **Client auth and DCR:**
>     - One shared authenticator, with the registered method enforced, at /token, /par, /revoke, /introspect, /bc-authorize and /device/authorize. Failures are 401 `invalid_client`.
>     - `private_key_jwt` accepts `aud=issuer` and PS256; assertions are capped at 10 min.
>     - DCR redirect allow-list for POST and PUT; RFC 7592 full replace with registration-access-token rotation; DELETE revokes tokens.
>     - R8, R9, R26 and R10 (`RegistrationSource`).
>     - Bounded Argon2 verification.
>   - **Tokens:**
>     - Refresh `FamilyId` closes the reuse-detection race.
>     - Revoking a refresh token revokes its grant's access tokens.
>     - Device, CIBA and token-exchange access tokens are stored.
>     - mTLS binding is stored as `x5t#S256`.
>     - Pairwise subjects fixed for /userinfo, device, CIBA, token exchange (R4) and introspection; `sector_identifier_uri` is no longer fetched at token time.
>   - **Accounts:**
>     - Deactivated users are blocked on every sign-in path, with stamp rotation and token revocation.
>     - Operator password writes end sessions; import never writes a foreign account.
>     - TOTP replay protection, SHA1 for new enrolments, recovery codes, and TOTP failures count towards lockout.
>     - Fresh sign-in is required to add a passkey or change the email address.
>     - Atomic login counters.
>     - Logout confirmation (CSRF); endsession supports POST.
>     - Per-address throttling of reset and verification emails.
>     - Full export only for platform admins outside support sessions.
>     - OBO policy fails closed (R25); R11.
>     - Upstream `acr`/`amr` count only for trusted providers.
>     - The external IdP state is browser-bound and single-use; the front-channel `id_token` is ignored; a userinfo `sub` mismatch is rejected; `linked_immediate` is removed.
>     - Open redirects closed through one `SafeRedirect` helper.
>     - Atomic claim of invitations and reset tokens.
>   - **Tenancy and admin:**
>     - R7 default-deny client scopes (migration backfill preserves existing clients).
>     - `AllowCiba`, `AllowDeviceAuthorization` and `AllowClientCredentials` default to false for new clients.
>     - Every tenant-admin endpoint declares its operation kind, with a new `SecuritySensitiveRead`.
>     - Read-only support sessions fixed (they had never worked).
>     - R3 tenant cache, R2 DNS TXT domain verification, R18, R19, R20, R12.
>     - BCL dispatcher claims rows; BCL outbox and alerts scoped correctly.
>     - C5 key-rotation advisory lock.
>     - R1: ApiService retired (ADR-0010 phase 2).
>   - **Secrets at rest:** plaintext signing-key and TOTP secrets are backfilled and then rejected (`Security:RejectPlaintextSecrets`); upstream IdP `ClientSecret` in `ConfigJson` is encrypted.
>   - **Infra and ops:**
>     - SSRF guard (no proxy, full non-public ranges).
>     - Stored licenses re-verified.
>     - Redis outage tolerated, with replay caches failing closed (R16).
>     - Rate-limit keys tenant/client/IP qualified; R13, R14, R15.
>     - `/health` liveness and `/health/ready` readiness; `Deployment:MultiInstance` guard.
>     - KeyGen CSP without `'unsafe-inline'` (R21); non-root dev/example images; dev compose has no literal secrets; tracked dev pfx removed; nginx upstream TLS verified.
>     - SFTP host key pinned; Actions pinned to SHAs; SBOM workflow least-privilege.
>     - MCP read-only unless `--allow-writes`, and secrets go to 0600 files; CLI files created 0600; DPAPI on Windows; loopback TLS validated; https-only servers.
>     - Example secrets out of committed config; portal tokens in sessionStorage behind a CSP; per-run OIDF certification credentials; R23, R24, R27.
>   - **Migrations added this round** (all additive): `AddTokenFamilyId`, `AddTokenCnfX5tS256`, `MfaHardening`, `ClientScopeAndGrantDefaults`.
> - **Still open after Phase 0d:**
>   - **D17** (tenant query filter fails open without a tenant). It is implemented on branch `fix/wave2-tenancy-d17`, with a system-scope mechanism and the escape hatch `MultiTenancy:TenantFilterFailOpen`, but it is not merged: 29 existing tests read through tenantless contexts and need updating first.
>   - **Accepted as is:**
>     - Refresh tokens without `offline_access`: OAuth 2.0 allows it, and gating would break clients.
>     - Per-`client_id` `AuthOptions` maps not tenant-qualified: `client_id` has a global unique index, so tenants cannot collide; operators must make sure configured ids belong to the intended tenant.
>   - **Smaller follow-ups:**
>     - Login-page client branding for JAR flows (`ReturnUrlClientContextResolver`).
>     - RFC 7592 PUT does not re-sync `ClientScopes`.
>     - `PublicJwksCache` is not tenant-scoped.
>     - `ProviderSpecificConfigJson` is not encrypted, and plaintext rejection is not applied to provider secrets.
>     - Legacy self-verified domain claims are still `Verified` (product decision).
>     - macOS/Linux keychain for the CLI.
>   - **Operational:**
>     - Revoke the demo's OIDF conformance clients and secrets and re-seed them with per-run credentials.
>     - Add the `SFTP_KNOWN_HOSTS` secret.
>     - Treat the old `aspnetapp.pfx` and the example secrets in git history as public.
>     - Persist and share the DataProtection key ring.
>     - Point readiness probes at `/health/ready`.
>     - Re-run the OIDF Basic, Form Post and logout plans (error responses, fragment mode, the logout confirmation page and the claims handling changed).
>   - Phases 1–4 (features: OP session store, audit trail, GDPR, localisation, FAPI and others).

---

## 1. Executive summary

MrWhoOidc covers a wide range of protocols: code+PKCE, PAR, JAR, JARM, DPoP, CIBA, device flow, token exchange, DCR, the three logout specs, multi-tenancy and passkeys. Before Phase 0 it passed the OIDF Config, Basic, Form Post and the three logout plans against `/t/default` (last evidence 2026-06-22). Those runs must be repeated on the current code; see the readiness doc.

The weaknesses sit where conformance tests don't look:

1. **Client authentication and policy enforcement.** A `private_key_jwt` client can authenticate with only its `client_id`. Registered `grant_types` and `token_endpoint_auth_method` are not enforced.
2. **Session and logout.** There is no server-side OP session and `sid` is not stable. Back-channel logout trusts an unvalidated `id_token_hint` and notifies *every* client in the tenant.
3. **Multi-tenant background work.** Token/PAR cleanup, key rotation **and back-channel logout dispatch** only run for the default tenant.
4. **Horizontal scale.** Authorization-code metadata (`sid`, `acr`, `amr`, `idp`, mapped claims) is kept in process memory. With more than one replica, ID tokens silently lose these claims.
5. **Compliance features required by NIS2/DORA/GDPR** are missing. There are no audit events for login/token/admin CRUD, no tamper evidence or retention, no GDPR export/delete, and no cs/sk localisation.

**Recommendation:** freeze new protocol features. Run **Phase 0 (security fixes, ~2 sprints)** and **Phase 1 (session foundation + multi-tenant ops)** before FAPI or new grant types. Most Phase 0 items are small, local diffs.

---

## 2. Critical & high defects in existing features (fix first)

| # | Sev | Defect | Evidence | Fix |
|---|---|---|---|---|
| C1 | Critical ✔ | **Client impersonation.** A client with no secret hash authenticates when no secret is sent. This covers all `private_key_jwt` and mTLS clients. It affects `/token`, `/introspect`, `/revoke` and `/bc-authorize`. | `Auth/Services/ClientStore.cs:220-224`, `ClientAuthenticationService.cs:93-119`, `Handlers/Introspection/ClientAuthenticator.cs:52` | Allow "no credential" only when `TokenEndpointAuthMethod == "none"` (public client). See §2.1. |
| C2 | Critical ✔ | **Forged logout.** `/connect/endsession` enqueues BCL using `sub`/`sid` read from an **unvalidated** `id_token_hint` (or a `sid` query param). It builds signed logout tokens for **all** tenant clients that have a BCL URI. Front-channel does the same and leaks the RP list. | `Handlers/Logout/EndSessionHandler.cs:48` (enqueue happens before any validation; validation at `:137` only runs for redirect inference), `BackChannelLogoutEnqueuer.cs:39-57`, `FrontChannelLogoutNotifier.cs:19-46` | Validate the hint (signature, iss; `exp` may be expired). Notify only clients that took part in the session (needs F1). Drop the `sid` query param. Send a pairwise `sub` per client. |
| C3 | High ✔ | **PAR `request_uri` never consumed.** The handler passes the full `https://…/par/{id}`, `TryToGuid` fails silently, and the request can be replayed for 5 min. | `Handlers/AuthorizeHandler.cs:341`, `PushedAuthorizationRequestStore.cs:107-109` vs `AuthorizeRequestResolver.cs:87` (`parId` is extracted but not passed on) | Pass `parId` through the orchestration context. Make the consume atomic: `ExecuteUpdate … WHERE !Consumed`, then check rows==1, at *resolve* time rather than after code issue. |
| C4 | High | **PAR not bound / not enforceable.** The query `client_id` is never compared with `entry.ClientId`. Global `RequirePar` is only checked in the JAR branch. `Client.RequirePar` is dead config. | `AuthorizeRequestResolver.cs:119-145` | Enforce `client_id` equality, and enforce `RequirePar` (global, tenant and client) for every non-PAR request. |
| C5 | High ✔ | **Back-channel logout, token cleanup, PAR cleanup and key rotation only run for the default tenant.** Required tenant query filters hide the other tenants' rows. | `BackgroundServiceTenantHelper.cs`, callers: `BackchannelLogoutDispatcher.cs:104,167`, `ExpiredTokenCleanupService.cs:36`, `ParCleanupHostedService.cs:22`, `KeyRotationHostedService.cs:47`, `QrLoginCleanupService.cs:46`, `TenantSupportAccessCleanupService.cs:46` | Add a `ForEachActiveTenantAsync` helper and a distributed lock. See §2.3. |
| C6 | High ✔ | **Auth-code metadata is in memory.** `sid`, upstream `acr`/`amr`/`idp` and mapped claims are lost when `/token` hits another pod or after a restart. | `Auth/DependencyInjection.cs:169`, `AuthorizationCodeMetadataStore.cs:42` | Persist these fields on the `AuthorizationCode` row, which already exists and is read atomically at exchange. Delete the store. |
| C7 | High ✔ | **SSRF in DCR `sector_identifier_uri`.** The code requests the HttpClient name `"SectorIdentifierValidator"`, which is never registered, so it gets the default client with no private-IP guard. | `RegistrationHandler.cs:318`, `ClientConfigurationHandler.cs:180` vs `SectorIdentifierResolver.cs:15` (`"sector-identifier-safe"`) | Use `SectorIdentifierResolver.SafeHttpClientName`. Add a test that fails if any `CreateClient("…")` name is unregistered. |
| C8 | High | **Registered client policy is ignored at `/token`.** `GrantTypesJson`, `TokenEndpointAuthMethod` and `AllowClientSecretBasic/Post` are not enforced. `AllowClientCredentials` defaults to `true`. | `ClientAuthenticationService.cs:79-124`, `AuthDbContext.cs:2087` | Add a `ClientPolicyGuard` in the token pipeline, before grant dispatch. Change the default to `false` (needs a data migration, see §6). |
| C9 | High | **Arbitrary access-token audience.** `resource` is accepted as any absolute URI at `/authorize` and `/token`, and is not compared with what was authorized. | `AuthorizeRequestValidator.cs:123`, `AuthorizationCodeGrantHandler.cs:44-49`, `RefreshTokenExchanger.cs:90-92` | Use a per-client/tenant allow-list of resources. Token-time `resource` must be a subset of what was authorized (RFC 8707 §2.2). |
| C10 | High | **Introspection is open to any client.** When no `AudiencePolicy` is configured, everything is allowed, and only the first `aud` is checked. Together with C1 and anonymous DCR this is an oracle that reveals token contents. | `Introspection/AudiencePolicy.cs:15-41`, `JwtTokenIntrospector.cs:31` | Default deny. A caller may introspect only tokens where it is in `aud` or is a registered resource-server client for that audience. |
| C11 | High | **CIBA approval not bound to the hinted user.** Any logged-in user who knows an `auth_req_id` can approve it, and tokens are issued for *that* user. `/bc-authorize` doesn't check `AllowCiba`. | `Pages/Ciba.cshtml.cs:95,131-233`, `CibaAuthenticationHandler.cs` | Resolve the hint to a `UserId` at `/bc-authorize` (`unknown_user_id`), store it, and require a match on POST. Check `AllowCiba`. |
| C12 | High | **mTLS header spoofing.** `X-Client-Cert` is trusted from any source. | `SecurityCoreExtensions.cs:104-107`, `PipelineExtensions.cs:44-47` | Accept only from `KnownProxies`/`KnownNetworks`, strip the header otherwise, and support URL-encoded PEM (nginx/ingress-nginx). |
| C13 | High | **Private JWKs cached in Redis (HybridCache L2) in plaintext.** New keys are also inserted unprotected until their first read. | `KeyStore.cs:45-153,293,347`, `KeyRotationService.cs:90` | Cache only public keys in L2 and keep private keys L1-only. Protect keys on insert. Reject legacy plaintext after a one-off migration. |
| C14 | High | **Password reset doesn't kill sessions or tokens.** | `PasswordResetService.cs:195`, `UserAccountService.cs:136-146` | Rotate the SecurityStamp and revoke refresh tokens, as `Password/Index.cshtml.cs:98` already does. |
| C15 | High | **WebAuthn never checks the UV flag**, yet a passkey login is still issued `acr=passkey`. | `Webauthn/WebAuthnCrypto.cs:68,136`, `WebAuthnHandler.cs:277` | Enforce UV when `UserVerification=required`. Emit `acr=passkey` only when UV=1. |
| C16 | High | **Account takeover through external linking.** The link confirmation is a GET with no local re-authentication. Platform logins link immediately when the upstream asserts `email_verified`. | `ExternalOidcHandler.cs:474-525`, `ExternalOidcUserProvisioner.cs:212` | Require local re-authentication (password/passkey) on POST before linking, and keep a per-provider "trust email_verified" flag that defaults to off. |
| C17 | High | **JWE fails open.** If client JWKS fetch fails or the alg is unsupported, a plaintext ID token/userinfo is issued to a client that registered for encryption. | `AuthorizationCodeExchanger.cs:57-79`, `JarmService.cs:144-169`, `UserInfoHandler.cs:636-640` | Fail closed with `server_error` and an audit event. |
| C18 | High | **Device endpoint never authenticates confidential clients.** It runs `ClientSecrets.Any()` on a query without `Include`, so the result is always false. | `DeviceAuthorizationHandler.cs:59-69` | Use the shared `ClientAuthenticationService`. Check `AllowDeviceAuthorization` and the requested scopes at `/device/authorize`. |

### Medium

- **PKCE:** downgrade not rejected (verifier sent but no challenge stored); verifier length and charset not validated. See `AuthorizationCodeExchanger.cs:109`.
- **RFC 9207:** `iss` missing on authorization *error* responses (`AuthorizeResponseGenerator.cs:64-89`).
- **`response_mode=fragment`** is delivered in the query.
- **Pairwise:** `/userinfo` returns 401 for pairwise `sub` (`UserInfoHandler.cs:319`); the device flow leaks the raw id; `sector_identifier_uri` is fetched on every token request.
- **Refresh tokens:** issued without `offline_access` gating (`AuthorizationCodeExchanger.cs:650`).
- **RFC 9068:** `client_id` claim missing from code/refresh access tokens.
- **Client-auth errors:** should be 401 `invalid_client` (RFC 6749 §5.2), not 400 `unauthorized_client`.
- **`private_key_jwt`:** must accept `aud = issuer`; PS256 should be allowed.
- **JAR:** silently drops `prompt`, `max_age`, `acr_values`, `claims`, `login_hint`, `id_token_hint`, `ui_locales` (`RequestObjectValidator.cs:277-289`); `aud` must also accept the issuer.
- **`claims` parameter:** an unavailable essential claim returns an error (forbidden by OIDC Core §5.5.1).
- **Step-up:** `insufficient_user_authentication` is returned from `/authorize`. The authorize endpoint should return `login_required` / `interaction_required`.
- **Discovery:** may advertise `RSA-OAEP` as a signing alg (no `Use=="sig"` filter, `DiscoveryHandler.cs:159-165`); `pairwise` is always advertised.
- **mTLS-bound tokens:** `x5t#S256` is stored in the `CnfJkt` column, so it is reported as `cnf.jkt`.
- **Revoking a refresh token** doesn't revoke its family or access tokens. Device, CIBA and token-exchange JWT access tokens are not stored, so they can't be revoked.
- **Token exchange:** an unconfigured policy allows any audience. The `allowedCallers` check is tautological (`OboPolicyService.cs:44-46`).
- **DCR:**
  - redirect validation is a scheme denylist; PUT skips it entirely (`javascript:` accepted);
  - DCR clients get `AutoApprovalMode.All`;
  - registration access tokens are not rotated;
  - DELETE doesn't revoke tokens or invalidate the 15-min client cache.
- **Rate limiting:** the `/token` key uses the client_id *before* authentication and isn't tenant-qualified, so another caller can drain a client's budget. Login rate limiting is a non-atomic read-modify-write. `ReadFormAsync().GetAwaiter().GetResult()` appears in the limiter.
- **Logout CSRF:** `/connect/endsession` is GET only, with no confirmation when there is no hint.
- **Deactivated users** can still sign in via WebAuthn or QR.
- **TOTP:**
  - no replay protection (last step not stored);
  - no recovery codes;
  - `algorithm=SHA256` in the otpauth URI, which Google Authenticator ignores, so codes don't match.
- **Open redirect:** the backslash variant `/\evil.com` is accepted (`LocalLogoutHandler.cs:48`, `FederatedLogout.cs:242-248`).
- **Probable bug:** `SecurityStampCookieValidator` compares a per-tenant `User.Id` with `UserAccounts.Id`, which may sign users out in their second tenant.
- **Redis:** a Redis outage at startup crashes the app (no `abortConnect=false`). The DPoP/JAR replay caches throw a 500 at runtime. Without Redis they silently fall back to in-memory, which is unsafe with multiple replicas.

### 2.1 Sketch — C1 + C8 client authentication

```csharp
// ClientAuthenticationService — single entry point used by /token, /par, /introspect, /revoke, /device, /bc-authorize
var method = client.TokenEndpointAuthMethod ?? DefaultMethodFor(client); // never infer "none" from missing secrets
var presented = DetectPresentedMethod(request);                          // basic | post | private_key_jwt | tls | none
if (presented.Count > 1) return Fail(InvalidClient("multiple client authentication methods"));
if (presented.Single() != method) return Fail(InvalidClient("auth method not registered for client"));

return method switch
{
    "none"                         => client.IsPublic ? Ok(client) : Fail(InvalidClient()),
    "client_secret_basic" or
    "client_secret_post"           => await clientStore.VerifySecretAsync(client, secret, ct),   // false when no secret configured
    "private_key_jwt"              => await assertionValidator.ValidateAsync(client, assertion, acceptedAudiences: [issuer, tokenEndpoint], ct),
    "tls_client_auth" or
    "self_signed_tls_client_auth"  => mtls.Validate(client, http.Connection.ClientCertificate),
    _                              => Fail(InvalidClient()),
};
```

Then put a `ClientPolicyGuard.EnsureGrantAllowed(client, grantType)` in front of the grant dispatcher in `TokenHandler`. Return **401** with `WWW-Authenticate: Basic` when Basic was attempted.

**Failure modes to test:**
- a `private_key_jwt` client sending only `client_id`;
- a confidential client sending both Basic and a `client_secret` in the body;
- a client whose Basic `client_id` differs from the form `client_id`;
- a public client trying `client_credentials`.

### 2.2 Sketch — C6 persist code metadata

Add nullable columns to `AuthorizationCode`: `Sid`, `UpstreamIdp`, `UpstreamAcr`, `UpstreamAmr`, `MappedClaimsJson` (jsonb), `ResourcesJson` and `AuthTime`. `AuthorizationMetadataService.PopulateMetadataAsync` writes them in the same transaction that issues the code. `AuthorizationCodeExchanger` reads them from the row it already claims atomically. Delete `InMemoryAuthorizationCodeMetadataStore`. All the new columns are nullable, so the migration is additive and safe during a rolling deploy.

### 2.3 Sketch — C5 tenant-aware background work

```csharp
internal static class BackgroundServiceTenantHelper
{
    public static async Task ForEachActiveTenantAsync(
        IServiceScopeFactory scopes, string jobName, Func<IServiceProvider, CancellationToken, Task> work,
        ILogger logger, CancellationToken ct)
    {
        List<TenantContext> tenants;
        using (var s = scopes.CreateScope())
            tenants = await s.ServiceProvider.GetRequiredService<AuthDbContext>().Tenants
                .IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Status == TenantStatus.Active)
                .Select(t => new TenantContext { TenantId = t.Id, Slug = t.Slug, Name = t.Name, IssuerUri = t.IssuerUri, IsMultiTenantMode = true })
                .ToListAsync(ct);

        foreach (var tenant in tenants)
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(tenant);
            try { await work(scope.ServiceProvider, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogError(ex, "{Job} failed for tenant {TenantSlug}", jobName, tenant.Slug); } // isolate tenant failures
        }
    }
}
```

Wrap key rotation and BCL dispatch in a **Postgres advisory lock** (`pg_try_advisory_lock(hashtext('key-rotation'))`) so that only one replica runs them. Cleanup jobs are idempotent and don't need the lock. Mark `TrySetDefaultTenantContextAsync` `[Obsolete]`. Also make the tenant query filter **fail closed** when `TenantFilterTenantId == null`, outside an explicit `IgnoreQueryFilters()` scope (`AuthDbContext.cs:1468`).

---

### 2.4 Findings carried over from retired reviews (re-verified 2026-10-04)

These come from the retired security, code, multi-tenancy, delegated-access and DCR documents. Each was confirmed open in the current code.

| # | Sev | Finding | Evidence | Fix |
|---|---|---|---|---|
| R1 | High ✔ | **ApiService admin API lets the caller pick the tenant.** `X-Tenant-Id` / `X-Tenant-Slug` headers take precedence over the token's tenant claims. The `admin` policy checks only realm+role, with `ValidateAudience=false`, so any admin-role token from the issuer acts on any tenant. | `MrWhoOidc.ApiService/Program.cs:38-40,66-72,149-169` | Take the tenant from the token only. Ignore headers, or allow them only for a platform-admin role. Validate the audience. |
| R2 | Medium | **Domain claims can be self-verified** (the DNS proof is a TODO). A tenant admin can squat a domain, and auto-join then pulls that domain's sign-ups into their tenant. | `TenantDomainClaimService.cs:85-98,141,175`, `AdminApiEndpointMappingExtensions.cs:2914-2938`, `Pages/Registrations/Index.cshtml.cs:358-372` | Require a DNS TXT challenge before `Verified`. Platform-admin override with audit. |
| R3 | Medium | **Suspended/deleted tenants keep resolving for up to 5 min on other pods.** The per-pod cache is never invalidated, and the invalidation key (`tenant:slug:`) does not match the resolver key (`tenant:`). | `TenantResolver.cs:40-41,147-177`, `TenantService.cs:102-109` | Fix the key. Invalidate through HybridCache tags so the change propagates across pods. |
| R4 | Medium | **Token exchange emits the raw internal user id as `sub`**, bypassing pairwise subjects. | `TokenExchangeService.cs:573` | Use `IPairwiseSubjectService` for the target client. |
| R5 | Medium | **`cnf` / `act` are likely serialized as JSON strings, not objects** (built with `JsonSerializer.Serialize` without `JsonClaimValueTypes.Json`). Third-party RSs would reject them. Unconfirmed: add a test on the real `JwtService` output. | `ClientCredentialsTokenFactory.cs:103-111`, `DeviceCodeTokenFactory.cs:122`, `AccessTokenClaimBuilder.cs:51-52`, `TokenExchangeService.cs:576,603` | Emit them as JSON objects and add a serialization test. |
| R6 | Medium | **Google `hostedDomain` is only sent as `hd=` and never enforced**, and Entra `common`/`organizations` has no tenant allow-list. | `Pages/Admin/Providers/Add.cshtml.cs:480-482`, `ExternalOidcTokenValidator.cs:126-160` | Validate the `hd` / `tid` claims against the configured value or list. |
| R7 | Medium | **A client with no scope assignments may request any scope** (except `tenants`). New and DCR clients are allow-all. | `AuthorizeRequestValidator.cs:113-120` | Default deny. Seed explicit scopes for clients. |
| R8 | Low | RFC 7592 PUT merges instead of replacing omitted fields. | `ClientConfigurationHandler.cs:118,140,225-235` | Full replace semantics. |
| R9 | Low | DCR accepts `private_key_jwt` with neither `jwks` nor `jwks_uri`. | `RegistrationHandler.cs:214-219,297-303` | Reject with `invalid_client_metadata`. |
| R10 | Low | No record that a client was created via DCR (audit provenance). | `AuthDbContext.cs` `Client` | Add `RegistrationSource` plus an audit event. |
| R11 | Low | Delegated-access grants cannot be revoked/declined while `EnableDelegatedAccess=false`. | `IDelegatedAccessGrantService.cs:414,523-533` | Always allow revoke/decline. |
| R12 | Low | No test enforces that every tenant-admin endpoint has a read/write `.WithOperation` marker (read-only support access relies on it). | `UnitTests/Security/TenantSupportAccessTests.cs` | Add a reflection test over the endpoint metadata. |
| R13 | Low | Token-exchange rate-limit key is not tenant-qualified. | `RedisTokenExchangeRateLimiter.cs:44` | Key by tenant + client. |
| R14 | Low | `subject_token_type` is not validated against the supported set (RFC 8693 §2.2.1). | `TokenExchangeService.cs:97-98` | Return `invalid_request` for unsupported types. |
| R15 | Low | Raw `x-correlation-id` header is logged unvalidated in token exchange. | `TokenExchangeGrantHandler.cs:149-152` | Validate/limit length, or hash it. |
| R16 | Low | The Redis DPoP nonce key contains the raw client IP (the in-memory store hashes it). | `DPoPNonceStore.Redis.cs:41` vs `MrWhoOidc.Security/DPoP.cs:317` | Hash the IP. |
| R17 | Low | Fire-and-forget `Task.Run` to record secret usage on every secret auth. | `ClientStore.cs:159` | Use a background queue, or update inline. |
| R18 | Low | Production `/bootstrap` seeds demo clients (localhost redirects) into the real tenant. | `BootstrapEndpointMappingExtensions.cs:139`, `Seeder.cs:213-231` | Seed demo clients only in Development. |
| R19 | Low | `"*"` in the host allow-list disables host validation without explicit opt-in. | `HostAllowListMiddleware.cs:91` | Require an explicit unsafe flag. |
| R20 | Low | Provider logo is served with the uploaded `Content-Type`; the logo endpoint has no tenant filter. | `Add.cshtml.cs:283`, `EndpointMappingExtensions.cs:351` | Sniff/whitelist image types. Scope by tenant. |
| R21 | Low | CSP: CDN styles/fonts without SRI, `style-src-attr 'unsafe-inline'`; the KeyGen CSP uses `'unsafe-inline'`. | `SecurityHeadersMiddleware.cs:48-51`, `MrWhoOidc.KeyGen/Program.cs:80-81` | Self-host or add SRI. Move to nonces. |
| R22 | Low | CLI stores tokens in plaintext (no Windows ACL) and accepts any certificate on loopback. | `CliConfig.cs:71,169`, `CliServerConnection.cs:130` | Use the OS keychain / DPAPI. Pin the dev certificate. |
| R23 | Low | Client-secret metrics meter `MrWhoOidc.Auth.ClientSecrets` is never registered with `AddMeter`, so its counters are never exported. | `ClientSecretMetrics.cs:57`, `ServiceDefaults/Extensions.cs:62-63` | Register the meter. |
| R24 | Low | `.env.example` documents `RATE_LIMIT_REQUESTS_PER_MINUTE` and `SESSION_TIMEOUT_MINUTES`, which no Compose file or code reads. | `.env.example:154,158` | Remove them, or wire them to real settings. |
| R25 | Medium ✔ | OBO policy: an invalid JSON allow-list (source/target audiences, scopes, callers) parses to empty, which means "no restriction", so the policy fails open. | `OboPolicyService.cs:112-116` | Treat unparseable allow-lists as deny, and validate the JSON on save. |
| R26 | Low | Discovery/DCR auth-method mismatch: DCR accepts `none` but discovery omits it; discovery advertises `self_signed_tls_client_auth` but DCR rejects it. Blocks a clean Config/Dynamic OP run. | `DiscoveryHandler.cs:178,180,209`, `RegistrationHandler.cs` | Generate both lists from one source. |
| R27 | Low | The rate-limiting admin dashboard and the `/admin/api/rate-limits/*` endpoints return placeholder data (zeros, empty lists). | `Admin/Api/RateLimitingEndpoints.cs` | Wire them to the limiter metrics, or remove the dashboard. |

**Phase placement:** R1 goes into Phase 0 (it is a cross-tenant privilege escalation). R2–R7 and R25 go into Phase 1, and the rest into Phase 2 hygiene.

### 2.5 Post-Phase-0 security review (2026-10-04, `master` @ `366403ae`)

A second static review ran after Phase 0 merged. It had two goals: check that the C-fixes have no bypasses, and cover areas this document had not reviewed: the account↔tenant model, the admin authz surface, end-user flows, KeyGen, CLI/MCP, infra and CI. **✔** marks findings re-read in source by the reviewer. The others have file:line evidence and should be confirmed with a failing test.

**Root cause shared by K1, K2, H2 and S-L10:** in several places the global `UserAccount` is resolved from a per-tenant `User` by matching **email or username text** instead of `UserTenantMembership.UserAccountId`. A single resolver by foreign key closes all four.

#### Critical

| # | Sev | Finding | Evidence | Fix |
|---|---|---|---|---|
| K1 | Critical ✔ | **A tenant admin can take over any global account.** Admin "add user" checks uniqueness only within the tenant. `UserAccountProvisioner.EnsureAsync` then links the *global* account found by username/email and overwrites `Email`, `TotpSecret`=null and `TotpEnabled`=false. Password reset to the attacker's email follows. The same overwrite also happens on registration approval and external provisioning. | `Auth/Services/UserAccountProvisioner.cs:44-46,73-81`, `Pages/Admin/Users/Add.cshtml.cs:58,85`, `RegistrationService.cs:228`, `ExternalOidcUserProvisioner.cs:468,531` | Never link an existing account by username/email during admin create (fail on a global username/email collision; invitations are the way to add existing people). Never copy email or TOTP onto an existing account. |
| K2 | Critical ✔ | **Unverified email change leads to takeover of another account.** Profile sets `User.Email` with a per-tenant-only uniqueness check. `/Mfa`, `/Password` and `/LoginTotp` then resolve the account **by email**, so they can disable or plant TOTP. They can also set a password without knowing the current one when the victim's `PasswordHash` is empty. | `Pages/Account/Profile.cshtml.cs:66-86`, `Pages/Mfa/Index.cshtml.cs:186-191`, `Pages/Password/Index.cshtml.cs:43,114-148`, `Pages/LoginTotp.cshtml.cs:59` | Resolve via the membership foreign key. Make an email change pending until confirmed, and require re-authentication. |

#### High

| # | Sev | Finding | Evidence | Fix |
|---|---|---|---|---|
| H1 | High ✔ | **A default-tenant `tenant-admin` can assign itself `platform-admin`**, either by assigning the role or by renaming a role in realm `platform`. | `AdminApiEndpointMappingExtensions.cs:1634-1650,1721-1754`, `Pages/Admin/Users/Roles/Index.cshtml.cs:161-205`, `PlatformAdminAuthorizationHandler.cs:41-51` | Refuse to create, rename or assign roles in the platform realm unless the caller passes the `platform-admin` policy. |
| H2 | High ✔ | **The tenant-selection ticket is bound by a 32-bit email hash** (`SHA256(email)[..8]`). An offline collision lets an attacker sign in as another member of the same tenant without that member's password. | `Services/TenantCredentialTicketStore.cs:114-117`, `Pages/Login.cshtml.cs:284,314`, `Pages/SelectTenant.cshtml.cs:233` | Store the verified `UserAccountId` in the ticket. Require that the resolved membership matches it. |
| H3 | High ✔ | **Any RP access token works on the admin APIs.** Code-flow tokens default to `aud="api"`, which is all the admin bearer scheme requires. | `Security/ApiBearer/ApiTokenAuthHandler.cs:72`, `AuthorizationCodeExchanger.cs:169`, `AuthenticationAuthorizationExtensions.cs:40-51` | A dedicated admin audience (`urn:mrwho:admin`) plus required scopes and a client allow-list. Decide together with R1. |
| H4 | High ✔ | **The tenant membership guard is dead code.** `UseTenantResolution()` runs before `UseAuthentication()`, so `context.User` is anonymous. | `Infrastructure/Pipeline/PipelineExtensions.cs:106,133`, `Middleware/TenantResolutionMiddleware.cs:49,147-213` | Run the membership check after authentication. Add an integration test. |
| H5 | High ✔ | **C14 gap: QR login and tenant switching issue cookies without `sec_stamp`.** The validator ignores stamp-less cookies, so these sessions survive a password reset. | `Handlers/QrLoginHandler.cs:582`, `Services/TenantSwitchingService.cs:357`, `SecurityStampCookieValidator.cs:32-35` | Add the stamp at every `SignInAsync`. Fail closed after a grace period. |
| H6 | High ✔ | **C9 gap: the device flow and CIBA accept any `resource`/`audience`.** | `DeviceAuthorizationHandler.cs:91-109`, `CibaAuthenticationHandler.cs:240,280`, `DeviceCodeGrantHandler.cs:155`, `CibaGrantHandler.cs:164` | `ResourceIndicatorPolicy.IsAllowed`, returning `invalid_target` when it fails. |
| H7 | High ✔ | **KeyGen has no authentication.** Anyone who can reach it can mint signed licenses and private JWKs. | `MrWhoOidc.KeyGen/Program.cs` | Use OIDC with an admin role and a fallback policy. Record who issued each license. |
| H8 | High | **Account-level TOTP is ignored by passkey, external-IdP, device and CIBA logins**, which check only the per-tenant `User.TotpEnabled`. | `WebAuthnHandler.cs:217,247`, `ExternalOidcSessionManager.cs:182`, `Pages/Device.cshtml.cs:168`, `Pages/Ciba.cshtml.cs:195` | Use the account as the single source of truth. |
| H9 | High | **QR login is not bound to the browser that started it** (login-jacking). The confirm page shows no initiator context. `?qr=` renders an attacker-chosen image. | `Pages/Auth/Qr.cshtml.cs:85-108`, `QrLoginHandler.cs:216-269,532-582` | Use a `__Host-` initiator cookie, number matching, and never take the QR image from the query string. |

#### Medium

- **The `claims` parameter at `/token` overrides consented `ClaimsJson`** ✔, which releases PII beyond the granted scopes (`AuthorizationCodeExchanger.cs:212-222`).
- **`id_token_hint` accepts any token this server signs** (access tokens, logout tokens), with no time limit. This means forced logout from all RPs. `TokenValidator` has no `typ` check (`LogoutTargetResolver.cs:114-147`).
- **The refresh-token reuse-detection race:** the child token is linked after the family revoke can run (`RefreshTokenExchanger.cs:72-81,205-234`). Add an explicit `FamilyId`.
- **Argon2id at 128 MiB on every client-secret check** ✔ is an unauthenticated memory DoS (`ClientStore.cs:143-146,191-195`). Use SHA-256/HMAC for 384-bit random secrets.
- **External IdP `state`:** not browser-bound, has no expiry and is not single-use, which allows login CSRF (`ExternalOidcStateManager.cs:29-49`). Also, a query-string `id_token` takes priority, and the userinfo `sub` is not matched against the ID token.
- **Open redirects (new locations):**
  - `ExternalOidcHandler.cs:83→385,529,576`;
  - `WebAuthnHandler.cs:308`, where `//evil` passes `IsWellFormedUriString(Relative)`;
  - `Auth/Providers/Select.cshtml.cs:195,264`;
  - the Consent "Deny" link.
- **Host-header poisoning of the password-reset link** (`ForgotPassword.cshtml.cs:50-54`).
- **Admin full export reveals upstream IdP client secrets.** It is a GET, so it counts as a read for support sessions. Provider `ConfigJson` secrets are not encrypted at rest.
- **Deactivated users can also log in through** the MFA branch, the ticket path, the external callback, `/authorize` and refresh.
- **Phase 0 gaps:**
  - **C14:** import, seed and bootstrap write passwords without revocation.
  - **C16:** the `linked_immediate` auto-link remains.
  - **C17:** alg-only userinfo encryption fails open.
  - **C9:** refresh is not restricted to the resources authorized at grant time.
  - **C11/C18:** `AllowCiba` and `AllowDeviceAuthorization` default to `true`.
- **`OnlyExternalIdp` auto-assignment matches local logins** (`idp="local"`; `UserClientAssignmentService.cs:37`).
- **TOTP failures don't count towards account lockout.** MFA disable, passkey registration and email change require no fresh authentication.
- **Infra:**
  - MCP write tools, including `invitation_create isTenantAdmin`, are exposed to the LLM.
  - The SFTP deploy accepts any host key.
  - The website nginx sets `proxy_ssl_verify off` ✔.
  - The default `docker-compose.yml` serves the tracked `certs/aspnetapp.pfx` ✔, contradicting `certs/README.md`.
  - Conformance credentials in `tools/certification/` are likely live on the public demo (PLAUSIBLE).

#### Low

- **C1 residue:** `/par`, `/revoke` and introspection don't enforce `TokenEndpointAuthMethod`. The device endpoint doesn't use the shared authenticator.
- **JAR:** `iat`/`nbf` and `jti` are optional. `jwks_uri` accepts `http://` and has no response-size cap.
- **SSRF and key storage:**
  - SSRF denylist gaps: NAT64, `0.0.0.0/8`, `198.18/15`, `192.0.0/24`, plus `UseProxy=true` (extends F11).
  - Stored licenses are never re-verified (`LicenseService.cs:89,676`).
  - `SecretProtector` still accepts legacy plaintext keys. Rotate keys and flush Redis key caches left over from before C13.
- **DCR encryption metadata:** alg without enc is stored as null, which breaks the client (C17 fail-closed). Oversized upstream claims can 500 `/authorize` (PLAUSIBLE).
- **S-L10:** in `Login.cshtml.cs:198`, the global username maps to the per-tenant `User.Username`. This can collide after enrollment renames (PLAUSIBLE; same root cause as K1).
- **Secret exposure:**
  - ApiService serializes `SecretHash`.
  - KeyGen secret pages lack `no-store`.
  - Key files are written with a world-readable umask (`tools/KeyGenerator`, `CliFileOutput`).
  - The portal keeps tokens in `localStorage` with no CSP or SRI.
- **Enumeration and abuse:**
  - Timing and registration-status responses reveal whether an account exists.
  - Reset and verification emails have no rate limit.
  - The tenant icon `ContentType` is trusted.
  - The BCL outbox endpoint is registered with `isPlatformAdmin: true` under the tenant group.
- **CI:**
  - Actions are pinned by tag, not SHA.
  - `sbom-generation.yml` has `contents: write` on PRs.
  - `action-gh-release@v1` is deprecated.
  - The examples' `appsettings.json` files hold the dev secrets.

**Checked and clean:**
- **Codes and PAR:** atomic auth-code use and PAR consume (C3); PAR binding (C4).
- **Introspection:** default deny (C10).
- **mTLS:** trusted-proxy peer check (C12).
- **SSRF guard:** connect-time IP check on all outbound clients.
- **Code safety:** no raw-SQL injection or unsafe deserialization.
- **JWT algorithms:** an asymmetric-only allow-list; no `jku`/`x5u` following.
- **Admin pages:** antiforgery is on.
- **Containers:** they run as non-root.
- **CI:** no `pull_request_target`.
- **NuGet:** `dotnet list package --vulnerable` is clean. Go, React and Python dependencies were not scanned.

**Phase placement:** K1, K2, H1–H7 go into Phase 0b, ahead of Phase 1. Fix order: K1+K2+H2 (shared resolver) → H1, H4, H5 → H3 with R1 → H7 → Medium.

### 2.6 Third security review (2026-10-04, `master` @ `97750351`)

A third static review ran after Phase 0b (K1, K2, H1, H2, H4, H5, H6 and S-L10) and ADR-0010 merged. Five reviewers worked in parallel, one per attack surface:
- authorize, token and grants;
- client authentication, keys and DCR;
- end-user authentication and sessions;
- tenancy and admin authorization;
- KeyGen, CLI/MCP, infra and CI.

Each reviewer also looked for bypasses of the fixes that had just merged. **✔** marks findings whose key lines the editor re-read in source. The others have file:line evidence and should be confirmed with a failing test. Nothing was run. NuGet (`--vulnerable --include-transitive`) and the React example's `npm audit` are clean. `govulncheck` and `pip-audit` were not available.

**Headline:** two of the Phase 0b fixes can be bypassed.
- **H1 (platform realm writes) is bypassed by V1.** The bypass does not write the platform realm at all; it impersonates the platform admin with a client token.
- **K1 (taking over a global account) is bypassed by V3 and V4.** `UserAccountProvisioner.EnsureAsync` still adopts a foreign global account by username/email when it provisions a *new*, unlinked user. The conflict pre-check was added to the admin and Profile paths only, not to registration, external provisioning or tenant seeding.

The two shared root causes:
1. **A bearer token stands in for a user.** `"auto"` is the default scheme for the whole app, `sub` is trusted as a user id, and client tokens put `client_id` in `sub`. This covers V1, V2, and the V5 device path.
2. **`EnsureAsync` resolves accounts by text** instead of requiring an explicit, authenticated link. This covers V3, V4 and V11.

#### Critical

| # | Sev | Finding | Evidence | Fix |
|---|---|---|---|---|
| V1 | Critical ✔ | **A tenant admin can mint a token as any user in their tenant, and a default-tenant admin can become platform-admin (H1 bypass).** Client-credentials tokens set `sub = client_id`. A tenant admin may choose any `client_id`, including a user's GUID. `AllowClientCredentials` defaults to true and `aud="api"` is allowed. `ApiTokenAuthHandler` maps `sub` to `NameIdentifier`, and `PlatformAdminAuthorizationHandler` / `TenantAdminAuthorizationHandler` look roles up by that id. Attack: create a client whose `client_id` is the platform admin's `User.Id` → `client_credentials&audience=api` → `/platform-admin/api/**`. `PlatformRealmWriteGuard` authorizes the same forged principal, so the role can also be made permanent. Inside any tenant, the same token works on `/t/x/Password`, `/t/x/Mfa`, `/api/webauthn/registration/*` and `/userinfo` (see V2). Those pages write the victim's **global** account: planting a password when its hash is empty, stripping or planting TOTP, planting a passkey. That gives cross-tenant takeover for any member of the attacker's tenant. Found independently by three reviewers. | `Auth/Services/Token/ClientCredentialsTokenFactory.cs:90`, `WebAuth/Pages/Admin/Clients/Add.cshtml.cs:49,72`, `Edit.cshtml.cs:2178`, `AdminApiEndpointMappingExtensions.cs:861-889`, `Security/ApiBearer/ApiTokenAuthHandler.cs:59-66,84-93`, `Security/Admin/PlatformAdminAuthorizationHandler.cs:32-53` | (1) Make client tokens non-user: `sub = client:{client_id}`, or omit `sub` and add a `gty`/`token_use` marker. Map `sub` to `NameIdentifier` only for user-grant tokens. (2) Reject GUID-shaped `client_id`s in the UI, the API and import, or generate client ids on the server. (3) Authorization handlers also require that `sub` is an existing `Users.Id`. (4) ADR-0010 would close this on the admin APIs (its `AllowAdminApi` flag is platform-admin-only and limited to system clients), but not on the browser pages reached through V2, so (1)–(3) are needed regardless. *Correction: an earlier version of this row said a tenant admin controls `AllowAdminApi`; ADR-0010 §2 already prevents that.* Regression test: a client-credentials token whose `client_id` is the platform admin's id must get 401/403 on `/platform-admin/api/tenants`. |
| V3 | Critical ✔ | **Registration approval writes the attacker's password onto a foreign global account (K1 bypass).** An attacker who is a tenant admin of T, or anonymous on a tenant with auto-approve or an invitation link, registers `victim@x` with password P. The existence checks only cover T's users (tenant filter). `EnsureAsync` falls back to a username/email match, links the new user to the victim's account and adds a membership. `ApplyRegistrationPasswordAsync` then finds the victim's account by email and sets `PasswordHash = P` when it is empty. Victims: every account created by external-IdP provisioning or admin Add, since those start with an empty hash. Sign-in additionally needs `EmailVerified` or a realm that allows unconfirmed login. | `Auth/Services/UserAccountProvisioner.cs:46,70-76,90-108,150-152,173`, `Auth/Services/Users/RegistrationService.cs:52-55,228,294-312`, `TenantEnrollmentService.cs:244-256` | `EnsureAsync`: when an unlinked user matches `account.Id != user.Id`, **never link and never add a membership**. Throw, or create a fresh account. Existing people join only through an authenticated invitation, with the account id taken from the session. `ApplyRegistrationPasswordAsync` writes only when `account.Id == user.Id` and the account was just created. Reject a registration when any global account has that email. |
| V4 | Critical ✔ | **External auto-provisioning links an attacker-tenant user to any global account (K1/K2 bypass).** A tenant admin adds an OIDC provider they control that asserts `email=victim@x, email_verified=true`, then signs in at `/t/T`. `AutoProvisionUserAsync` creates a user, and `EnsureAsync` links it to the victim's account (no `FindConflictingAccountAsync`). The MFA gate only checks the per-tenant `User.TotpEnabled` (H8). `/t/T/Mfa` then disables the victim's **global** TOTP with no code, or plants the attacker's secret. `/t/T/Password` sets a password when the hash is empty. This strips MFA from any account, platform admins included, and fully takes over passwordless accounts. | `ExternalOidcUserProvisioner.cs:123,433-468`, `ExternalOidcSessionManager.cs:176-185`, `Pages/Mfa/Index.cshtml.cs:146-160`, `Pages/Password/Index.cshtml.cs:43-91` | Same `EnsureAsync` fix as V3. Run the conflict check in `AutoProvisionUserAsync`; on a conflict, require the authenticated link flow. Close H8. Require a fresh TOTP code (or re-authentication) to disable or re-enroll MFA. |

#### High

| # | Sev | Finding | Evidence | Fix |
|---|---|---|---|---|
| V2 | High ✔ | **A bearer access token counts as a browser login on `/authorize`, `/consent` and the account pages.** The default scheme `"auto"` forwards any `Authorization: Bearer` request to `api-bearer` app-wide. `/authorize` has no endpoint-level scheme restriction, so `http.User` is the token's subject, and a missing `auth_time` becomes "now". If an attacker holds any non-DPoP access token of victim V (a resource server, a relying party, or a leak), they can replay any RP's `/authorize` with the token and finish the login as V. Login, MFA, `acr_values` and `max_age` are all bypassed. With their own client and `offline_access`, they turn a short-lived token into a refresh token. A password reset does not stop it (the bearer path has no stamp check). Combined with V1, any tenant admin can sign in as any tenant user at any RP. ADR-0010 narrows the audience but does not close this. | `Infrastructure/ServiceRegistration/AuthenticationAuthorizationExtensions.cs:39-50`, `Handlers/AuthorizeHandler.cs:79,256-264`, `Services/AuthorizationMetadataService.cs:120-130`, `EndpointMappingExtensions.cs:192` | Make **Cookies** the default scheme. Use `api-bearer` only on `/admin/api`, `/t/{slug}/admin/api`, `/platform-admin/api` and the CLI groups, through an explicit policy. `AuthorizeHandler`, consent, account pages and `/api/webauthn/*` require the cookie authentication type. Tests: `/authorize` and `/t/x/Password` with Bearer and no cookie must redirect or return 401. |
| V5 | High | **QR, device and CIBA issuance skip user↔client assignment and consent.** `EnsureAssignedAsync` has one call site, in the normal `/authorize` path. The QR branch returns before authentication, prompt, max_age, acr, assignment, consent and the PAR consume (C3/C4 gaps: one `request_uri` can start many QR sessions). It issues `auth_time = now` with no `sid`/`acr`/`amr`, and always delivers by query without `iss`, even for JARM clients. A user who is not assigned to a restricted client can log into it through `?qr=1`, or through `/device/authorize` (allowed by default). | `Handlers/AuthorizeHandler.cs:124-128,264`, `Handlers/QrLoginHandler.cs:247-252,362-385,621-634`, `Pages/Device.cshtml.cs:201-223`, `Pages/Ciba.cshtml.cs` | Move assignment and consent into one issuance gate that every path must pass: authorize, QR confirm, device approve, CIBA approve. In the QR branch, validate after authentication and reuse `PopulateMetadataAsync` and the PAR consume. Reject an absolute `ReturnUrl` on the standalone `/auth/qr` page. |
| V6 | High ✔ | **Any tenant admin can change platform-wide identity providers** (`TenantId == null`) through three Razor pages. The provider entities use the *optional* tenant filter, and the write guard allows null-tenant rows. `ProviderKeys/Index` (add, activate, delete, publish, unpublish) has no tenant check at all. `Providers/ClaimMappings` checks only `Id`. `Providers/Edit` logo upload and clear skip `ValidateTenantAccessAsync`. Possible actions: import an attacker key as the platform provider's JAR signing key (it is published at `/providers/jwks`); delete keys (platform login DoS); add mappings that replace the default `email`/`email_verified` mapping. The admin API already validates this, so the pages are the inconsistent part. | `Pages/Admin/ProviderKeys/Index.cshtml.cs:45-270`, `Pages/Admin/Providers/ClaimMappings.cshtml.cs:49-103`, `Pages/Admin/Providers/Edit.cshtml.cs:410-480`, vs `ProviderAndBclEndpoints.cs:789-810` | Apply `ValidateProviderAccessAsync` to every handler. Make the tenant write guard refuse Modified/Deleted rows whose *original* `TenantId` is null unless the caller is a platform admin. |

#### Medium

- **Upstream-IdP private signing keys leak through provider export ✔.** This happens in `obfuscated` mode too, and for read-only support sessions.
  - `ConfigurationExportService.cs:638-647` exports `Jwk = k.Jwk`. The comment says "public key only", but these are the JAR signing keys.
  - The keys are stored in plaintext (`ProviderAndBclEndpoints.cs:492,536`).
  - **Fix:** export only public parameters (`PublicJwksCache.SanitizeSingleJwk`), and protect at rest with `ISecretProtector`.
- **Device and CIBA access tokens carry roles from every realm** in the tenant, including the platform realm, yet `realm` is set to the client's realm.
  - A resource server that authorizes on (realm, role) is fooled. ApiService checks `realm=="admin"` together with `roles∋admin` (`DeviceCodeTokenFactory.cs:126-162`).
  - **Fix:** scope roles to `client.RealmId`, as the code flow does.
- **`claims` at `/authorize` releases `email`, `email_verified`, `name` and `realm` without the matching scope.** This bypasses the client's scope allow-list and consent (`AuthorizationCodeExchanger.cs:409-443`, `ConsentService.cs:147-168`). It is separate from the known `/token` `claims` item.
  - **Fix:** map claims to their scopes, and release a claim only when its scope was allowed and consented.
- **Tenant seeding links a new tenant's admin to the platform `admin` account.**
  - The username is derived from the email local part, so `admin@customer.com` becomes `admin`. `EnsureAsync` then matches the platform admin (`TenantSeedingService.cs:163-174`, `Seeder.cs:28`).
  - The platform admin silently becomes tenant admin, and the intended admin cannot sign in.
  - **Fix:** the V3 fix, plus don't derive usernames from email.
- **H5 gap:** `EnsureAsync` creates accounts with `SecurityStamp = null`, and the validator skips stamp-less cookies. Sessions for external-IdP, admin-added and seeded accounts therefore survive a password reset (`UserAccountProvisioner.cs:50-67`, `SecurityStampCookieValidator.cs:33-36`).
  - **Fix:** always generate a stamp, backfill nulls, then reject stamp-less cookies.
- **C14 gap:** `RevokeTokensForAccountAsync` still selects users by email/username instead of the new FK. Tokens of a tenant user whose email differs survive a reset, and unrelated same-name users get revoked (`UserAccountService.cs:193-198`).
  - **Fix:** use `u.UserAccountId == accountId`.
- **K2 residue on unlinked legacy rows (PLAUSIBLE, depends on data).**
  - `FindConflictingAccountAsync` excludes the account found by the legacy *username* match. An unlinked user who shares a username can then set the victim's email in Profile, and `FindForUserAsync` falls back to that email (`UserAccountProvisioner.cs:127-139`, `UserAccountService.cs:93-94`).
  - **Fix:** account pages fail closed for unlinked users, and a one-off job links or quarantines the remaining unlinked rows.
- **Anonymous `/health/*` data leak and cheap DB load ✔.**
  - `/health/client-secrets` lists client and tenant ids across tenants, plus secret descriptions and expiry.
  - `/health/global-auth` runs four `COUNT`s over `UserAccounts` per call (totals, MFA adoption, locked-out). It has no rate limit.
  - `/health/forwarded-headers` reveals the proxy-trust posture.
  - Evidence: `AdminApiEndpointMappingExtensions.cs:213-481`. This extends F2.
  - **Fix:** only `/health` stays anonymous and returns no data; the rest go behind `platform-admin` + `rl-admin`.

#### Low

- **Authorize and grant handling:**
  - `EnableDeviceAuthorizationGrant` (default false) is never enforced; discovery hardcodes it on (`DiscoveryHandler.cs:89-93`).
  - PAR uses the weaker `AuthorizeService` validator, whose redirect allow-list fails open when empty (`ParHandler.cs:155`).
  - `redirect_uri` normalisation drops userinfo and trailing slashes, which is not exact matching per RFC 9700 (`UrlComparison.cs:341-370`).
  - The DPoP `jwk` header may carry private parameters, and there is no RSA minimum size (`MrWhoOidc.Security/DPoP.cs:241-279`).
  - Upstream `acr`/`amr` are trusted verbatim and satisfy local `acr_values` and the device/CIBA MFA gates (PLAUSIBLE; `ExternalOidcSessionManager.cs:67-75`).
- **Client authentication:**
  - Per-`client_id` maps in `AuthOptions` are not tenant-qualified, so a credential configured for tenant A's `rs-api` works for tenant B's `rs-api`. The maps are `IntrospectionMtlsCertificates`, `RevocationMtlsCertificates`, `IntrospectionPermissions` and `RequestObjectAllowedAlgorithmsPerClient`.
  - Authentication decisions use the 5–15 min client cache, and the admin API PUT/DELETE never invalidate it, so a removed mTLS thumbprint keeps working on other pods.
  - `private_key_jwt` assertions have no lifetime bound; `iat` is not required (`ClientAssertionValidator.cs:96-127`).
- **Account-resolution residue (K1 root cause):**
  - Delegated token exchange resolves the delegate by email/username (`TokenExchangeService.cs:235-256`).
  - `Account/Index` and `LinkedAccounts` still look the account up by email.
- **Tenant resolution and authorization checks:**
  - Any path ending in `/notfound` skips tenant resolution and the H4 membership check. It is not exploitable today (`TenantResolutionMiddleware.cs:165`).
  - Config-audit uses `User.IsInRole("PlatformAdmin")` instead of the policy (`ExportImportHandler.cs:1252,1328`). It currently fails closed.
- **WebAuthn and single-use tokens:**
  - WebAuthn `CredentialId` has no unique index; a duplicate can DoS a victim's passkey login.
  - Reset, confirmation and invitation tokens are consumed read-check-write rather than atomically.
- **Ops:**
  - The OIDF certification zips published on the website and in `MrWhoOidc.Web/downloads/` contain client secrets and refresh tokens for `mrwho.onrender.com/t/default`. Revoke those clients and grants.
  - `docker-compose.dev.yml` publishes every port on 0.0.0.0 with `Admin123!` and MailHog. Bind them to `127.0.0.1`.
  - The dev and example images run as root and rewrite `/etc/hosts` and the CA store.
  - The CLI/MCP accept `http://` for non-loopback servers, and the MCP returns plaintext secrets and passwords into the LLM context.
  - Production compose doesn't fail on a missing `OIDC_PUBLIC_BASE_URL` and defaults `EnforceHostAllowList=false`, which makes the reset-link host poisoning reachable.
  - `ApiService/appsettings.json` has `postgres/postgres`.
  - `dotnet.yml` has no `permissions:` block.
  - Images are published unsigned, with no provenance or scan, and compose consumes `:latest`.
  - Tracked artifacts: `keygen-dev.db`, e2e logs containing id_tokens, and two Go `.exe` binaries.

#### Status of fixed items

| Item | Status |
|---|---|
| K1 | **Bypassed:** V3, V4, and seeding (Medium) |
| H1 | **Bypassed:** V1. The guard itself holds: no `ExecuteUpdate`/raw SQL touches realms, roles or assignments. |
| K2 | Largely fixed: Mfa, Password, LoginTotp and WebAuthn go through the FK. Residue on unlinked rows (Medium). |
| H5 | Every sign-in site now stamps the cookie. Accounts with a null stamp are a gap (Medium). |
| C14 | Revocation lookup is a gap (Medium). Import/seed/bootstrap are still open. |
| C3 / C4 | Hold for `/authorize`. The QR branch skips them (V5). |
| H2, H4, H6, C1, C7, C10, C11, C12, C13, C15, C16 (confirm flow), C17, C18 | Hold. Cookies across tenants become anonymous, and bearer tokens are pinned to per-tenant keys. |

**Re-confirmed still open:** R1, H3, H7, H8, H9, D17, R2, R20, R21, R22, R23, R24, the C1 residue on `/par`, `/revoke`, `/introspect` and `/bc-authorize`, and the `TokenValidator` with no `typ` check. Also still open from the §2.5 Medium/Low lists:
- the PKCE downgrade;
- the refresh-token linking race;
- open redirects at `WebAuthnHandler.cs:308` and `ExternalOidcHandler.cs:384`;
- TOTP replay;
- the `linked_immediate` auto-link;
- the infra and CI items.

**Checked and clean:**
- **Codes, PAR and JAR:**
  - Auth codes are single-use and atomic, bound to client and redirect, and stored hashed.
  - PAR binding and consume on `/authorize`.
  - JAR: signature required, alg allow-list, `aud`/`exp`/jti replay checks.
- **Tokens:**
  - Refresh rotation with family revocation and DPoP `jkt` binding.
  - Device and CIBA codes have 256-bit entropy and are redeemed atomically.
  - The token-exchange subject must be a stored local access token.
  - Signing keys are per tenant and the issuer is pinned. There is no HS*/`none` path and no `jku`/`x5u` following.
  - Private keys are cached L1-only.
- **Outbound HTTP:** all outbound fetches use the SSRF-safe handler with redirects off.
- **Sessions and browser:**
  - Cookies are `__Host-`, Secure, HttpOnly, Lax, with an 8 h absolute lifetime.
  - Clickjacking defenses are in place.
  - WebAuthn ceremony checks: challenge, origin, rpId, UP/UV, counter, userHandle.
  - Reset, confirmation and invitation tokens are 256-bit and stored hashed.
  - Consent is challenge-bound.
- **Admin and bootstrap:**
  - Bootstrap and auto-seed are gated.
  - Every `PlatformAdmin` page has the policy.
  - Export/import handlers are authorized.
- **CI and dependencies:**
  - No `pull_request_target` and no `${{ github.event.* }}` in `run:`.
  - NuGet is clean.

**Phase placement (Phase 0c, ahead of Phase 1). Fix order:**
1. **V1 + V2 together.** Make cookies the default scheme, mark client tokens as non-user and require a real user `sub`. ADR-0010 (H3/R1) still applies on top of this.
2. **V3 + V4 + seeding.** `EnsureAsync` must never adopt a foreign account. Add the conflict check on every provisioning path.
3. **H8 together with fresh authentication for MFA changes.** Without these, V4-style chains keep working.
4. V5, then V6.
5. The Medium list.

---

## 3. Missing features — proposals

Priorities: **P0** needed for safe production use · **P1** compliance / enterprise-readiness · **P2** ecosystem / differentiation.

### F1 (P0) Server-side OP session store

This is the foundation for C2, `sid`, session listing/revocation and correct check_session.

- **Table** `OpSessions`, with columns:
  - `Id` (the `sid`, 128-bit random)
  - `TenantId`, `UserId`
  - `AuthTime`, `LastSeenAt`, `ExpiresAt` (absolute), `IdleExpiresAt`
  - `Acr`, `AmrJson`, `UpstreamIdp`
  - `IpHash`, `UserAgent`, `RevokedAt`
- **Table** `OpSessionClients(SessionId, ClientId, FirstSeenAt)`: the RPs that received tokens in this session. It drives targeted front- and back-channel logout.
- **Cookie flow:**
  - Login (password, TOTP, WebAuthn, QR, external) creates a session and puts `sid` in the auth cookie.
  - A cookie `OnValidatePrincipal` check rejects revoked or expired sessions.
  - This replaces most of the current `SecurityStampCookieValidator`.
- **Token side:**
  - The ID token and logout token carry the stable `sid`.
  - Refresh tokens get a `SessionId` FK, so logout can optionally revoke them (per-client `revoke_refresh_on_logout`, default on for public clients).
- **UI:** `Account/Sessions` lists real OP sessions and can end them.
- **Idle timeout:** configurable per tenant (default 30 min idle, 8 h absolute).
- **Effort:** M–L. Note that this changes cookie semantics, so a rolling deploy forces existing users to sign in again. Announce it.

### F2 (P0) Fail-closed multi-replica readiness

- Make Redis **required** when `replicas > 1`: add a startup check for the flag `Deployment:MultiInstance=true`.
- Set `abortConnect=false` and add a Redis readiness check.
- Move the remaining per-process rate-limit policies (authorize, login, device user-code entry, CIBA page) to the Redis limiter.
- Move the upstream logout state out of `IMemoryCache`.
- Split health endpoints into `/health/live` (process only) and `/health/ready` (DB + Redis + migrations applied).
- Run migrations as a pre-start job or init container, **not** on `ApplicationStarted`. Today the app takes traffic before the schema is ready.
- Put `/health/backchannel` and `/health/client-secrets` behind admin auth; they leak tenant and client IDs.
- Provide a reference **Kustomize base**: Deployment with requests and no CPU limit (per org guidance), PDB, HPA/VPA, readiness/liveness probes, and a NetworkPolicy allowing only ingress → app and app → Postgres/Redis.

### F3 (P1) Security audit trail fit for NIS2/DORA

- **Missing events to add:**
  - `auth.login.succeeded` / `auth.login.failed` (reason bucketed), `auth.mfa.*`, `auth.lockout`
  - `password.changed` / `password.reset`
  - `consent.granted` / `consent.revoked`
  - `token.issued` (grant, client, scopes, `jti`, no token value)
  - `session.created` / `session.ended`
  - `admin.{client|user|role|provider|tenant|key}.{created|updated|deleted}`, with a before/after diff where secrets are redacted.
- **Sink:**
  - Write to an outbox (`AuditOutbox`) asynchronously instead of calling synchronous `SaveChanges` in the request.
  - Add a **hash chain** (`PrevHash`, `Hash = SHA256(prev || canonical(event))`) per tenant for tamper evidence, plus a daily signed checkpoint.
  - Export through OTLP logs or a webhook to the SIEM.
- **Retention:** per-tenant policy (default 400 days for auth events) with a pruning job; F2's tenant iteration applies.
- **PII:**
  - Stop logging raw email (`Login.cshtml.cs:70-73`, `ExternalOidcUserProvisioner.cs:353,388`).
  - `LoggerAuditSink` must not destructure `{@Event}`.
  - Require an IP-hash pepper in production.
- Wire up or delete the dead `ImpersonationAuditLog` table.

### F4 (P1) GDPR data-subject rights

- `Account/Data`: export a JSON bundle of profile, emails, linked identities, consents, sessions, passkeys metadata and audit entries about the user.
- Self-service account deletion, with:
  - re-authentication;
  - a grace period;
  - revocation of tokens and sessions;
  - BCL fan-out;
  - pseudonymisation of the user's audit rows. Keep the hash chain intact by storing `sub` as an HMAC.
- An admin-initiated equivalent.
- Data residency: document that everything stays in the configured Postgres. Redis holds only ephemeral state once C13 is fixed.

### F5 (P1) Localisation: cs-CZ, sk-SK, en

- **Dependency:** no UI string is localised today (`PipelineExtensions.cs:105` is en-US only) and `ui_locales` is parsed but never applied. Introducing `IStringLocalizer` with `.resx` (or a PO-file provider) across `Pages/` is a sizeable but mechanical change.
- **Locale resolution order:**
  1. `ui_locales` from the authorize request (also from JAR and PAR; see the JAR param fix);
  2. the user's preference;
  3. `Accept-Language`;
  4. the tenant default.
- **What gets translated:**
  - Emails: confirmation, reset, invitation.
  - Device and CIBA consent screens.
  - Error pages.
- **Discovery:** advertise `ui_locales_supported` from the configured cultures.
- **Claims:** honour `claims_locales` for `name#cs-CZ`-style claims when a tenant provides localised profile fields.

### F6 (P1) MFA completeness

- **TOTP:** store `LastUsedStep` (replay protection); default to SHA1 for authenticator compatibility; generate 10 single-use **recovery codes**, hashed with Argon2 at low cost.
- **Email OTP** as a fallback factor. SMS should be pluggable and off by default, because NIST SP 800-63B treats SMS as a restricted authenticator.
- **MFA policy per tenant and per client:** require MFA for an `acr`; require phishing-resistant authentication (passkey with UV) for admin roles.
- **Typed `acr` values:** `urn:mrwho:acr:pwd`, `…:mfa`, `…:phr`. Optional mapping to eIDAS LoA (`low`/`substantial`/`high`) for regulated tenants.
- **RFC 8176 `amr` values:** `pwd`, `otp`, `hwk`, `swk`, `user`, `mfa`.
- **Breached-password check** (HIBP k-anonymity range API) on registration, reset and change. Tenant-toggleable, and fail open on network error.
- **Login throttling:** an IP-only sliding window in Redis, plus optional CAPTCHA/Turnstile after N failures per IP. Replace account-lockout-as-DoS with progressive delay plus a notification to the user.

### F7 (P1) Metadata & resource-server support

- **RFC 8414** `/.well-known/oauth-authorization-server`, served from the same builder as discovery. **RFC 9728** protected resource metadata for registered APIs.
- **RFC 9701** JWT introspection responses (`Accept: application/token-introspection+jwt`).
- **Step-up (RFC 9470), properly:**
  - Introspection returns `acr`/`auth_time`.
  - Document the RS challenge.
  - `/authorize` uses `login_required`/`interaction_required` when it can't meet `acr_values`.

### F8 (P1) FAPI 2.0 Security Profile switch

Add a per-client (and per-tenant default) `SecurityProfile` with values `default` | `fapi2`. `fapi2` enforces:

- PAR required (needs C3 and C4);
- PKCE S256;
- `private_key_jwt` or mTLS only;
- sender-constrained access tokens (DPoP or mTLS);
- `aud = issuer` for assertions;
- PS256/ES256 only;
- code lifetime ≤ 60 s;
- `iss` in responses;
- no `response_mode` other than query/jwt;
- DPoP server nonce at `/token`, and `dpop_jkt` at authorize.

Also implement `tls_client_auth` (PKI: subject DN / SAN match plus a CA bundle per tenant). Then run the OIDF FAPI2 plans. This is the natural route if the platform is meant to protect partner APIs, for example insurer integrations.

### F9 (P1) Key management hardening

- Pre-publication: a new key is published in JWKS **N hours before** it becomes the signing key (`ActivatesAt`).
- An `IKeyProvider` abstraction:
  - `DatabaseKeyProvider`: the current behaviour, with DataProtection.
  - `KmsKeyProvider`: Azure Key Vault / AWS KMS / Vault Transit, signing remotely so private keys never touch the app.
- Rotate encryption keys as well.
- Add an ETag and `Cache-Control: max-age` aligned to the pre-publication window on `/jwks`.
- Add EdDSA (Ed25519) as an opt-in.
- Write an ADR covering rotation and the custody model.

### F10 (P2) Grants and federation

- **JWT bearer grant (RFC 7523 §2.1)**, for service identity bootstrapping, e.g. Kubernetes workload identity tokens. The trust config is a per-client trusted issuer plus JWKS. This is small, given `ClientAssertionValidator`.
- **Token exchange:**
  - `actor_token` support and nested `act` claims;
  - `may_act` checks;
  - **deny-by-default** audience policy (fixes the Medium item above).
- **SAML 2.0 upstream** (SP role, via ITfoxtec or Sustainsys). The admin UI already offers "SAML" in a dropdown without a handler; either implement it or remove the option. **Recommendation:** remove the option now, and implement only when a customer needs it.
- **SCIM 2.0** server (`/scim/v2/Users`, `/Groups`), for enterprise tenants provisioning from Entra ID. Per-tenant bearer token, with each request audited.
- **CIBA notification:**
  - a real `ICibaAuthenticationChannel` (email/push/webhook), with the current log-only implementation kept as dev default;
  - client-registered delivery mode;
  - ping/push callbacks with `client_notification_token`.
- **Shared Signals (SSF) transmitter** with CAEP `session-revoked` / `credential-change` events. Hold this until F1 and F3 exist.

### F11 (P2) Engineering hygiene and CI

- **CI:**
  - CodeQL;
  - `dotnet list package --vulnerable --include-transitive` as a gate;
  - a Trivy image scan and **cosign** signing with SBOM attestation in `docker-publish.yml`;
  - Dependabot for Docker and GitHub Actions;
  - `TreatWarningsAsErrors` solution-wide.
- **Conformance in CI:**
  - a nightly job against an ephemeral stack, using the OIDF suite container, the Config/Basic/Form-Post/logout plans and **a second tenant**. That second tenant would have caught C5.
  - A multi-replica E2E job (2 pods behind a round-robin proxy) would have caught C6.
- Untrack `certs/aspnetapp.pfx`, and remove the literal secrets from `docker-compose.dev.yml` and `AppHost.cs:16` in favour of `.env`.
- Fill the gaps in `NetworkSecurity.IsInternal`: 0.0.0.0/8, 198.18/15, 64:ff9b::/96, 2002::/16.

---

## 4. Proposed roadmap

| Phase | Scope | Size |
|---|---|---|
| **0 — Security fixes** (no new features) | C1–C4, C7–C12, C14–C18. Each fix is a small PR with a regression test that reproduces the defect first. | ~2 sprints |
| **1 — Foundations** | C5, C6, C13, F1 (OP sessions), F2 (multi-replica/health/k8s), the Medium list | ~2–3 sprints |
| **2 — Compliance** | F3 (audit), F4 (GDPR), F5 (cs/sk), F6 (MFA) | ~3 sprints |
| **3 — Ecosystem** | F7, F9, then F8 (FAPI 2.0) and run the FAPI conformance plans | ~3 sprints |
| **4 — Optional** | F10 items, driven by demand | — |

**Order within Phase 0:** C1 → C8 → C10, which together close the "anyone with a client_id" chain. Then C2, C3/C4, C7, C12, and the rest.

---

## 5. Change-safety notes

- **C8:** changing the default `AllowClientCredentials=true` → `false` breaks existing clients that use the grant implicitly. The migration must set `AllowClientCredentials = (GrantTypesJson contains "client_credentials")` for existing rows. Run it in dry-run and report the result per tenant first.
- **C1:** before deploying, query for confidential clients with no secret, no JWKS and no mTLS thumbprint. They will stop authenticating, which is correct, but they need owners notified.
- **C13 and F9** touch key custody. Rehearse key migration on a staging copy, and keep the old keys in JWKS until all issued tokens have expired.
- **F1** forces existing users to sign in again. Ship it behind a flag, with a compatibility window that accepts old cookies and lazily creates a session row.
