# Admin guide: Providers, Clients, Keys, Claim Mappings & OBO Policy

This guide helps tenant administrators configure external identity providers, clients and their security policy, keys, claim mappings, user enrollment, and OBO (token exchange) policy. Admin UI routes are kebab-case (for example `/admin/providers`).

## Prerequisites

- Access to the Admin UI (role: Admin)
- Basic knowledge of your external IdPs (issuer URLs, client IDs/secrets, JWKS)
- If using Redis for replay/rate-limit features, ensure `ConnectionStrings:redis` is configured for production

## 1) Providers (External IdPs)

Add one or more OpenID Connect identity providers (IdPs). Each provider record encapsulates configuration, keys (for outbound JAR/PAR), and claim mappings.

Navigation: **Admin → Providers → New**

OIDC providers are configured through a structured form (with templates for Google, Entra ID, GitHub, Facebook, Apple, and generic OIDC). An "Extended JSON" input exists for advanced, non-standard keys only; it cannot set standard fields such as `Authority` or `ClientId`.

### 1.1 Core Fields

| Field | Required | Example | Notes |
|-------|----------|---------|-------|
| Name | Yes | `contoso` | Machine-safe unique key (used in `idp=` authorize param & cookies). Lowercase recommended. |
| DisplayName | Yes | `Contoso ID` | Shown to end-users on provider picker. |
| Type | Yes | `OIDC` | |
| Authority | Yes | `https://login.contoso.com` | Base issuer for discovery if `DiscoveryUrl` not set. No trailing slash needed. |
| DiscoveryUrl | No | `https://login.contoso.com/v2/.well-known/openid-configuration` | Override when tenant-specific or non-standard path. Must return valid OIDC metadata. |
| ClientId | Yes | `webapp-contoso` | Registered with upstream IdP. |
| ClientSecret | Sometimes | (secret value) | Omit when using `private_key_jwt` or IdP-managed credential flows. Stored hashed if supported or plaintext if necessary (avoid weak secrets). |
| ResponseType | No | `code` | Default `code`. |
| Scopes | Yes | `["openid","profile","email"]` | Additional scopes (e.g. `offline_access`) if permitted. |
| UsePKCE | Recommended | `true` | Always enable for public/hybrid clients. PKCE challenge S256 enforced. |
| UseJAR | Optional | `false` | When true, outbound authorization request is wrapped & signed (requires provider key). |
| UsePAR | Optional | `false` | When true, a pushed authorization request is sent first (requires PAR endpoint in discovery). |
| RequestedAcrValues | Optional | `urn:mfa` | Space-delimited ACR list. Added to upstream auth query / JAR. |
| Prompt | Optional | `login` | Upstream prompt override. (Not recommended unless forcing re-auth.) |
| ResponseMode | Optional | `query` / `form_post` | Leave empty to let IdP default. JARM modes handled separately. |
| ExtraAuthParams | Optional | `{"domain_hint":"contoso"}` | Arbitrary K/V pairs appended to auth request (careful with collisions). |
| BackChannelLogout | Optional | `true` | Default `true`. Accept upstream back-channel logout for this provider. |
| TokenValidation.* | Optional | `{ "ValidateIssuer": true }` | `ValidateIssuer`, `ValidateAudience`, `ValidateLifetime` (all default `true`). |

#### 1.1.1 Extended Parameters (JSON)

For advanced provider-specific settings that are not part of the standard schema, use the **Extended JSON** field.

- The value must be a JSON **object**.
- It must **not** include standard keys like `Authority`, `ClientId`, `Scopes`, etc. (those come from the form fields).
- The standard form fields always win.
- Providing an empty object (`{}`) removes previously stored extended keys.

### 1.2 Validation

On save, the UI performs:

- Discovery fetch (Authority or explicit DiscoveryUrl) → must return 200 & JSON with `authorization_endpoint`, `token_endpoint`, `jwks_uri`.
- Authority vs metadata `issuer` consistency check (warning if mismatch).
- Basic JWKS parse to ensure key retrieval works.

