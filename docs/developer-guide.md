# Developer guide: Integrating with MrWhoOidc

This guide covers what a relying party (RP) or API developer needs to integrate with MrWhoOidc: endpoints, client authentication, authorization parameters, PAR/JAR/JARM, the token endpoint, token exchange and DPoP, introspection, device flow, CIBA, logout, and operational headers.

Installation and local setup are covered elsewhere:

- [for-developers/quickstart-15-min.md](for-developers/quickstart-15-min.md) — published image and seeded source-build stack (`docker-compose.dev.yml`).
- [example-applications-guide.md](example-applications-guide.md) — which sample app demonstrates which scenario.
- [../e2e/README.md](../e2e/README.md) — Playwright browser and protocol tests.
- [../.github/copilot-instructions.md](../.github/copilot-instructions.md) — contributor conventions (architecture rules, migrations, primary keys, tests).

## 1) Discovery and endpoints

Protocol endpoints are served under the tenant path `/t/{slug}`. Always read endpoint URLs from the discovery document instead of hard-coding them. In the seeded dev stack the issuer is `https://localhost:8443/t/default`.

| Endpoint | Path (relative to issuer) | Notes |
|----------|---------------------------|-------|
| Discovery | `/.well-known/openid-configuration` | |
| JWKS | `/jwks` | Server signing keys. Cache with ETag. |
| Authorization | `/authorize` | GET/POST |
| PAR | `/par` | RFC 9126 |
| Token | `/token` | |
| UserInfo | `/userinfo` | GET/POST |
| Introspection | `/introspect` | Confidential clients only |
| Revocation | `/revoke` | |
| Device authorization | `/device/authorize` | When `Auth:EnableDeviceAuthorizationGrant=true` |
| CIBA | `/bc-authorize` | When `Auth:EnableCiba=true` |
| Dynamic registration | `/register`, `/register/{clientId}` | When `Auth:EnableDynamicClientRegistration=true` |
| End session | `/connect/endsession` | RP-Initiated Logout |
| Check session | `/connect/checksession` | Session Management iframe |

Discovery advertises `response_types_supported = ["code"]`, PKCE `S256` only, and response modes `query`, `fragment`, `form_post`, `query.jwt`, `fragment.jwt`, `form_post.jwt`. Token endpoint auth methods are `client_secret_basic`, `client_secret_post`, `private_key_jwt`, and `self_signed_tls_client_auth`.

### 1.1 Optional client and provider JWKS endpoints

Disabled by default (`Auth:ExposeClientJwks`, `Auth:ExposeProviderJwks`, `Auth:ExposeAggregatedProviderJwks`; enabled in `appsettings.Development.json`):

| Endpoint | Purpose |
|----------|---------|
| `/clients/{clientId}/jwks` | Public keys a client registered. `{"keys":[]}` when none. |
| `/providers/{providerName}/jwks` | Active signing keys for one external provider. 404 when unknown or disabled. |
| `/providers/jwks` | All active provider keys, deduplicated by `kid`. |

Responses strip private members (`d,p,q,dp,dq,qi,oth,k`, `_*`), carry an `ETag` derived from the sorted `kid` set, and are rate limited by the `rl-jwks` policy. Encryption keys are included only when `Auth:ProviderJwksIncludeEncryption=true`. Poll with `If-None-Match` and treat `304` as "unchanged".

## 2) Client authentication

The same rules apply at `/token`, `/par`, `/revoke`, `/device/authorize`, and `/bc-authorize`:

- **Public clients** (registered with `token_endpoint_auth_method=none`, or with no auth method and no credential material at all) authenticate with `client_id` only. A client that has or ever had secrets, a JWKS/JWKS URI, or mTLS thumbprints is confidential and must present that credential, even if its secrets have since expired or been revoked.
- At `/token` the registered `token_endpoint_auth_method` is enforced. When it is unset, the per-client toggles `AllowClientSecretBasic`, `AllowClientSecretPost`, and `AllowPrivateKeyJwt` apply.
- Sending more than one authentication method, or a Basic `client_id` that differs from the form `client_id`, is rejected.
- Authentication failures return `invalid_client`. When the client used HTTP Basic, the response is `401` with `WWW-Authenticate`.
- `/introspect` and `/bc-authorize` always require a client credential.
- `self_signed_tls_client_auth` uses the TLS client certificate. Behind a proxy, `X-Client-Cert` is honoured only when `Security:CertificateForwarding:Enabled=true` and the TCP peer is loopback or a configured trusted proxy (see section 11).

