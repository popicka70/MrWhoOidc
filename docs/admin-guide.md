# Admin guide: Providers, Keys, Claim Mappings & OBO Policy (Draft)

Updated: 2026-05-23 (registration, invitations, and domain claims)

> **⚠️ URL Convention Change (November 2025)**  
> All admin URLs now use kebab-case (e.g., `/admin/providers` instead of `/Admin/Providers`). Update bookmarks and scripts. See [URL Mappings Reference](../specs/002-url-kebab-case-conversion/url-mappings.md) for complete list.

This guide helps administrators configure providers, keys, client mappings, claim mapping, and OBO (token exchange) policy for common scenarios. Screenshots will be added; for now, follow the steps and examples.

## Prerequisites

- Access to the Admin UI (role: Admin)
- Basic knowledge of your external IdPs (issuer URLs, client IDs/secrets, JWKS)
- If using Redis for replay/rate-limit features, ensure `ConnectionStrings:redis` is configured for production

## 1) Providers (External IdPs)

Add one or more OpenID Connect identity providers (IdPs). Each provider record encapsulates configuration, keys (for outbound JAR/PAR), and claim mappings.

Navigation: **Admin → Providers → New**

> Note (2025-12): OIDC providers are now configured primarily via a structured form in the Admin UI. An "Extended JSON" input exists for advanced/non-standard keys only; it cannot set standard fields like `Authority` or `ClientId`.

### 1.1 Core Fields

| Field | Required | Example | Notes |
|-------|----------|---------|-------|
| Name | Yes | `contoso` | Machine-safe unique key (used in `idp=` authorize param & cookies). Lowercase recommended. |
| DisplayName | Yes | `Contoso ID` | Shown to end-users on provider picker. |
| Type | Yes | `OIDC` | (Future: `SAML`). |
| Authority | Yes | `https://login.contoso.com` | Base issuer for discovery if `DiscoveryUrl` not set. No trailing slash needed. |
| DiscoveryUrl | No | `https://login.contoso.com/v2/.well-known/openid-configuration` | Override when tenant-specific or non-standard path. Must return valid OIDC metadata. |
| ClientId | Yes | `webapp-contoso` | Registered with upstream IdP. |
| ClientSecret | Sometimes | (secret value) | Omit when using `private_key_jwt` or IdP-managed credential flows. Stored hashed if supported or plaintext if necessary (avoid weak secrets). |
| ResponseType | No | `code` | Typically `code`; can support `code id_token` (hybrid) later. |
| Scopes | Yes | `["openid","profile","email"]` | Additional scopes (e.g. `offline_access`) if permitted. |
| UsePKCE | Recommended | `true` | Always enable for public/hybrid clients. PKCE challenge S256 enforced. |
| UseJAR | Optional | `false` | When true, outbound authorization request is wrapped & signed (requires provider key). |
| UsePAR | Optional | `false` | When true, a pushed authorization request is sent first (requires PAR endpoint in discovery). |
| RequestedAcrValues | Optional | `urn:mfa` | Space-delimited ACR list. Added to upstream auth query / JAR. |
| Prompt | Optional | `login` | Upstream prompt override. (Not recommended unless forcing re-auth.) |
| ResponseMode | Optional | `query` / `form_post` | Leave empty to let IdP default. JARM modes handled separately. |
| ExtraAuthParams | Optional | `{"domain_hint":"contoso"}` | Arbitrary K/V pairs appended to auth request (careful with collisions). |
| BackChannelLogout | Optional | `true` | Enables future back-channel logout integration. |
| TokenValidation.* | Optional | `{ "ValidateIssuer": true }` | Per-provider validation overrides (future extensibility). |

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
- Basic JWKS parse to ensure key retrieval works (not cached permanently yet).

### 1.3 Ordering & Defaults

In **Client ↔ Providers** mapping you control:

- Order: display order on picker.
- `IsDefaultForClient`: influences auto-selection when only one or hints present.
- `AutoRedirectIfSingle`: if a client has exactly one enabled provider, auto-redirect rather than showing the picker.

