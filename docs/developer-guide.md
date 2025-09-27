# Developer guide: Integrating with MrWhoOidc

Updated: 2025-09-27 (expanded draft)

This guide shows how to integrate your app and APIs with MrWhoOidc: sign-in flows, request parameters, JAR/JARM, token exchange (OBO), DPoP, and discovery.

## 1) Discovery and base endpoints

- OIDC discovery: `/.well-known/openid-configuration`
- JWKS: as advertised in discovery
- Token endpoint: `/token`
- Authorization endpoint: `/authorize`
- Introspection endpoint: `/introspect` (if enabled)

Cache `.well-known` and JWKS using ETag/Cache-Control.

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

## 4) Token Exchange (OBO)

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

## 5) DPoP and bridging modes

If the subject token is DPoP-bound (`cnf.jkt`), the server enforces a bridging policy per client.

- `Deny` (default): exchange fails when subject is DPoP-bound
- `RequireSameJkt`: caller must send a DPoP proof for `/token` using the same key (same JKT) and the issued token is bound to that key
- `AllowSameJktOnly`: like RequireSameJkt but only permitted when the subject is already DPoP-bound

Security note: `/token` DPoP proof must include `ath` = base64url(SHA-256(subject_token)) to prevent substitution.

End-to-end example: `docs/obo-dpop-requiresamejkt-e2e.md`

## 6) Error Handling & UX

- User flows: cancellation/timeouts/invalid_scope produce friendly error pages; correlate via request IDs in logs
- API calls: map OAuth error codes to client behavior (retry, prompt, or fail fast)
- Token Exchange errors
  - `invalid_target` when audience not allowed
  - `insufficient_scope` when scopes are not permitted
  - `dpop_same_key_required` / `dpop_bridging_not_supported` per policy

## 7) Minimal Client Snippets

PowerShell example for TE with DPoP (pseudo): see `docs/obo-dpop-requiresamejkt-e2e.md`.

C# sketch for TE request (no DPoP shown)

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

## 8) Testing & Environments

- Use the provided `.http` files under `docs/http` for quick endpoint testing
- In CI, spin up Redis to exercise replay cache and rate-limit paths
- Lock SDK/toolchain to a known-good version until .NET 9 GA

---

Related docs
- `docs/obo-client-policy.md`
- `docs/obo-dpop-requiresamejkt-e2e.md`
- `docs/jar-replay-cache.md`

## 9) Token Exchange Rate Limiting & Metrics

Per-client Token Exchange requests are rate limited (in-memory by default; Redis-backed when a multiplexer is registered). The limiter enforces a maximum number of TE requests per client per rolling minute (`TokenExchangeRateLimitOptions:PerClientPerMinute`, default 60). When Redis is present, a fixed one‑minute bucket key (`te:rl:{client}:{yyyyMMddHHmm}`) with atomic INCR + TTL is used for horizontal scalability.

Configuration (appsettings)

```
"TokenExchangeRateLimit": {
  "Enabled": true,
  "PerClientPerMinute": 60
}
```

Environment overrides (examples)
- `TokenExchangeRateLimit__Enabled=false`
- `TokenExchangeRateLimit__PerClientPerMinute=120`

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


## 10) TLS Termination / Reverse Proxy (Render, Nginx, etc.)

---

## 11) Quick Reference Cheat Sheet

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
- Don’t clear `KnownProxies`/`KnownNetworks` if the app is directly exposed to the internet without a reverse proxy; restrict to known proxy IPs instead.