## 3) Authorization request

| Param | Notes |
|-------|-------|
| `response_type` | `code` only. |
| `client_id`, `redirect_uri` | `redirect_uri` must exactly match a registered URI. |
| `scope` | Include `openid` for OIDC. |
| `state`, `nonce` | Strongly recommended; `state` is required when `Auth:RequireState=true`. |
| `code_challenge`, `code_challenge_method` | PKCE with `S256`. Required unless the client has `RequirePkce=false`. |
| `response_mode` | `query`, `fragment`, `form_post`, or a JARM mode (section 4). |
| `prompt`, `max_age`, `login_hint`, `id_token_hint`, `acr_values`, `ui_locales`, `display`, `claims` | Standard OIDC parameters. `max_age` must be a non-negative integer. |
| `resource` | RFC 8707. Must be an absolute URI listed in `Auth:ApiAudiences` or in the client's `M2MAllowedAudiencesJson`; otherwise `invalid_target`. |
| `authorization_details` | RFC 9396 rich authorization requests. |
| `idp` / `idp_hint` | Extensions: force or suggest an external identity provider. |
| `request` / `request_uri` | JAR request object or PAR handle (section 4). |

Authorization responses include `iss` (RFC 9207, `authorization_response_iss_parameter_supported=true`).

## 4) PAR, JAR, and JARM

**PAR (RFC 9126).** POST the authorization parameters to `/par` with client authentication and receive a `request_uri`. Then call `/authorize?client_id=...&request_uri=...`.

- A `request_uri` is single-use. It is consumed inside the code-issuance transaction, and a replay fails with `invalid_request`.
- The front-channel `client_id` must match the client that pushed the request. Front-channel overrides such as `state` are ignored.
- PAR is mandatory when any of these apply: `Auth:RequirePar=true`, the client is listed in `Auth:RequireParClients`, or the client has `RequirePar=true`. Non-PAR requests from such clients are rejected.
- `Auth:ParClientPendingLimit` (default 50) caps outstanding pushed requests per client, returning `429 rate_limit_exceeded`.

**JAR (RFC 9101).** Send a signed request object as `request` (or inside PAR):

- `iss` and `sub` must equal `client_id`, and `aud` must be `{issuer}/authorize`. Include `exp`, and preferably `nbf` and `jti`.
- Allowed algorithms come from `Auth:RequestObjectAllowedAlgorithms` (default `RS256, PS256, ES256, ES384, ES512`), optionally narrowed per client by `Auth:RequestObjectAllowedAlgorithmsPerClient`. Discovery advertises the allow-list.
- Lifetime and replay limits: `Auth:RequestObjectMaxLifetimeSeconds` (300), `Auth:RequestObjectClockSkewSeconds` (120), `Auth:RequestObjectReplayTtlSeconds` (300), `Auth:RequestObjectMaxBytes` (4096). The replay cache uses Redis when it is configured.
- Details: [reference/jar-replay-cache.md](reference/jar-replay-cache.md) and [jar-jarm-guide.md](jar-jarm-guide.md).

**JARM.** Use `response_mode=query.jwt`, `fragment.jwt`, or `form_post.jwt`. Validate the response JWT's signature, `iss`, `aud` (your `client_id`), and `exp`.

**Encryption fails closed.** If a client registered `authorization_encrypted_response_alg` or `id_token_encrypted_response_alg` and the server cannot resolve a usable encryption key or does not support the alg/enc pair, the request fails (`server_error` at the token endpoint). The server never falls back to a plaintext response.

## 5) Token endpoint

Supported grants: `authorization_code`, `refresh_token`, `client_credentials`, `urn:ietf:params:oauth:grant-type:device_code`, `urn:openid:params:grant-type:ciba`, and `urn:ietf:params:oauth:grant-type:token-exchange`.

