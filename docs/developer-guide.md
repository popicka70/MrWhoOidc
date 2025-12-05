# Developer guide: Integrating with MrWhoOidc

Updated: 2025-11-01 (URL convention migration to kebab-case)

> **⚠️ URL Convention Change (November 2025)**  
> All URLs now use kebab-case (e.g., `/auth/external/callback` instead of `/Auth/External/Callback`). External IdPs must update registered callback URLs. See [URL Mappings Reference](../specs/002-url-kebab-case-conversion/url-mappings.md) for complete migration guide.

This guide shows how to integrate your app and APIs with MrWhoOidc: sign-in flows, request parameters, JAR/JARM, token exchange (OBO), DPoP, and discovery.

## 1) Discovery and base endpoints

- OIDC discovery: `/.well-known/openid-configuration`
- JWKS: as advertised in discovery
- Token endpoint: `/token`
- Authorization endpoint: `/authorize`
- Introspection endpoint: `/introspect` (if enabled)

Cache `.well-known` and JWKS using ETag/Cache-Control.

### 1.1 Optional JWKS Endpoints (Feature-Flagged)

If enabled by the admin, additional JWKS surfaces exist beyond the discovery `jwks_uri` (the latter represents the authorization server's own signing keys—handled elsewhere):

| Endpoint | Purpose |
|----------|---------|
| `/clients/{clientId}/jwks` | Public keys a client has published (e.g. for validating its request objects or future self-issued tokens). Returns `{"keys":[]}` when none. |
| `/providers/{providerName}/jwks` | Active keys (signing by default) for a specific external provider. 404 if unknown/disabled. |
| `/providers/jwks` | Aggregate of all active provider keys (deduplicated by `kid`). |

Characteristics
- Sanitized: private key fields removed (`d,p,q,dp,dq,qi,oth,k,_*`).
- ETag: hash of sorted `kid` values; stable ordering not required by caller.
- Cache headers (future): currently rely on ETag + client heuristic caching. Use `If-None-Match` to minimize bandwidth.
- Encryption keys included only when admin sets `Auth:ProviderJwksIncludeEncryption=true`.

Polling Strategy
1. Initial GET → cache ETag.
2. Subsequent GET with `If-None-Match` every TTL period (exposed TTL not returned today; align with admin guidance, e.g. 120s steady-state).
3. If 304 Not Modified → retain cached keys; if 200 with new body → update local verification store.

Rotation Detection
- New key addition changes ETag only when a new `kid` appears.
- Removing a key also changes the ETag.
- Re-ordering keys without membership change does not alter ETag.

Failure Modes
- 404 on `/providers/{name}/jwks` means provider disabled or unknown; treat as transient if expecting eventual creation.
- Empty `{"keys":[]}` is valid; don't treat as error.

Security
- Never assume encryption keys are present; check `use` or intended algorithm.
- Validate `kty`, `alg`, and `use` align with your crypto expectations before trusting.

Example (PowerShell):
```
$r = Invoke-WebRequest https://as.example.com/providers/jwks
$etag = $r.Headers.ETag
# Later conditional fetch
$r2 = Invoke-WebRequest -Headers @{ 'If-None-Match' = $etag } https://as.example.com/providers/jwks
if ($r2.StatusCode -eq 304) { 'No change' } else { 'Updated:' + $r2.Content }
```

## 2) Authorization Parameters & Provider / UX Hints

Below is a consolidated matrix of supported request parameters for `/authorize` (native + OIDC standard). Parameters marked (JAR-only) must appear inside the request object when JAR is used if you rely on them.

| Param | Required | Source | Description / Behavior |
|-------|----------|--------|------------------------|
| response_type | Yes | Standard | Typically `code`. Supports `code` (MVP). Hybrid & implicit not enabled yet. |
| client_id | Yes | Standard | Must match registered client. |
| redirect_uri | Yes* | Standard | Required unless pre-registered and single value (future optimization). Must exactly match allowed list. |
| scope | Yes | Standard | Space-delimited; must at least include `openid` for OIDC flows. Additional (e.g. profile, email). |
| state | Recommended | Standard | CSRF + app correlation. Always validated/round-tripped. |
| nonce | Recommended | Standard | Required for code+ID token/hybrid/JARM responses containing ID token; still stored for upstream correlation. |
| prompt | Optional | Standard | `login`, `consent`, `none`, etc. Passed through upstream (and into JAR if outbound JAR). |
| login_hint | Optional | Standard | Hint to upstream (email/username). Sanitized; not persisted. |
| idp | Optional | Extension | Forces a specific provider (fails if provider not allowed for client). Skips picker when valid. |
| idp_hint | Optional | Extension | Suggests (but does not force) a provider; picker may highlight it. Ignored if `idp` present. |
| acr_values | Optional | Standard | Passed through upstream (space-separated). Also influences upstream claim mapping if returned. |
| max_age | Optional | Standard | Auth freshness requirement. Enforced upstream only (local enforcement TODO). |
| ui_locales | Optional | Standard | BCP47 language tags, passed upstream as-is. |
| resource | Optional | RFC8707 | Target resource indicator (single). Mutually exclusive with `audience` (server-enforced). |
| audience | Optional | Extension | Alternative to `resource`; normalized internally. |
| code_challenge | PKCE | RFC7636 | Required when PKCE enforced (always for public/hybrid). S256 only. |
| code_challenge_method | PKCE | RFC7636 | Must be `S256`. |
| request | Optional | RFC9101 | JAR: signed JWT containing some/all params. Merged per RFC precedence rules. |
| request_uri | Optional | RFC9101 | JAR via PAR or pre-registered URI. When present, server dereferences & merges. |
| response_mode | Optional | Standard/JARM | Supports `query`, `form_post`, and JARM forms `query.jwt`, `form_post.jwt`. |
| claims | Future | OIDC | Not yet implemented; reserved for selective claim requests. |

Resolution / Precedence (RFC 9101): When `request` or dereferenced `request_uri` contains a claim also present in the outer query, JWT value takes precedence (subject to server validation). Conflicts cause `invalid_request`.

Example (plain):
```
GET /authorize?response_type=code&client_id=web-client&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcb&scope=openid%20profile%20email&idp=contoso&acr_values=urn%3Amfa&state=xyz&nonce=abc
```

Example (JAR embedded):
```
GET /authorize?client_id=web-client&request=eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...<snip>
```

Server merges request object claims, validates signature (client key), verifies `aud`, `iss`, `exp`, `nbf`, and replays (`jti`/`nonce`).

## 3) JAR (Request Objects) & JARM (Response Mode JWTs)

- JAR (RFC 9101)
  - Signed JWT containing authorization parameters (`iss`=client_id, `aud`=authorization endpoint or issuer + `/authorize`).
  - Must include `exp`; recommended to include `nbf` & `jti`.
  - Supported algs: surfaced via discovery; actual allowed set configured in `Auth:RequestObjectAllowedAlgorithms`.
  - Replay cache keyed by (`iss`,`aud`,`jti`) with TTL & skew from `Auth` options; optionally Redis-backed.
  - Invalid signature / unknown alg → `invalid_request`.
  - Conflicts between outer query vs JWT → `invalid_request`.

- PAR (Pushed Authorization Request) synergy
  - When `UsePAR` enabled for a provider, outbound upstream request may first POST parameters (or signed request JWT) to its PAR endpoint, receiving a `request_uri`.
  - For inbound (client → AS) PAR, future extension will allow POST to AS PAR endpoint and supply `request_uri` at `/authorize`.

- JARM (JWT Secured Authorization Response)
  - Supported response modes: `query.jwt`, `form_post.jwt` (success & error).
  - Response object claims (subset): `iss`=issuer, `aud`=client_id, `exp`, `iat`, `nonce` (if provided), plus either `code` or error fields.
  - Signing alg: server default key (RS256/PS256/ES256 depending on rotation set). Encryption (JWE) optional future.
  - Clients must validate signature, `aud`, `iss`, `exp`, and correlate `state` externally (state stays in URL/form, not inside JWT). Some ecosystems also embed `state`—we purposely keep it outside for simplicity.

Security Considerations
- Always verify `exp` on response JWT; keep tolerance small (<= 60s).
- Protect against mix-up by verifying `iss` matches discovery issuer.
- For JAR + JARM combined, `nonce` and `state` must align across request and response to mitigate substitution.

Additional detail: `docs/jar-replay-cache.md`

## 4) Correlation IDs and `cid_ref` Handles

### Overview

MrWhoOidc implements comprehensive correlation tracking (per ADR-0008) to enable end-to-end debugging across distributed OIDC flows involving external IdPs, token exchanges, and multi-hop delegation.

**Key Principles**:
- Every HTTP request to `/authorize`, `/token`, `/admin/api/*`, and external OIDC handlers participates in the correlation pipeline.
- Clients MAY supply `X-Correlation-Id` request header; server ALWAYS responds with `X-Correlation-Id` (either echo or generated).
- Correlation IDs are ephemeral (10-minute TTL) and never stored long-term; they exist solely for operational debugging.
- Front-channel flows (browser redirects) use opaque `cid_ref` handles to avoid exposing raw correlation IDs in URLs.

### Header Format & Validation

**Request Header**: `X-Correlation-Id: <value>`

**Validation Rules**:
- Maximum length: 64 characters
- Allowed characters: `[A-Za-z0-9-_]` (alphanumeric, hyphen, underscore)
- Invalid headers are silently rejected; server generates new 26-char Base32 ID

**Examples** (valid):
```
X-Correlation-Id: test-correlation-123
X-Correlation-Id: T0SDEC90BD1G1SAXA1JS6619RM
X-Correlation-Id: user_flow_checkout_20251014
```

**Examples** (invalid, will be rejected):
```
X-Correlation-Id: contains spaces invalid
X-Correlation-Id: <script>alert('xss')</script>
X-Correlation-Id: [65+ chars will exceed maximum and be rejected...]
```

### Response Header

Server ALWAYS includes `X-Correlation-Id` in HTTP responses:
```
HTTP/1.1 302 Found
Location: https://upstream.idp.com/authorize?...
X-Correlation-Id: T0SDEC90BD1G1SAXA1JS6619RM
```

Clients should:
1. **Capture** the response header value for logging/support tickets
2. **Propagate** to downstream API calls (e.g., `/token`, `/userinfo`, custom APIs)
3. **Include** in error reports submitted to administrators

### Browser Flow (`cid_ref` Handles)

For security and privacy, correlation IDs are NOT exposed in front-channel URLs (browser address bar). Instead:

1. **Start** (`/authorize` or `/Auth/External/Start`):
   - Server stores CID → opaque handle mapping in cache (10-min TTL)
   - Handle embedded in encrypted `state` parameter
   - Example: `state=CfDJ8...` (data-protected JWT containing `CorrelationId` field)

2. **Callback** (`/Auth/External/Callback`):
   - Server decrypts `state`, extracts handle, retrieves CID from cache
   - If cache miss (expired/evicted), generates new CID and logs `oidc.correlation.cache.misses` metric
   - Attaches CID to HTTP context for remaining request processing

3. **Return URL**:
   - Server appends `cid_ref=<handle>` query parameter to final redirect
   - Example: `https://app.example.com/cb?code=...&state=...&cid_ref=abc123`
   - Client can resolve via `/correlation/resolve?handle=abc123` (future admin endpoint)

**Cache Characteristics**:
- **TTL**: 10 minutes (sufficient for typical OAuth flows; upstream IdP timeouts usually < 5 minutes)
- **Storage**: In-memory (L1) + Redis (L2, if configured)
- **Eviction**: LRU with size limit; stale handles degrade gracefully (new CID generated)

### Retention Policy & Privacy

**Retention**:
- Correlation IDs exist ONLY in memory/Redis with 10-minute TTL
- Logs containing `correlation_id` follow standard log retention (typically 30-90 days)
- No long-term database persistence; cannot reconstruct flows after log retention expires

**Privacy Considerations**:
- Correlation IDs are **pseudonymous** (cannot identify users without corresponding logs)
- Never include PII (email, username, SSN) in custom correlation IDs
- Log scrubbing applies to correlation context (e.g., PII in adjacent fields hashed)
- GDPR/privacy requests: correlation IDs automatically expire; no manual deletion needed

**Compliance**:
- Treat correlation IDs as operational metadata, not personal data
- If auditor requires justification: cite operational debugging necessity per ADR-0008
- Log export: correlation IDs included in structured logs but scrubbed during export (per `LogScrubber`)

### Best Practices

**For Client Applications**:
```csharp
// Generate deterministic correlation ID from request context
var correlationId = $"user-flow-{requestId.ToString("N")[..16]}";

// Attach to all outbound requests
var request = new HttpRequestMessage(HttpMethod.Get, "/authorize?...");
request.Headers.Add("X-Correlation-Id", correlationId);

var response = await httpClient.SendAsync(request);

// Capture response header for logging
var serverCid = response.Headers.GetValues("X-Correlation-Id").FirstOrDefault();
_logger.LogInformation("Authorization initiated: correlation_id={CorrelationId}", serverCid);
```

**For Troubleshooting**:
```bash
# Step 1: Capture correlation ID from initial authorize request
curl -v -H "X-Correlation-Id: debug-issue-12345" \
  "https://as.example.com/authorize?client_id=web&response_type=code&..." \
  2>&1 | grep -i x-correlation-id

# Step 2: Follow redirects preserving correlation ID
# (Correlation propagates automatically via state/cid_ref handles)

# Step 3: Query logs with captured correlation ID
kubectl logs -l app=mrwhooidc --since=1h | grep "correlation_id=T0SDEC90BD1G1SAXA1JS6619RM"
```

**For Admin APIs**:
```powershell
# Propagate correlation ID through administrative operations
$cid = "admin-op-provider-update-001"
Invoke-RestMethod -Method POST -Uri "https://as.example.com/admin/api/providers/contoso" `
  -Headers @{ "X-Correlation-Id" = $cid; "Authorization" = "Bearer $adminToken" } `
  -Body $providerConfig
```

### Structured Logging Integration

All logs include correlation ID in structured scope:

```json
{
  "timestamp": "2025-10-14T12:34:56.789Z",
  "level": "Information",
  "message": "External OIDC start: initiating discovery and redirect",
  "correlation_id": "T0SDEC90BD1G1SAXA1JS6619RM",
  "provider": "contoso",
  "client_id": "web-app",
  "scope": "openid profile email"
}
```

**Query Patterns** (Application Insights / Splunk / Elasticsearch):
```kusto
// Azure Monitor / Application Insights
traces
| where customDimensions.correlation_id == "T0SDEC90BD1G1SAXA1JS6619RM"
| order by timestamp asc
| project timestamp, message, client_id, provider

// Splunk
index=mrwhooidc correlation_id="T0SDEC90BD1G1SAXA1JS6619RM"
| table _time, message, client_id, provider

// Elasticsearch
GET /logs-*/_search
{
  "query": { "match": { "correlation_id": "T0SDEC90BD1G1SAXA1JS6619RM" } },
  "sort": [{ "@timestamp": "asc" }]
}
```

### Metrics & Observability

**Key Metrics**:
- `oidc.correlation.cache.hits`: Successful handle resolution on callback
- `oidc.correlation.cache.misses`: Cache miss (expired/evicted); fallback to new CID
- `oidc.correlation.cache.writes`: New handle stored during authorize/start
- `oidc.correlation.cache.stale`: Handle exists but TTL expired (subset of misses)

**Alerting Thresholds** (recommended):
- `cache.misses` > 5% of `cache.hits` → investigate cache eviction pressure or TTL tuning
- `cache.stale` > 1% → upstream IdP response times exceeding 10-min TTL (adjust TTL)

### Further Reading

- **Design Rationale**: [ADR-0008: Correlation Handles](./adr/ADR-0008-correlation-handles.md)
- **Implementation**: `MrWhoOidc.WebAuth/Observability/CorrelationTrackingMiddleware.cs`
- **Cache Implementation**: `MrWhoOidc.WebAuth/Observability/CorrelationStateCache.cs`
- **Test Coverage**: `MrWhoOidc.UnitTests/CorrelationPipelineTests.cs` (unit), `ExternalOidcIntegrationTests.cs` (integration)

---

### Example: End-to-End Correlation Flow

```
1. Client → AS:
   GET /authorize?client_id=web&response_type=code&idp=contoso
   X-Correlation-Id: debug-login-20251014

2. AS → Client:
   HTTP/1.1 302 Found
   Location: https://login.contoso.com/authorize?...&state=CfDJ8...
   X-Correlation-Id: debug-login-20251014
   
   (state contains encrypted handle mapping to correlation ID)

3. User authenticates at Contoso...

4. Contoso → AS:
   GET /Auth/External/Callback?code=abc&state=CfDJ8...
   
   (AS decrypts state, resolves handle → correlation ID from cache)

5. AS → Client:
   HTTP/1.1 302 Found
   Location: https://app.example.com/cb?code=xyz&cid_ref=handle123
   X-Correlation-Id: debug-login-20251014

6. Client → AS:
   POST /token
   X-Correlation-Id: debug-login-20251014
   code=xyz&client_id=web&...

7. AS → Client:
   HTTP/1.1 200 OK
   X-Correlation-Id: debug-login-20251014
   {"access_token":"...","id_token":"..."}
```

All logs from steps 1-7 tagged with `correlation_id=debug-login-20251014` for unified trace reconstruction.

## 5) Licensing APIs & Observability

### 5.1 Current License Endpoint

- `GET /admin/api/license/current`
- Returns the active license (tier, organization, validity period, features, limits)
- Use in operational tooling to verify tier when troubleshooting access issues.
- Response mirrors `LicenseInfo` model.

### 5.2 Usage Analytics

- `GET /admin/api/license/usage?from=<ISO-8601>&to=<ISO-8601>`
- Aggregates feature usage metrics recorded via `FeatureUsageRepository`.
- Response fields:
  - `aggregationPeriod` (currently `daily`)
  - `metrics[]` with `featureName`, `usageCount`, `firstUsed`, `lastUsed`
- Use to drive custom dashboards or alerts when premium features are exercised.

### 5.3 Usage Limits

- `GET /admin/api/license/limits`
- Combines current license limit values with live usage counts (tenants, users, clients, custom entries).
- Response includes:
  - `limitType`
  - `currentUsage`
  - `limitValue`
  - `utilization` (0–1 ratio)
  - `isNearLimit` (>=80%)
  - `isAtLimit`
- Suggested usage: pre-flight checks in provisioning pipelines to prevent hitting hard limits.

### 5.4 Tier Reference

- `GET /admin/api/license/tiers`
- Returns descriptive catalog of license tiers (features, default limits, summary text).
- Ideal for UI tooltips or developer documentation.

### 5.5 Recording Feature Usage

- Call `IFeatureUsageRepository.RecordUsageAsync` when implementing new premium features.
- Parameters:
  - `featureName`: string key matching `FeatureFlags` constants
  - `tenantId`: optional for tenant-scoped metrics
  - `licenseId`: optional correlation when multi-tenant licensing introduced
  - `occurredAt`: timestamp
  - `increment`: default `1`
- Repository is resilient to duplicate inserts for the same day/feature; aggregates counts.

### 5.6 Metrics & Logging

- Service layer emits:
  - `licensing.license.install.*`
  - `licensing.license.revoke.*`
  - `licensing.license.validate.*`
- Consume via OTLP/Prometheus exporter configured in `MrWhoOidc.ServiceDefaults`.
- Logs include structured fields: `tenant`, `license_tier`, `organization` (when present).

### 5.7 Error Handling

- Install/Validate error codes (HTTP 400): `invalid_signature`, `expired_license`, `tier_mismatch`, `invalid_format`.
- Revocation returns HTTP 404 when no active license exists.
- All endpoints require Admin scope; include `X-Correlation-Id` for traceability.

## 6) Token Exchange (OBO)

Use OAuth 2.0 Token Exchange to obtain a token for a downstream audience on behalf of a user.

Request (form-encoded)
- `grant_type = urn:ietf:params:oauth:grant-type:token-exchange`
- `subject_token` = the caller's user token (JWT or opaque supported by server)
- `subject_token_type = urn:ietf:params:oauth:token-type:access_token`
- `audience` or `resource` = target API audience
- `scope` (optional) = subset of subject scopes

Server behavior (summary)
- Validates client auth (secret or private_key_jwt)
- Validates subject token (sig/iss/exp/nbf; rejects multi-hop JWT subjects with `act`)
- Applies per-client OBO policy (allowed callers, audiences, scopes, lifetime, delegation depth)
- Returns access token for target audience with `act` claim indicating the caller

Reference policy fields and examples: `docs/obo-client-policy.md`

## 7) DPoP and bridging modes

If the subject token is DPoP-bound (`cnf.jkt`), the server enforces a bridging policy per client.

- `Deny` (default): exchange fails when subject is DPoP-bound
- `RequireSameJkt`: caller must send a DPoP proof for `/token` using the same key (same JKT) and the issued token is bound to that key
- `AllowSameJktOnly`: like RequireSameJkt but only permitted when the subject is already DPoP-bound

Security note: `/token` DPoP proof must include `ath` = base64url(SHA-256(subject_token)) to prevent substitution.

End-to-end example: `docs/obo-dpop-requiresamejkt-e2e.md`

## 8) Error Handling & UX

- User flows: cancellation/timeouts/invalid_scope produce friendly error pages; correlate via request IDs in logs
- API calls: map OAuth error codes to client behavior (retry, prompt, or fail fast)
- Token Exchange errors
  - `invalid_target` when audience not allowed
  - `insufficient_scope` when scopes are not permitted
  - `dpop_same_key_required` / `dpop_bridging_not_supported` per policy

## 9) Minimal Client Snippets

PowerShell example for TE with DPoP (pseudo): see `docs/obo-dpop-requiresamejkt-e2e.md`.

C# sketch for TE request (no DPoP shown)

```csharp
using var http = new HttpClient { BaseAddress = new Uri("https://as.example.com") };
var form = new FormUrlEncodedContent(new Dictionary<string,string>{
  ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
  ["subject_token"] = subjectJwt,
  ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
  ["audience"] = "api-b",
  ["scope"] = "read"
});
var req = new HttpRequestMessage(HttpMethod.Post, "/token") { Content = form };
req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCreds);
var res = await http.SendAsync(req);
res.EnsureSuccessStatusCode();
var json = await res.Content.ReadAsStringAsync();
```

## 10) Testing & Environments

- Use the provided `.http` files under `docs/http` for quick endpoint testing
- In CI, spin up Redis to exercise replay cache and rate-limit paths
- Lock SDK/toolchain to a known-good version until .NET 9 GA

---

Related docs

- `docs/obo-client-policy.md`
- `docs/obo-dpop-requiresamejkt-e2e.md`
- `docs/jar-replay-cache.md`
- `docs/adr/ADR-0008-correlation-handles.md`

## 11) Token Exchange Rate Limiting & Metrics

Per-client Token Exchange requests are rate limited (in-memory by default; Redis-backed when a multiplexer is registered). The limiter enforces a maximum number of TE requests per client per rolling minute (`TokenExchangeRateLimitOptions:PerClientPerMinute`, default 60). When Redis is present, a fixed one‑minute bucket key (`te:rl:{client}:{yyyyMMddHHmm}`) with atomic INCR + TTL is used for horizontal scalability.

Configuration (appsettings)

```json
"TokenExchangeRateLimit": {
  "Enabled": true,
  "PerClientPerMinute": 60
}
```

Environment overrides (examples)

- `TokenExchangeRateLimit__Enabled=false`
- `TokenExchangeRateLimit__PerClientPerMinute=120`

Behavior

Behavior

- Under limit: request proceeds normally.
- Over limit: HTTP 429 with `error = rate_limit_exceeded` and a `Retry-After` header (seconds until bucket resets).
- Disabled (`Enabled=false`) or non-positive `PerClientPerMinute` => limiter short-circuits and always allows.

### Metrics emitted

All metrics are `System.Diagnostics.Metrics` instruments under meter name `MrWhoOidc.WebAuth` (prefix `oidc.`). Existing Token Exchange metrics:

- `oidc.token_exchange.requests` (counter) – every attempt, tags: outcome, client_bucket, target_aud, dpop_mode, source_token_type
- `oidc.token_exchange.success` (counter) – successful exchanges (same tags as above)
- `oidc.token_exchange.failures` (counter) – failed exchanges (same tags as above)
- `oidc.token_exchange.duration.ms` (histogram) – elapsed milliseconds (same tags as above)

New rate limiter focused counters:

- `oidc.token_exchange.ratelimit.allowed` (counter) – incremented for every TE request that passes the limiter; tags:
  - `client_bucket`
- `oidc.token_exchange.ratelimit.blocked` (counter) – incremented when a request is blocked with 429; tags:
  - `client_bucket`
  - `retry_after_seconds` (present only when computed)

Interpretation / example queries (Prometheus style if exported via OTLP → Prometheus):

- Block percentage per client (5m window):
  `sum(rate(oidc_token_exchange_ratelimit_blocked[5m])) / ( sum(rate(oidc_token_exchange_ratelimit_allowed[5m])) + sum(rate(oidc_token_exchange_ratelimit_blocked[5m])) )`
- Top N throttled clients (1h):
  `topk(10, sum(rate(oidc_token_exchange_ratelimit_blocked[1h])) by (client_bucket))`
- Latency of successful exchanges: histogram/summary derived from `oidc.token_exchange.duration.ms` filtering `outcome="success"`.

Correlating limiting with failures

- A blocked request also records a token exchange failure (`reason=rate_limited`) in the standard exchange counters. Use the dual signals to distinguish genuine policy validation failures from throttling.

Operational guidance

- Sudden spikes in `ratelimit.blocked` with flat `requests` usually indicate an abusive or misconfigured client (retry loop). Consider lowering the per-client limit temporarily or contacting the client owner.
- If all clients start hitting the limit simultaneously, examine whether the configured value is too low for peak traffic or if a deployment introduced additional exchange calls in a single logical flow.

Extensibility
- Future enhancements may introduce per-client overrides (dictionary) or token-exchange specific sliding window / leaky bucket algorithms. Current interface (`ITokenExchangeRateLimiter`) allows swapping implementation without touching handlers.

Troubleshooting
- If you never see `ratelimit.blocked` even when intentionally hammering the endpoint, verify that Redis is reachable (if expected) and that `PerClientPerMinute` is not set to zero or a very high value via environment variables.

## 12) TLS Termination / Reverse Proxy (Render, Nginx, etc.)

When running behind a reverse proxy that terminates TLS (for example, Render), the app must honor forwarded headers so it can publish https URLs in discovery and redirects.

What we do in code

- The WebAuth host enables forwarded headers early in the pipeline and honors X-Forwarded-Proto, X-Forwarded-Host, and X-Forwarded-For.
- KnownProxies/KnownNetworks are cleared so managed platforms with dynamic proxy IPs are accepted. Only use this setup when the app is actually behind a trusted proxy.
- With this in place, `HttpContext.Request.Scheme` and `Host` reflect the client-facing values, so `/.well-known/openid-configuration` advertises https endpoints.

Optional explicit issuer

- You can force the issuer via configuration to avoid any ambiguity behind multiple layers of proxies:
  - Set `Oidc:Issuer = https://your-domain.example.com` (environment variable key: `Oidc__Issuer`).
  - If set, discovery uses this value instead of computing from the incoming request.

Render specifics

- Render automatically adds `X-Forwarded-Proto` and `X-Forwarded-Host`. No custom headers are required.
- Keep the app listening on HTTP inside the container; TLS is handled by Render's edge.

Verify after deploy

- Open `https://<host>/.well-known/openid-configuration` and check:
  - `issuer` is `https://<host>`
  - `jwks_uri`, `authorization_endpoint`, `token_endpoint`, etc. all start with `https://`
- If they appear as `http://`:
  - Ensure the proxy is sending `X-Forwarded-Proto: https` and `X-Forwarded-Host`.
  - Confirm forwarded headers middleware runs before routing and redirection.
  - Optionally set `Oidc__Issuer` as a quick override.

Security note

- Don't clear `KnownProxies`/`KnownNetworks` if the app is directly exposed to the internet without a reverse proxy; restrict to known proxy IPs instead.

## 13) Quick Reference Cheat Sheet

| Topic | Key Takeaway |
|-------|--------------|
| Force provider | Add `idp=providerKey` to /authorize |
| Suggest provider | Use `idp_hint=providerKey` |
| Require MFA upstream | Include `acr_values=urn:mfa` |
| Enable PKCE | Handled automatically (S256) for public flows |
| Use JAR | Provide `request` (signed JWT) or `request_uri` |
| JARM success token | Validate response JWT (sig, exp, aud, iss) |
| Token Exchange | `grant_type=urn:ietf:params:oauth:grant-type:token-exchange` |
| DPoP bridging same key | Set client OBO policy `RequireSameJkt` |
| Replay protection tuning | Configure `Auth:RequestObjectReplayTtlSeconds` + `ClockSkew` |
| Rate limiting TE | `TokenExchangeRateLimit` section in config |

End of expanded draft.

When running behind a reverse proxy that terminates TLS (for example, Render), the app must honor forwarded headers so it can publish https URLs in discovery and redirects.

What we do in code
- The WebAuth host enables forwarded headers early in the pipeline and honors X-Forwarded-Proto, X-Forwarded-Host, and X-Forwarded-For.
- KnownProxies/KnownNetworks are cleared so managed platforms with dynamic proxy IPs are accepted. Only use this setup when the app is actually behind a trusted proxy.
- With this in place, `HttpContext.Request.Scheme` and `Host` reflect the client-facing values, so `/.well-known/openid-configuration` advertises https endpoints.

Optional explicit issuer
- You can force the issuer via configuration to avoid any ambiguity behind multiple layers of proxies:
  - Set `Oidc:Issuer = https://your-domain.example.com` (environment variable key: `Oidc__Issuer`).
  - If set, discovery uses this value instead of computing from the incoming request.

Render specifics
- Render automatically adds `X-Forwarded-Proto` and `X-Forwarded-Host`. No custom headers are required.
- Keep the app listening on HTTP inside the container; TLS is handled by Render’s edge.

Verify after deploy
- Open `https://<host>/.well-known/openid-configuration` and check:
  - `issuer` is `https://<host>`
  - `jwks_uri`, `authorization_endpoint`, `token_endpoint`, etc. all start with `https://`
- If they appear as `http://`:
  - Ensure the proxy is sending `X-Forwarded-Proto: https` and `X-Forwarded-Host`.
  - Confirm forwarded headers middleware runs before routing and redirection.
  - Optionally set `Oidc__Issuer` as a quick override.

Security note
- Don't clear `KnownProxies`/`KnownNetworks` if the app is directly exposed to the internet without a reverse proxy; restrict to known proxy IPs instead.

## 14) Database & Primary Key Strategy

### Primary Key Generation (UUIDv7)

MrWhoOidc uses **UUIDv7** (RFC 9562) for all entity primary keys instead of standard random GUIDs (UUIDv4).

**Why UUIDv7?**

- **Better performance**: Time-ordered UUIDs reduce B-tree page splits by 80-90% compared to random UUIDs
- **Improved cache locality**: Sequential writes keep hot index pages in buffer pool
- **Implicit chronological ordering**: Records can be approximately sorted by ID (millisecond precision)
- **Fully compatible**: Works with existing PostgreSQL `uuid` columns; no schema changes required
- **Standard**: RFC 9562 ratified spec with native PostgreSQL 17+ support

### Implementation

All entity classes use `GuidHelper.NewId()` for ID generation:

```csharp
// File: MrWhoOidc.Auth/Persistence/GuidHelper.cs
public class User
{
    public Guid Id { get; set; } = GuidHelper.NewId();  //  Correct
    // NOT: = Guid.NewGuid();  //  Don't use this
}
```

### Helper API

```csharp
// Generate new UUIDv7 for entity ID
var id = GuidHelper.NewId();

// Check if a Guid is UUIDv7
bool isV7 = GuidHelper.IsUuidV7(someGuid);

// Extract embedded timestamp (millisecond precision)
DateTimeOffset? timestamp = GuidHelper.ExtractTimestamp(uuidV7);
```

### For New Entities

When adding new entities, always use `GuidHelper.NewId()`:

```csharp
public class MyNewEntity
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    public Guid TenantId { get; set; }
    // ... other properties
}
```

### Migration Notes

- Existing UUIDv4 records remain valid and functional
- No data migration required; only new records use UUIDv7
- Foreign keys work transparently with mixed UUID versions
- External APIs unchanged; UUIDs serialize as standard RFC 4122 strings

### Performance Impact

- Insert operations: 50%+ latency reduction on high-volume tables (`Tokens`, `AuthorizationCodes`)
- Index size: ~15% smaller growth over time
- Query performance: Neutral to slightly better (especially time-range queries)

### References

- RFC 9562: <https://www.rfc-editor.org/rfc/rfc9562.html>
- Implementation: `MrWhoOidc.Auth/Persistence/GuidHelper.cs`
- Backlog: `docs/uuidv7-migration-backlog.md`

## 15) Global User Credentials Architecture

### Overview

MrWhoOidc uses a **global credentials model** where each user has a single password and MFA configuration that applies across all tenants they belong to.

**Key Entities:**

| Entity | Scope | Credentials Stored |
|--------|-------|-------------------|
| `UserAccount` | Global | PasswordHash, MfaEnabled, MfaSecret, lockout state |
| `User` | Per-tenant | (Legacy: PasswordHash - deprecated) |
| `UserTenantMembership` | Link | Role, status, tenant access |

### Authentication Flow

1. User enters email and password on tenant login page
2. `IGlobalAuthenticationService.AuthenticateAsync` is called
3. Service looks up `UserAccount` by normalized email
4. Verifies password against `UserAccount.PasswordHash`
5. Checks `UserTenantMembership` for active membership in the target tenant
6. If MFA enabled on `UserAccount`, redirects to MFA challenge
7. On success, creates session for the tenant

```text
Login Request → GlobalAuthenticationService
                      │
                      ▼
              UserAccount lookup (by email)
                      │
                      ▼
              Password verification (Argon2id)
                      │
                      ▼
              Lockout check (global)
                      │
                      ▼
              Tenant membership check
                      │
                      ▼
              MFA challenge (if enabled)
                      │
                      ▼
              Session created
```

### Lockout Protection

Lockout is applied **globally** across all tenants to prevent distributed brute-force attacks:

- **Threshold**: 5 failed attempts
- **Duration**: 15 minutes
- **Scope**: `UserAccount` (not per-tenant)

This means if an attacker tries to brute-force a password on tenant A, the lockout also protects tenant B.

### Password Operations

| Operation | Endpoint/Page | Scope |
|-----------|---------------|-------|
| Change password | `/profile/change-password` | Updates `UserAccount.PasswordHash` |
| Reset password | `/account/reset-password` | Resets `UserAccount.PasswordHash` |
| Admin reset | `/admin/users/edit` | Resets `UserAccount.PasswordHash` |

All password operations:
- Clear lockout state (`FailedLoginAttempts = 0`)
- Update `PasswordUpdatedAt` timestamp
- Apply immediately to all tenant sessions

### MFA Configuration

MFA settings are stored on `UserAccount` and apply globally:

```csharp
public class UserAccount
{
    public bool MfaEnabled { get; set; }
    public string? MfaSecret { get; set; }        // Encrypted TOTP secret
    public string[]? MfaRecoveryCodes { get; set; } // Encrypted
}
```

When a user enables MFA on one tenant, they will be challenged for MFA on all tenants.

### Migration from Per-Tenant Passwords

If migrating from the legacy per-tenant password model:

1. **On-demand migration**: `UserAccountProvisioner` automatically migrates passwords during login
2. **Batch migration**: Use `/platform-admin/api/migrate-credentials` endpoints
3. **Direct SQL**: See `docs/global-credentials-migration.md`

### Related Files

- `MrWhoOidc.Auth/Services/GlobalAuthenticationService.cs` - Core authentication logic
- `MrWhoOidc.Auth/Services/UserAccountService.cs` - Account operations
- `MrWhoOidc.Auth/Services/PasswordMigrationService.cs` - Migration tooling
- `MrWhoOidc.WebAuth/Pages/Login.cshtml.cs` - Login handler

## 16) Key & License Management Service (NEW)

### Overview

**SECURITY CHANGE**: As of feature branch `001-key-license-generator`, cryptographic key pair generation for OIDC clients has been **removed from the authorization server** (`MrWhoOidc.WebAuth`) and moved to a dedicated standalone service (`MrWhoOidc.KeyGen`).

**Why this change?**
- The authorization server should **never generate or possess private keys** for clients
- Violates principle of least privilege and separation of duties
- Creates unnecessary attack surface

### Key & License Generator Service

A separate web application for generating:
1. **RSA/ECDSA Key Pairs** for JAR/JARM (JWT-secured requests/responses)
2. **License Tokens** with custom claims (tier, organization, features, limits)

**Key Features:**
- Secure generation: Private keys never stored server-side (one-time download)
- Multiple algorithms: RSA (2048/3072/4096-bit), ECDSA (P-256/P-384/P-521)
- JWK/JWKS formats: Standards-compliant key formats
- License signing: ECDSA P-256 signed JWTs
- Audit trail: Download history, IP tracking, revocation support
- Web UI: Razor Pages interface for administration
- Docker ready: Containerized deployment with persistent volumes

**Documentation:**
- Service README: `MrWhoOidc.KeyGen/README.md`
- Docker deployment: `MrWhoOidc.KeyGen/DOCKER.md`
- Full deployment guide: `docs/key-license-generator-deployment.md`
- Feature spec: `specs/001-key-license-generator/spec.md`

### Deprecated: WebAuth Key Generation

**REMOVED in feature branch `001-key-license-generator`:**
- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs`:
  - `OnPostGenerateJwksAsync()` method
  - `OnPostAddKeyAsync()` method
- Any UI elements for generating keys in the client editor

**Migration Path:**
1. Deploy `MrWhoOidc.KeyGen` service (Docker recommended)
2. Generate keys via KeyGen web UI
3. Download public key (JWKS format)
4. Paste JWKS into client configuration in WebAuth admin UI
5. Provide private key (JWK format) securely to client application

**Security Benefits:**
- Authorization server never sees client private keys
- Proper separation of cryptographic material
- Dedicated audit trail for key generation
- Reduced attack surface on authorization server

### Using Generated Keys with JAR/JARM

#### JAR (JWT-secured Authorization Requests)

1. Generate ECDSA P-256 or RSA 2048+ key pair in KeyGen service
2. Download private key (JWK) to client application
3. Register public key (JWKS) in client configuration via WebAuth admin
4. Client signs authorization request JWT with private key
5. Authorization server validates using registered public key

#### JARM (JWT-secured Authorization Response Mode)

1. Generate encryption key pair in KeyGen service
2. Register public key in client configuration
3. Client provides private key for decrypting responses
4. Authorization server encrypts response with client's public key

#### Example: Registering Public Key

After generating a key pair in KeyGen:

1. **Download public key** (JWKS format):
   ```json
   {
     "keys": [
       {
         "kty": "RSA",
         "use": "sig",
         "alg": "RS256",
         "kid": "rsa-2048-20251028-abc123",
         "n": "...",
         "e": "AQAB"
       }
     ]
   }
   ```

2. **Navigate to** WebAuth Admin → Clients → Edit Client
3. **Paste JWKS** into "Client JWKS" field
4. **Save** client configuration

5. **Provide private key** to client application:
   ```json
   {
     "kty": "RSA",
     "use": "sig",
     "alg": "RS256",
     "kid": "rsa-2048-20251028-abc123",
     "n": "...",
     "e": "AQAB",
     "d": "...",
     "p": "...",
     "q": "...",
     "dp": "...",
     "dq": "...",
     "qi": "..."
   }
   ```

### License Token Validation

License tokens generated by KeyGen service use ECDSA P-256 (ES256) signing.

**Token Claims:**
- `iss`: Issuer (MrWhoOidc-KeyGen)
- `jti`: Unique token ID (UUIDv7)
- `nbf`: Not before timestamp
- `iat`: Issued at timestamp
- `exp`: Expiration timestamp
- `tier`: License tier (Free/Developer/Pro/Enterprise)
- `organization`: Organization name
- `features`: Array of enabled features
- `limits`: Object with usage limits

**Validation:**
1. Verify signature using KeyGen's licensing public key
2. Check `exp` claim (token not expired)
3. Check `nbf` claim (token active)
4. Validate `tier` and `features` match expected values

**Example Validation (C#):**
```csharp
var tokenHandler = new JwtSecurityTokenHandler();
var validationParameters = new TokenValidationParameters
{
    ValidIssuer = "MrWhoOidc-KeyGen",
    IssuerSigningKey = new ECDsaSecurityKey(ecdsaPublicKey),
    ValidateAudience = false,
    ValidateLifetime = true,
    ClockSkew = TimeSpan.Zero
};

var principal = tokenHandler.ValidateToken(licenseToken, validationParameters, out _);
var tier = principal.FindFirst("tier")?.Value;
var features = JsonSerializer.Deserialize<string[]>(principal.FindFirst("features")?.Value ?? "[]");
```

### Development Workflow Changes

**Old Workflow (DEPRECATED):**
1. Open WebAuth Admin → Client Editor
2. Click "Generate JWKS" button
3. Private key generated on authorization server ❌

**New Workflow (SECURE):**
1. Open KeyGen service web UI
2. Generate key pair (algorithm, size, curve)
3. Download private key (one-time, secure to client) ✅
4. Download public key
5. Register public key in WebAuth Admin

**Benefits:**
- Private keys never touch authorization server
- Audit trail in dedicated service
- Key lifecycle management (revocation, download history)
- Compliance with security best practices

### Deployment Considerations

- Deploy KeyGen service on separate infrastructure
- Restrict access to authorized administrators only
- Use HTTPS with strong TLS configuration
- Mount licensing private key securely (Docker secrets/volume)
- Backup database volume regularly
- Monitor health endpoint: `/health`
- Review audit logs for key downloads

See full deployment guide at `docs/key-license-generator-deployment.md`.