### 1.3 Ordering & Defaults

In **Client ↔ Providers** mapping you control:

- Order: display order on picker.
- `IsDefaultForClient`: influences auto-selection when only one or hints present.
- `AutoRedirectIfSingle`: if a client has exactly one enabled provider, auto-redirect rather than showing the picker.

### 1.4 Cookies & Remembered Provider

Per client, the last successful provider is remembered in a cookie named `__Host-mrwhooidc-lastidp-<client bucket>` (90-day expiry). Picker highlights this provider unless an explicit `idp=` or `idp_hint=` parameter forces another choice.

### 1.5 Security Recommendations

- Restrict scopes to what downstream mapping needs; avoid blanket `profile` if unneeded.
- Use PKCE (`UsePKCE=true`) for every OIDC provider (defense in depth).
- Prefer JAR/PAR only if upstream mandates; otherwise keep complexity low initially.

### 1.6 Failure & Cancel UX

Upstream `error=access_denied` or `interaction_required` shows a friendly error page with a correlation handle; the user can return to the picker.

### 1.7 Example Minimal ConfigJson

```jsonc
{
  "Authority": "https://login.contoso.com",
  "ClientId": "webapp-contoso",
  "ClientSecret": "<secret>",
  "ResponseType": "code",
  "Scopes": ["openid","profile","email"],
  "UsePKCE": true,
  "UseJAR": false,
  "UsePAR": false,
  "RequestedAcrValues": "",
  "Prompt": null,
  "ResponseMode": null,
  "ClockSkewSeconds": 120,
  "BackChannelLogout": true,
  "ExtraAuthParams": {}
}
```

> Security note: Admin UI screens avoid displaying stored secrets; if a secret is present in config JSON, it is redacted in the details view.

## 2) Keys (PEM/JWK Import & Rotation)

Provider keys sign or encrypt outbound artifacts such as JAR/PAR requests sent to an upstream IdP. Client keys (`PublicJwksJson` or `PublicJwksUri`) are used to validate a client's request objects and `private_key_jwt` assertions, and to encrypt ID tokens, userinfo, and JARM responses for clients that register encryption.

Navigation: **Admin → Providers → Details → Manage keys** (provider keys), or **Admin → Clients → Edit → Keys** and the dedicated client keys page.

Workflow (provider keys):