- **Registered grant types are enforced.** If the client has `grant_types` registered, any other grant is rejected with `unauthorized_client`. `refresh_token` is implied by `authorization_code`. Clients without registered grant types fall back to the per-grant toggles (`AllowClientCredentials`, `AllowDeviceAuthorization`, `AllowCiba`, ...).
- **`resource` at the token endpoint** follows the same allow-list as `/authorize`. For `authorization_code`, a token-request `resource` must match the one bound to the code. `audience` and `resource` are mutually exclusive unless equal.
- **Login context survives replicas.** `sid`, upstream `idp`/`acr`/`amr`, and mapped external claims are stored on the authorization-code row. ID tokens therefore carry them no matter which replica serves `/token`.
- **`claims`.** For `authorization_code`, a `claims` parameter is accepted and normalized before claim shaping.
- **Refresh tokens** rotate with lineage tracking (`ReplacedById`). Reuse revokes the whole family. The tenant setting `tokens.refreshTokenAbsoluteLifetimeSeconds` caps the absolute family lifetime. DPoP-bound refresh tokens require the same key on refresh.
- **Password changes** (self-service change, reset, or admin reset) rotate the account's security stamp and revoke all live tokens of that account across its tenants.

## 6) Token exchange (OBO) and DPoP bridging

Enable with `Auth:EnableTokenExchange=true` and configure a per-client OBO policy (admin UI or `mrwho-cli`).

Request (form-encoded, client-authenticated):

- `grant_type=urn:ietf:params:oauth:grant-type:token-exchange`
- `subject_token`, `subject_token_type=urn:ietf:params:oauth:token-type:access_token`
- `audience` or `resource` (target API), optional `scope`
- Optional `delegation_id` for client-bound user-to-user delegation (requires `Auth:EnableDelegatedAccess=true`). The authenticated client must be the grant's bound client.

The server validates the subject token, applies the OBO policy (callers, audiences, scopes, lifetime, delegation depth), and returns a token with an `act` claim.

DPoP-bound subject tokens (`cnf.jkt`) follow the client's bridging mode:

- `Deny` (default) rejects the exchange.
- `RequireSameJkt` requires a `/token` DPoP proof with the same key, and binds the issued token to it.
- `AllowSameJktOnly` works like `RequireSameJkt`, but only when the subject token is already bound.

Errors: `invalid_target`, `insufficient_scope`, and `invalid_request` with `dpop_same_key_required` or `dpop_bridging_not_supported`.

Rate limiting: `TokenExchangeRateLimit:Enabled` (default `true`) and `TokenExchangeRateLimit:PerClientPerMinute` (default 60). The limiter is Redis-backed when Redis is configured. Over the limit it returns `429 rate_limit_exceeded` with `Retry-After`. Metrics (meter `MrWhoOidc.WebAuth`): `oidc.token_exchange.requests|success|failures|duration.ms|ratelimit.allowed|ratelimit.blocked`.

References: [reference/obo-client-policy.md](reference/obo-client-policy.md), [reference/obo-dpop-requiresamejkt-e2e.md](reference/obo-dpop-requiresamejkt-e2e.md).

## 7) Introspection

`/introspect` is deny-by-default. An authenticated caller may introspect a token only when one of these holds:

- it is the token's client,
- it appears in the token's `aud`,
- it is granted one of the token's audiences through its per-client `IntrospectionAudiencesJson` or the global `Auth:IntrospectionPermissions`.

All audiences are considered. A corrupt per-client policy fails closed. Response fields default to `Auth:IntrospectionDefaultResponseFields`. Refresh-token introspection requires `Auth:AllowRefreshTokenIntrospection=true`.

## 8) Device authorization and CIBA

- **Device flow.** `/device/authorize` authenticates every client under the rules in section 2 and rejects clients without `AllowDeviceAuthorization` (`unauthorized_client`). For `private_key_jwt`, the assertion audience is the `/device/authorize` URL.
- **CIBA.** `/bc-authorize` requires client authentication and `AllowCiba`, and rejects a non-positive `requested_expiry`. `login_hint`, `login_hint_token`, or `id_token_hint` must resolve to an active user in the tenant (by id, pairwise subject, email, or username); otherwise the response is `unknown_user_id`. Only that user can approve the request.