### 1.4 Cookies & Remembered Provider

Per client, the last successful provider is stored as a hashed cookie (`.mrwhooidc.lastidp.<hash>`). Picker highlights this provider unless an explicit `idp=` or `idp_hint=` parameter forces another choice.

### 1.5 Security Recommendations

- Restrict scopes to what downstream mapping needs; avoid blanket `profile` if unneeded.
- Use PKCE (`UsePKCE=true`) for every OIDC provider (defense in depth).
- Prefer JAR/PAR only if upstream mandates; otherwise keep complexity low initially.

### 1.6 Failure & Cancel UX

Upstream `error=access_denied` or `interaction_required` triggers friendly error page with correlation id; user can return to picker. Structured correlation telemetry is a follow-up item (see backlog).

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

Keys are used to sign or encrypt outbound artifacts (JAR, optional JWE for JARM in future) and—later—back-channel logout tokens. The platform stores *provider* keys and *client* keys (for inbound JAR validation) separately.

Navigation: **Admin → Providers → Keys** (contextual) or **Admin → Client Keys** (for inbound JAR).

Workflow:

1. Click *Import Key*.
2. Paste PEM (PKCS#8 preferred) or JWK JSON. The UI derives public components & thumbprint.
3. Choose *Purpose*: `Signing` or `Encryption` (encryption currently reserved for JWE / future features).
4. Confirm `alg` suggestion (e.g., `RS256`, `PS256`, `ES256`). Only algorithms allowed by policy should be activated.
5. Save → key is persisted with `Active=true` (unless you explicitly stage it disabled).

Validation includes:

- Structural JWK parse.
- alg/kty consistency (ES256 must be EC P-256, etc.).
- Duplicate `kid` rejection (across keys of same provider scope).
- Optional: future not-before / expiry warnings.

Rotation Strategy (Recommended):

- Keep at least two signing keys active (`current` + `next`).
- Introduce new key → mark active → wait for caches / downstream clients to fetch JWKS → deactivate old key → optionally delete once no outstanding tokens reference it.

Deletion Safety:

- Only delete keys that no longer sign valid unexpired artifacts (outbound JAR). Since outbound JARs are ephemeral at auth time, rotation is lower risk than long-lived ID/Access tokens.

Future Enhancements (Backlog):

- Enhanced JWKS visual diff & history view.
- Expiry alerts via background service metrics.

Security Notes:

- Prefer PSS algorithms (PS256) or EC (ES256) where ecosystem support exists.
- Do not reuse the same private key between providers.

### 2.1 Public JWKS Endpoints (Clients & Providers)

The server can optionally expose sanitized public keys for:

| Scope | Endpoint | Description |
|-------|----------|-------------|
| Client | `/clients/{clientId}/jwks` | Keys a client has published (for its own consumers validating client-generated artifacts e.g. request objects). |
| Provider (single) | `/providers/{providerName}/jwks` | Active provider keys (signing only by default) for upstream/federated flows or logout tokens. 404 if provider unknown or disabled. |
| Providers (aggregate) | `/providers/jwks` | All active provider keys (signing only by default) deduplicated by `kid`. |

Feature flags (appsettings*) under `Auth`:

```jsonc
"Auth": {
  "ExposeClientJwks": true,
  "ExposeProviderJwks": true,
  "ExposeAggregatedProviderJwks": true,
  "ClientJwksCacheSeconds": 120,
  "ProviderJwksCacheSeconds": 120,
  "ProviderJwksIncludeEncryption": false
}
```

Caching & ETags:

- Responses carry an `ETag` header derived from sorted `kid` values (stable across key order changes, changes only when membership changes).
- IMemoryCache TTL = `ClientJwksCacheSeconds` / `ProviderJwksCacheSeconds` (minimum 5s enforced).
- Consumers should perform conditional GETs with `If-None-Match` for efficient polling.

Sanitization:

- Private key members are removed: `d,p,q,dp,dq,qi,oth,k` and any property starting with `_`.
- Ensures `use` is present (`sig` for signing keys, `enc` if encryption flag enabled and purpose is encryption).

Encryption Keys (optional):

- Disabled by default to reduce exposure surface. Set `ProviderJwksIncludeEncryption=true` to include encryption-purpose keys alongside signing keys.

Rotation Procedure (Providers):

1. Import new key (Active=true) → now two signing keys are served.
2. Wait for dependent systems to re-fetch JWKS (>= cache TTL; encourage conditional GETs).
3. Deactivate old key (Active=false) → endpoint stops including it; ETag changes.
4. After confirming no tokens refer to old key (for outbound artifacts), optionally delete it.

Rotation Procedure (Clients):

1. Client updates its own `PublicJwksJson` (admin UI or API) with new key(s) added.
2. Invalidate cache automatically (future) or rely on TTL; manual invalidation via admin operation if exposed (currently internal API). Tests show explicit invalidation logic exists.
3. Remove old key after consumers no longer use it for verification.

Operational Tips:

- Monitor logs for duplicate `kid` warnings (duplicates are skipped during aggregation).
- Use short TTLs (60–120s) during active rotation phases, longer (5–10m) for steady state.
- If you see unexpected stale keys, verify cache invalidation triggers on key lifecycle events (future enhancement) or temporarily reduce TTL.

Security Considerations:

- Avoid exposing encryption keys unless a downstream requirement exists.
- Do not publish private keys; sanitization enforces this but defense in depth (never store private in `PublicJwksJson`).
- Consider rate limiting (policy `rl-jwks`) when high-frequency polling is expected (configured in `Program.cs`).

Client Consumption Guidance: see Developer Guide JWKS section.

Tips

- Keep at least two signing keys to support seamless rotation
- For request-object signing algs, align with `Auth:RequestObjectAllowedAlgorithms` (see replay cache doc)

See also: docs/jar-replay-cache.md for discovery alignment and TTL/skew guidance.

## 3) Client ↔ Provider Mappings

Map relying-party clients to behaviors and capabilities.

Navigation: **Admin → Clients → Edit → Providers tab**

- Configure:
  - Allowed grant types (authorization_code, client_credentials, token-exchange)
  - Redirect URIs and post-logout URIs
  - Authentication methods (secret vs private_key_jwt)
  - Allowed audiences/resources and scopes
  - Token formats (JWT vs opaque) and lifetimes

## 3.1) Client Secret Management

**Navigation**: **Admin → Clients → Edit → Secrets** (via "Manage Secrets" link)

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
- **Expiry dates**: Default 90 days from activation (configurable)
- **One-time display**: Secret value shown ONLY on creation (cannot be retrieved later)
- **Usage tracking**: Last used timestamp and usage count per secret
- **Audit trail**: Records who created/activated/revoked each secret

### Rotation Workflow (Zero Downtime)

1. **Generate new secret** (inactive state)
   - Click "Add Secret" button
   - Enter description (e.g., "Q4 2025 Production Secret")
   - Set expiry (optional, default 90 days)
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
   - Check metrics: `oidc.client_secrets.authentication_success`

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
- **Monitor expiry warnings**: Background service emits warnings 7 days before expiry

### Troubleshooting

**"Invalid client credentials" error:**

- Verify application config matches new secret exactly (no spaces/newlines)
- Ensure application reloaded config (restart if needed)
- Check "Last Used" timestamp to confirm secret is being tried

**"Cannot revoke last active secret" error:**

- Generate and activate new secret first
- Then revoke old one

**Legacy clients (single secret):**

- Clients with only `ClientSecretHash` (deprecated field) continue working
- Admin UI automatically migrates to multi-secret model on first edit
- No action required unless rotating secret

### Related Documentation

- [Client Secret Rotation Guide](client-secret-rotation-guide.md) — User-facing rotation steps
- [Client Secret Rotation Playbook](client-secret-rotation-playbook.md) — Operational procedures for admins
- [Telemetry Taxonomy](telemetry-taxonomy.md) — Metrics and logging reference
- Licensing management has moved out of MrWhoOidc.WebAuth; use the standalone licensing service for license administration and analytics

---

## 4) User Registration and Tenant Enrollment

Tenant administrators have three supported paths for bringing users into a tenant:

| Path | Admin UI | Best for |
| --- | --- | --- |
| Manual or external IdP registration | `/Registrations` plus **Admin -> Registrations** | General self-service sign-up and pending review |
| Invitations | **Admin -> Invitations** | Known users, contractors, tenant admins, and non-domain users |
| Domain claims | **Admin -> Domain claims** | Organization domains where matching users should self-service join |

Key rules:

- `UserAccount` owns credentials globally; tenant membership is separate.
- Invitations lock registration to the invited email and tenant.
- Invitations can be managed through **Admin -> Invitations**, `mrwho-cli invitation`, or the CLI MCP tools `invitation_list`, `invitation_create`, and `invitation_revoke`.
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

Reference: docs/obo-client-policy.md for full field descriptions and examples.

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

See: docs/jar-replay-cache.md

## 10) Rate Limiting & Headers (Token / Introspect)

When enabled with Redis, endpoints like /token and /introspect return appropriate rate-limit headers and 429 with Retry-After.

## 11) User Password Management

### Global Credentials Model

User passwords are stored globally on the `UserAccount` entity, not per-tenant. This means:

- **Single password**: Users have one password for all tenants they belong to
- **Global lockout**: Failed login attempts lock the account across all tenants
- **Unified MFA**: MFA enrollment applies to all tenants

### Admin Password Reset

When resetting a user's password from the Admin UI:

1. Navigate to **Admin → Users → Edit**
2. Click "Reset Password"
3. **⚠️ Important**: This affects ALL tenants the user belongs to

The confirmation dialog warns:
> "This will reset the user's password across all tenants. The user will need to use this new password for all tenant logins."

### Lockout Management

Users are locked out after 5 failed login attempts for 15 minutes.

**To unlock a user manually:**
1. Navigate to **Admin → Users → Edit**
2. Click "Clear Lockout"
3. The user can immediately attempt login again

Lockout state includes:
- `FailedLoginAttempts`: Number of consecutive failures
- `LockedOutUntil`: When lockout expires (null if not locked)
- `LastFailedLoginAt`: Timestamp of last failure

### Password Migration (Platform Admin)

For systems migrating from per-tenant passwords, platform admins can use:

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/platform-admin/api/migrate-credentials/status` | GET | View migration progress |
| `/platform-admin/api/migrate-credentials` | POST | Batch migrate users |
| `/platform-admin/api/migrate-credentials/{accountId}` | POST | Migrate single user |

See: `docs/global-credentials-migration.md` for detailed migration procedures.

## 12) Troubleshooting

- External OIDC UX & correlation
  - Supply an `X-Correlation-Id` header (<= 64 chars, `[A-Za-z0-9-_]`) when reproducing issues; the value is echoed back on every response and surfaces in structured logs/telemetry.
  - Browser hops use opaque `cid_ref` handles embedded in the state payload; stale handles trigger a friendly error and emit `oidc.correlation.cache.misses`.
  - Friendly error pages for cancel/timeout/invalid_scope (localization-ready) display a shortened correlation handle so support can cross-reference logs.
  - See [ADR-0008](./adr/ADR-0008-correlation-handles.md) for the full design rationale, cache policy, and future enhancements.
- Admin APIs
  - Missing `X-Correlation-Id` headers are logged as warnings via `AdminCorrelationMiddleware`; attach the correlation value from the problematic `/authorize` or admin UI action when filing tickets.
- Token Exchange
  - `invalid_target`, `insufficient_scope`, DPoP errors (`dpop_same_key_required`, `dpop_bridging_not_supported`)
- Keys
  - Ensure alg/kty/use alignment; check duplicate `kid`

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

- docs/obo-client-policy.md
- docs/obo-dpop-requiresamejkt-e2e.md
- docs/jar-replay-cache.md