1. Import a PEM (PKCS#8 preferred) or JWK. The UI derives the public components and thumbprint.
2. Choose *Purpose*: `Signing` or `Encryption`.
3. Confirm the `alg` (for example `RS256`, `PS256`, `ES256`) and save. The key is stored as active unless you stage it disabled.

Validation covers structural JWK parsing, alg/kty consistency (ES256 must be EC P-256, and so on), and duplicate `kid` rejection within a provider.

Rotation: keep two keys active during a rollover. Add the new key, wait for consumers to re-fetch the JWKS (at least the cache TTL), deactivate the old key, and delete it once nothing depends on it. Prefer PS256 or ES256 where supported, and never reuse a private key across providers.

The server's own signing keys are rotated automatically for every active tenant. Their private JWKs are encrypted at rest and cached only in process memory, never in Redis. See [for-operators/key-rotation.md](for-operators/key-rotation.md).

### 2.1 Public JWKS Endpoints (Clients & Providers)

The server can optionally expose sanitized public keys:

| Scope | Endpoint | Description |
|-------|----------|-------------|
| Client | `/clients/{clientId}/jwks` | Keys a client has published. |
| Provider (single) | `/providers/{providerName}/jwks` | Active provider signing keys. 404 if unknown or disabled. |
| Providers (aggregate) | `/providers/jwks` | All active provider keys, deduplicated by `kid`. |

Settings under `Auth` (defaults shown; `appsettings.Development.json` enables the first two):

```jsonc
"Auth": {
  "ExposeClientJwks": false,
  "ExposeProviderJwks": false,
  "ExposeAggregatedProviderJwks": false,
  "ClientJwksCacheSeconds": 300,
  "ProviderJwksCacheSeconds": 300,
  "ProviderJwksIncludeEncryption": false
}
```

- Responses carry an `ETag` derived from the sorted `kid` set and `Cache-Control: public, max-age=<cache seconds>`. Consumers should use conditional GETs (`If-None-Match`).
- Private members (`d,p,q,dp,dq,qi,oth,k`, and any `_`-prefixed property) are removed.
- Encryption-purpose provider keys are included only with `ProviderJwksIncludeEncryption=true`.
- The endpoints use the `rl-jwks` rate-limit policy.
- Never place private key material in `PublicJwksJson`.

For request-object signing algorithms and replay settings, see section 9 and [reference/jar-replay-cache.md](reference/jar-replay-cache.md).

## 3) Client Configuration and Security Policy

Navigation: **Admin → Clients → Edit**. The tabs are General, Redirect URIs, Scopes, Secrets, Keys, Providers, Introspection, OBO, Users, and Tools.

The UI covers redirect and post-logout URIs, PKCE, consent and auto-approval, local versus external login, QR login, signing and encryption algorithms, `RequirePar`, subject type and sector identifier, and the client authentication toggles (`AllowClientSecretBasic`, `AllowClientSecretPost`, `AllowPrivateKeyJwt`). Some settings are not in the UI: registered `grant_types`, `token_endpoint_auth_method`, the per-grant toggles (`AllowClientCredentials`, `AllowDeviceAuthorization`, `AllowCiba`), and `M2MAllowedAudiencesJson`. Set those through `mrwho-cli client` (`--grant-types`, `--token-auth-method`), configuration import, or dynamic client registration.

How these settings are enforced:

- **Public versus confidential.** A client may authenticate with `client_id` alone only when `token_endpoint_auth_method=none`, or when it has no auth method and no credential material at all (no secrets, JWKS, or mTLS thumbprints). Revoking every secret of a confidential client does not make it public: it can no longer authenticate.
- **Auth method.** The registered `token_endpoint_auth_method` is enforced at `/token`. Without it, the `Allow*` toggles apply. Failures return `invalid_client` (`401` for HTTP Basic).
- **Grant types.** When `grant_types` is registered, other grants are rejected at `/token` (`refresh_token` is implied by `authorization_code`).
- **PAR.** `RequirePar` on the client, `Auth:RequirePar`, or `Auth:RequireParClients` makes PAR mandatory. Pushed requests are single-use and bound to the pushing client.
- **Resource indicators.** `resource` values must be in `Auth:ApiAudiences` or the client's `M2MAllowedAudiencesJson`.
- **Introspection.** Deny-by-default. A client can introspect only its own tokens, tokens whose `aud` names it, or tokens for audiences granted on the **Introspection** tab (`IntrospectionAudiencesJson`) or in `Auth:IntrospectionPermissions`.
- **Device flow and CIBA.** `/device/authorize` and `/bc-authorize` authenticate the client and check `AllowDeviceAuthorization` and `AllowCiba`. CIBA requests must name an existing user, and only that user can approve them.
- **Encryption.** If a client registers ID-token or JARM encryption and its key cannot be resolved, requests fail instead of returning plaintext.

## 3.1) Client Secret Management

**Navigation**: **Admin → Clients → Edit → Secrets** tab

MrWhoOidc supports **multiple active client secrets** per confidential client to enable zero-downtime secret rotation. This follows the overlap strategy used for signing key rotation.

### Secret Lifecycle States

| State | Description | Valid for Auth? |
|-------|-------------|-----------------|
| **Inactive** | Generated but not yet activated | ❌ No |
| **Active** | Activated, not expired, not revoked | ✅ Yes |
| **Primary** | Active + recommended for new usage (advisory flag) | ✅ Yes |
| **Expired** | Passed expiry date | ❌ No |
| **Revoked** | Manually revoked by admin | ❌ No |

### Key Features

- **Up to 3 active secrets** per client (prevents clutter during rotation)
- **Expiry dates**: Optional, 1–730 days from creation; leave blank for no expiry
- **One-time display**: Secret value shown ONLY on creation (cannot be retrieved later)
- **Usage tracking**: Last used timestamp and usage count per secret
- **Audit trail**: Records who created/activated/revoked each secret

### Rotation Workflow (Zero Downtime)

1. **Generate new secret** (inactive state)
   - Click "Add Secret" button
   - Enter description (e.g., "Q4 2025 Production Secret")
   - Set expiry (optional, 1–730 days)
   - Leave "Activate immediately" unchecked
   - Copy secret value (shown once with copy button)

2. **Update client application** with new secret
   - Deploy to dev/staging first for testing
   - Update production config (Azure Key Vault, K8s Secrets, etc.)

3. **Activate new secret** (starts overlap period)
   - Click "Activate" button in Secrets table
   - Both old and new secrets now valid

4. **Set as primary** (optional)
   - Marks new secret as recommended (visual indicator only)

5. **Monitor usage**
   - Verify "Last Used" timestamp updates
   - Check metrics: `oidc.client_secrets.auth.success` / `oidc.client_secrets.auth.failure`

6. **Revoke old secret** (after 24-48 hour soak period)
   - Click "Revoke" button on old secret
   - Confirm revocation

### Security Features

- **Argon2id hashing**: All secrets hashed before storage (never plaintext)
- **Expiry enforcement**: Expired secrets rejected with specific error code
- **Self-lockout prevention**: Cannot revoke last active secret
- **Audit logging**: All lifecycle events logged with operator identity

### Monitoring & Alerts

Health endpoint: `/health/client-secrets`

**Status responses:**

- `Healthy`: All clients have valid secrets
- `Degraded`: Secrets expiring within 3 days
- `Unhealthy`: Client has no active secrets (locked out)

**Recommended alerts:**

- **Critical**: Authentication failures due to expired secrets
- **Warning**: Secrets expiring within 7 days
- **Info**: Clients with >3 active secrets (cleanup needed)

### Best Practices

- **Rotate regularly**: Every 90 days (or per your security policy)
- **Use overlap period**: Don't revoke old secret immediately after activating new one
- **Test first**: Deploy to non-production environments before production
- **Document secrets**: Use description field to note purpose/environment
- **Monitor expiry warnings**: `ClientSecretExpiryMonitor` checks every 24 hours and logs warnings for secrets expiring within 7 days

### Troubleshooting

**`invalid_client` error:**

- Verify application config matches new secret exactly (no spaces/newlines)
- Ensure application reloaded config (restart if needed)
- Check "Last Used" timestamp to confirm secret is being tried

**"Cannot revoke last active secret" error:**

- Generate and activate new secret first
- Then revoke old one

**Client stopped authenticating after its last secret expired or was revoked:**

- A confidential client never falls back to `client_id`-only authentication. Create and activate a new secret.

### Related Documentation

- [Client Secret Rotation](for-operators/client-secret-rotation.md) — verified lifecycle and operational procedure

---

## 4) User Registration and Tenant Enrollment

Tenant administrators have four supported paths for bringing users into a tenant:

| Path | Admin UI | Best for |
| --- | --- | --- |
| Platform registration | `/Registrations` plus **Admin -> Registrations** | General self-service sign-up and pending review |
| Tenant-specific registration | `/t/{tenantSlug}/Registrations` plus **Admin -> Settings -> User Registration** | Tenant-branded self-service sign-up with direct tenant assignment |
| Invitations | **Admin -> Invitations** | Known users, contractors, tenant admins, and non-domain users |
| Domain claims | **Admin -> Domain claims** | Organization domains where matching users should self-service join |

Key rules:

- `UserAccount` owns credentials globally; tenant membership is separate.
- Tenant registration mode is configured under **Admin -> Settings -> User Registration**: `platform-only`, `tenant-only`, or `both`.
- Tenant-specific registration can use tenant branding, a registration heading, intro copy, and a registration image URL.
- Tenant-specific registration targets the tenant directly but still creates a pending registration unless an invitation, domain claim, or client auto-approval policy applies.
- Invitations lock registration to the invited email and tenant.
- Invitations can be managed through **Admin -> Invitations**, `mrwho-cli invitation`, or the CLI MCP tools `invitation_list`, `invitation_create`, and `invitation_revoke`.
- Registration settings can be observed and changed with `mrwho-cli registration get` and `mrwho-cli registration set --mode <platform-only|tenant-only|both>`.
- Verified `AutoJoin` domain claims let matching email addresses discover the tenant and auto-approve registration into that tenant.
- A non-revoked domain can be claimed by only one tenant platform-wide.
- Common public mailbox domains cannot be claimed.
- Platform external login never auto-enrolls users into tenants.
- Platform admins can review and terminate global accounts with no active tenant membership from **Platform Admin -> Unassigned Users** or `mrwho-cli user unassigned`.

See [User Registration and Tenant Enrollment](user-registration-and-enrollment.md) for the full workflow, edge cases, and test coverage.

## 5) Claim Mappings

Define how upstream claims (from providers) become local claims and what flows emit them.

Navigation: **Admin → Providers → Claim Mappings** (scoped to a provider) OR global fallback via config.

- Examples:
  - Map upstream `email` to local `email`
  - Combine `given_name` + `family_name` → local `name`
  - Normalize `groups` or `roles` for downstream APIs

Validate via a test login and inspect the issued ID/access token in your app or via test utilities.

## 6) Licensing

Licensing management and analytics are no longer hosted in `MrWhoOidc.WebAuth`.

- There is no supported `Admin → License` UI in WebAuth.
- Legacy `/admin/license*` and `/admin/api/license*` routes are deprecated compatibility paths only.
- Use the standalone licensing service for license installation, validation, history, usage analytics, and limit reporting.
- WebAuth remains responsible for identity behavior, OAuth/OIDC endpoints, and admin/tenant management.

## 7) OBO Policy (Token Exchange)

Configure per-client OBO rules that constrain exchanges, audiences, scopes, lifetimes, and DPoP bridging.

- Navigate: Admin → Clients → Edit → OBO tab
- Fields (summary):
  - Enable OBO
  - Allowed callers (client_id allow-list)
  - Allowed source audiences (subject token aud)
  - Allowed target audiences/resources
  - Allowed scopes (intersection with subject and request)
  - Max delegation depth and max lifetime
  - DPoP bridging mode: Deny | RequireSameJkt | AllowSameJktOnly

Reference: [reference/obo-client-policy.md](reference/obo-client-policy.md) for full field descriptions and examples. Token exchange must be enabled with `Auth:EnableTokenExchange=true`.

## 8) Provider Picker UX (Accessibility & Mobile)

Users see a list of available providers. The picker supports accessibility basics and mobile layout.

- Remembered provider hint: optionally pre-select or highlight the last provider used
- A11y: labels, roles, tab order, focus visible
- Mobile: responsive layout and touch targets

## 9) Inbound JAR & Replay Protection

If clients send JWT-secured authorization requests (JAR), enable replay protection.

- Production: configure Redis via `ConnectionStrings:redis`
- Auth options (`appsettings*.json`):
  - `Auth:RequestObjectClockSkewSeconds`
  - `Auth:RequestObjectReplayTtlSeconds`
  - `Auth:RequestObjectMaxLifetimeSeconds`
  - `Auth:RequestObjectAllowedAlgorithms`
- Discovery advertises `request_object_signing_alg_values_supported` from the allow-list

See: [reference/jar-replay-cache.md](reference/jar-replay-cache.md)

## 10) Rate Limiting

Protocol and admin endpoints use named ASP.NET Core rate-limit policies (for example `rl-authorize`, `rl-jwks`, `rl-admin`); rejected requests receive `429`. Token exchange has its own per-client limiter (`TokenExchangeRateLimit`). See [rate-limiting-dashboard.md](rate-limiting-dashboard.md) and **Admin → Rate Limits**.

## 11) User Password Management

### Global Credentials Model

User passwords are stored globally on the `UserAccount` entity, not per-tenant. This means:

- **Single password**: Users have one password for all tenants they belong to
- **Global lockout**: Failed login attempts lock the account across all tenants
- **Unified MFA**: MFA enrollment applies to all tenants

### Admin Password Reset

Platform administrators can reset a user's password from **Admin → Users** (`/admin/users`) with the **Reset Password** action. The reset:

- applies to **all tenants** the user belongs to, and the user signs in with the new temporary password,
- unlocks the account if it is locked out,
- like every password update (self-service change and reset included), rotates the account's security stamp and revokes all live tokens of the account across its tenants, so existing sessions and refresh tokens stop working.

### Lockout

Users are locked out after 5 consecutive failed login attempts, for 15 minutes. Lockout is global, so a lockout on one tenant applies to all of them. It clears after a successful login, when the lockout expires, or after a password reset.

### Password Migration (Platform Admin)

For systems migrating from per-tenant passwords, platform admins can use:

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/platform-admin/api/migrate-credentials/status` | GET | View migration progress |
| `/platform-admin/api/migrate-credentials` | POST | Batch migrate users |
| `/platform-admin/api/migrate-credentials/{accountId}` | POST | Migrate single user |

## 12) Troubleshooting

- External OIDC UX & correlation
  - Supply an `X-Correlation-Id` header (<= 64 chars, `[A-Za-z0-9-_]`) when reproducing issues; the value is echoed in the `X-Correlation-Id` response header and surfaces in structured logs.
  - Browser hops use opaque `cid_ref` handles embedded in the state payload; stale handles trigger a friendly error and emit `oidc.correlation.cache.misses`.
  - Friendly error pages for cancel/timeout/invalid_scope (localization-ready) display a shortened correlation handle so support can cross-reference logs.
  - See [ADR-0008](./adr/ADR-0008-correlation-handles.md) for the design rationale and cache policy.
- Admin APIs
  - Missing `X-Correlation-Id` headers are logged as warnings via `AdminCorrelationMiddleware`; attach the correlation value from the problematic `/authorize` or admin UI action when filing tickets.
- Token Exchange
  - `invalid_target`, `insufficient_scope`, DPoP errors (`dpop_same_key_required`, `dpop_bridging_not_supported`)
- Keys
  - Ensure alg/kty/use alignment; check duplicate `kid`
- Logout
  - Back-channel and front-channel notifications are sent only for a verified subject (the OP session user or a signature-verified `id_token_hint`), and only to clients that received codes or hold live tokens for that user. A client that never obtained tokens for the user is not notified.
  - Inspect delivery under **Admin → Backchannel** or `/admin/api/bcl/outbox`; the dispatcher covers every tenant.
- External account linking
  - Linking an upstream identity to an existing account requires the user to sign in locally as that account in the same browser within 10 minutes. This applies to platform logins too.

## Appendix: Minimal checklists

- New provider
  - Issuer URL resolves; metadata reachable
  - Client ID/secret valid; redirect URI registered
  - Test login round-trip works; claims as expected
- New OBO policy
  - Caller listed in Allowed callers
  - Target audience/resource allowed
  - Scopes narrowed appropriately
  - DPoP bridging mode matches upstream token binding

---

Related docs

- [developer-guide.md](developer-guide.md)
- [reference/obo-client-policy.md](reference/obo-client-policy.md)
- [reference/obo-dpop-requiresamejkt-e2e.md](reference/obo-dpop-requiresamejkt-e2e.md)
- [reference/jar-replay-cache.md](reference/jar-replay-cache.md)
- [for-administrators/webauthn.md](for-administrators/webauthn.md)