## 9) Logout

- `/connect/endsession` accepts `id_token_hint`, `post_logout_redirect_uri`, `state`, and `client_id`. A `sid` query parameter is never used to choose whom to notify.
- Back-channel and front-channel notifications are sent only for a verified subject: the OP session user, or the subject of an `id_token_hint` whose signature and issuer verify. Expired hints are accepted. A hint naming a different user than the session is ignored.
- Notifications go only to RPs that received codes or hold live tokens for that user. Each RP gets its own (pairwise-aware) `sub`, and the hint's `sid` goes only to the RP it was issued to.
- Back-channel delivery uses a durable outbox and a background dispatcher that processes every tenant.

## 10) Correlation IDs

- Send `X-Correlation-Id` (at most 64 characters, `[A-Za-z0-9-_]`). The server ignores invalid values. `/authorize` generates an ID when none is supplied.
- When a correlation ID is present, the response echoes it in `X-Correlation-Id` and logs carry it as `correlation_id`.
- Browser hops carry an opaque `cid_ref` handle instead of the raw ID. Handles are cached for about 10 minutes.
- Metrics: `oidc.correlation.cache.writes|hits|misses|stale`.
- Design: [adr/ADR-0008-correlation-handles.md](adr/ADR-0008-correlation-handles.md). Code: `MrWhoOidc.WebAuth/Observability/CorrelationTrackingMiddleware.cs`.

## 11) Reverse proxy, issuer, and mTLS forwarding

- Forwarded headers (`X-Forwarded-For/Proto/Host`) are on by default (`ForwardedHeaders:Enabled`). Outside loopback they are honoured only from `ForwardedHeaders:KnownProxies` / `ForwardedHeaders:KnownNetworks`. `ForwardedHeaders:UnsafeTrustAll=true` is a last resort for platforms with unknowable proxy IPs.
- `ForwardedHeaders:AllowedHosts` defaults to the hosts of `Oidc:PublicBaseUrl` and `Oidc:Issuer`.
- Set `Oidc:PublicBaseUrl` (env `Oidc__PublicBaseUrl`) to pin the public scheme and host; otherwise the issuer is built from the request. Afterwards, check that every discovery URL starts with `https://`. `/health/forwarded-headers` and `/health/issuer` help diagnose problems.
- With `Security:CertificateForwarding:Enabled=true`, `X-Client-Cert` is stripped unless the TCP peer is loopback, a known proxy or network, or `UnsafeTrustAll` is set. Base64 DER and nginx URL-encoded PEM (`$ssl_client_escaped_cert`) are accepted.

## 12) Server internals relevant to integrators

- **Signing keys.** Private JWKs are encrypted before they are first stored and are cached only in process memory, never in the Redis tier. Key rotation runs for every active tenant.
- **Global credentials.** Passwords, MFA, and lockout (5 failures, 15 minutes) live on the global `UserAccount` and apply across tenants (`MrWhoOidc.Auth/Services/GlobalAuthenticationService.cs`).
- **Passkeys.** User verification is enforced when the tenant requires it. `acr=urn:mrwho:acr:passkey` and `amr` `user` are issued only when UV happened, and `amr` always carries `webauthn` and `hwk`.
- **External account linking.** Linking an upstream identity to an existing local account requires the user to be signed in locally as that account, in the same browser that completed the external sign-in, within 10 minutes. Platform logins follow the same rule.
- **Client key material.** WebAuth does not generate client key pairs. Use the separate `MrWhoOidc.KeyGen` service ([../MrWhoOidc.KeyGen/README.md](../MrWhoOidc.KeyGen/README.md)) and register only the public JWKS on the client.

## Related docs

- [oidc-idp-feature-reference.md](oidc-idp-feature-reference.md)
- [admin-guide.md](admin-guide.md)
- [reference/pairwise-subject-identifiers.md](reference/pairwise-subject-identifiers.md)
- [reference/idp-chaining-client-configuration.md](reference/idp-chaining-client-configuration.md)
- [http/](http/) — `.http` request samples
