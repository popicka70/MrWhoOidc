# Developer guide: Integrating with MrWhoOidc

Updated: 2025-09-25 (first draft)

This guide shows how to integrate your app and APIs with MrWhoOidc: sign-in flows, request parameters, JAR/JARM, token exchange (OBO), DPoP, and discovery.

## 1) Discovery and base endpoints

- OIDC discovery: `/.well-known/openid-configuration`
- JWKS: as advertised in discovery
- Token endpoint: `/token`
- Authorization endpoint: `/authorize`
- Introspection endpoint: `/introspect` (if enabled)

Cache `.well-known` and JWKS using ETag/Cache-Control.

## 2) Authorization parameters and provider hints

To direct users to a specific external IdP or request particular authentication methods:

- `idp` (string): provider key; selects a specific provider on the picker or bypasses it
- `acr_values` (space-separated): e.g. `urn:mfa` or policy-specific values
- `prompt`: `login`, `consent`, etc.
- `login_hint`: an email/username hint (avoid PII where not needed)

Example authorization request

GET /authorize?response_type=code&client_id=web-client&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcb&scope=openid%20profile%20email&idp=contoso&acr_values=urn%3Amfa&state=xyz&nonce=abc

## 3) JAR and JARM

- JAR (RFC 9101): send request parameters as a signed JWT in `request` or `request_uri`
- Replay protection: server caches `iss+aud+jti` (and nonce when applicable). Configure TTL and skew via `Auth` options
- Allowed request-object algorithms come from `Auth:RequestObjectAllowedAlgorithms` and appear in discovery (`request_object_signing_alg_values_supported`)
- JARM: responses can be JWT-wrapped when configured (verify signature and claims)

See: `docs/jar-replay-cache.md`

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

## 6) Error handling and UX

- User flows: cancellation/timeouts/invalid_scope produce friendly error pages; correlate via request IDs in logs
- API calls: map OAuth error codes to client behavior (retry, prompt, or fail fast)
- Token Exchange errors
  - `invalid_target` when audience not allowed
  - `insufficient_scope` when scopes are not permitted
  - `dpop_same_key_required` / `dpop_bridging_not_supported` per policy

## 7) Minimal client snippets

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

## 8) Testing and environments

- Use the provided `.http` files under `docs/http` for quick endpoint testing
- In CI, spin up Redis to exercise replay cache and rate-limit paths
- Lock SDK/toolchain to a known-good version until .NET 9 GA

---

Related docs
- `docs/obo-client-policy.md`
- `docs/obo-dpop-requiresamejkt-e2e.md`
- `docs/jar-replay-cache.md`
