# Admin guide: Providers, Keys, Claim Mappings & OBO Policy (Draft)

Updated: 2025-09-27 (expanded draft)

This guide helps administrators configure providers, keys, client mappings, claim mapping, and OBO (token exchange) policy for common scenarios. Screenshots will be added; for now, follow the steps and examples.

## Prerequisites

- Access to the Admin UI (role: Admin)
- Basic knowledge of your external IdPs (issuer URLs, client IDs/secrets, JWKS)
- If using Redis for replay/rate-limit features, ensure `ConnectionStrings:redis` is configured for production

## 1) Providers (External IdPs)

Add one or more OpenID Connect identity providers (IdPs). Each provider record encapsulates configuration, keys (for outbound JAR/PAR), and claim mappings.

Navigation: **Admin → Providers → New**

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

## 4) Claim Mappings

Define how upstream claims (from providers) become local claims and what flows emit them.

Navigation: **Admin → Providers → Claim Mappings** (scoped to a provider) OR global fallback via config.
- Examples:
  - Map upstream `email` to local `email`
  - Combine `given_name` + `family_name` → local `name`
  - Normalize `groups` or `roles` for downstream APIs

Validate via a test login and inspect the issued ID/access token in your app or via test utilities.

## 5) OBO Policy (Token Exchange)

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

## 6) Provider Picker UX (Accessibility & Mobile)

Users see a list of available providers. The picker supports accessibility basics and mobile layout.

- Remembered provider hint: optionally pre-select or highlight the last provider used
- A11y: labels, roles, tab order, focus visible
- Mobile: responsive layout and touch targets

## 7) Inbound JAR & Replay Protection

If clients send JWT-secured authorization requests (JAR), enable replay protection.

- Production: configure Redis via `ConnectionStrings:redis`
- Auth options (`appsettings*.json`):
  - `Auth:RequestObjectClockSkewSeconds`
  - `Auth:RequestObjectReplayTtlSeconds`
  - `Auth:RequestObjectMaxLifetimeSeconds`
  - `Auth:RequestObjectAllowedAlgorithms`
- Discovery advertises `request_object_signing_alg_values_supported` from the allow-list

See: docs/jar-replay-cache.md

## 8) Rate Limiting & Headers (Token / Introspect)

When enabled with Redis, endpoints like /token and /introspect return appropriate rate-limit headers and 429 with Retry-After.

## 9) Troubleshooting

- External OIDC UX
  - Correlation IDs flow from start → callback in structured logs
  - Friendly error pages for cancel/timeout/invalid_scope (localization-ready)
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
