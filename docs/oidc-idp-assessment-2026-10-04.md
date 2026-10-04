# MrWhoOidc — OIDC IdP Assessment & Implementation Proposals

**Date:** 2026-10-04  
**Baseline:** `master` @ `a1df8f4e`  
**Method:** Static code review across five areas: authorize/token, discovery/keys/crypto, sessions/user auth, secondary endpoints, and ops/CI. No tests were run. Findings marked **✔ verified** were re-read in source by the reviewer. The other findings come with file:line evidence and should be confirmed with a failing test before they are fixed.  
**Supersedes for planning purposes:** `oidc-implementation-assessment.md` and `oidc-feature-gap-analysis.md`, which are historical. Several of their "✅ Complete" claims no longer hold.

---

## 1. Executive summary

MrWhoOidc covers a wide range of protocols: code+PKCE, PAR, JAR, JARM, DPoP, CIBA, device flow, token exchange, DCR, the three logout specs, multi-tenancy and passkeys. It passes the OIDF Config, Basic, Form Post and the three logout plans against `/t/default`.

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

### Medium (abridged; full detail in the agent audit notes)

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

## 5. Corrections to existing documentation

- `oidc-implementation-assessment.md`:
  - "Pairwise subject IDs — missing" is outdated. Pairwise is **implemented but buggy** (userinfo, device flow, discovery).
  - "Refresh token reuse detection missing" is outdated. Rotation, family revocation and atomic claim exist.
  - "DPoP in-memory only" is outdated. Redis is used when configured, with a silent in-memory fallback.
  - The CIBA "✅ all 3 delivery modes" claim is **false**: only poll works.
- "mTLS ✅ Complete" is overstated: thumbprint pinning only, no `tls_client_auth`, and binding only on client_credentials.
- "Localization": no cs/sk support exists despite the `ui_locales` plumbing.

## 6. Change-safety notes

- **C8:** changing the default `AllowClientCredentials=true` → `false` breaks existing clients that use the grant implicitly. The migration must set `AllowClientCredentials = (GrantTypesJson contains "client_credentials")` for existing rows. Run it in dry-run and report the result per tenant first.
- **C1:** before deploying, query for confidential clients with no secret, no JWKS and no mTLS thumbprint. They will stop authenticating, which is correct, but they need owners notified.
- **C13 and F9** touch key custody. Rehearse key migration on a staging copy, and keep the old keys in JWKS until all issued tokens have expired.
- **F1** forces existing users to sign in again. Ship it behind a flag, with a compatibility window that accepts old cookies and lazily creates a session row.
