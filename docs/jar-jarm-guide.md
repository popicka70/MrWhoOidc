# JAR & JARM Integration Guide

This guide explains how the `MrWhoOidc.Client` package enables signed authorization requests (JAR) and JWT-secured authorization responses (JARM).

## Enabling JAR

1. Register the client's public signing key in MrWhoOidc (`PublicJwksJson` or `PublicJwksUri`). The server validates request objects only against the client's registered JWK/JWKS and only with the algorithms in `Auth:RequestObjectAllowedAlgorithms` (default `RS256`, `PS256`, `ES256`, `ES384`, `ES512`; per-client overrides via `Auth:RequestObjectAllowedAlgorithmsPerClient`). `iss` (and `sub`, if present) must equal the `client_id`, `aud` must be `{issuer}/authorize` (set `Jar.Audience` if the client library derives a different value), and `jti`/`nonce` is replay-checked (see [reference/jar-replay-cache.md](reference/jar-replay-cache.md)).
2. Configure the client options:

   ```json
   "MrWhoOidc": {
     "ClientId": "web-client",
     "ClientSecret": "<secret>",
     "Jar": {
       "Enabled": true,
       "SigningAlgorithm": "RS256",
       "Lifetime": "00:05:00"
     }
   }
   ```

   - Supply the private key through `Jar.SigningCredentialsResolver`. The client library can sign with `ClientSecret` (HS256), but the server rejects HMAC request objects unless an operator explicitly adds the algorithm to the allow-list and registers a matching key, so use an asymmetric algorithm.

3. When you call `IMrWhoAuthorizationManager.BuildAuthorizeRequestAsync`, the helper emits a signed JWT request object containing all authorization parameters.

## Enabling JARM

1. JARM responses are signed with the server's active signing key (published in its JWKS). If the client registers `authorization_encrypted_response_alg`/`enc`, the response is also encrypted to the client's `enc` key; when that key cannot be resolved or the alg/enc pair is unsupported, the server fails the request instead of falling back to a plaintext response.
2. Update options:

   ```json
   "MrWhoOidc": {
     "Jarm": {
       "Enabled": true,
       "ResponseMode": "query.jwt",
       "ValidateHashes": true
     }
   }
   ```

3. The `ValidateCallbackAsync` helper now detects the `response` parameter, validates the JWT using the cached JWKS keys, and surfaces a structured result.

## Sample toggle in Razor app

The Razor Pages sample (`Examples/MrWhoOidc.RazorClient`) includes two sign-in buttons:

- **Standard sign-in** uses classic query parameters.
- **Sign in (JAR + JARM)** issues a signed request object and expects a JARM payload.

This toggle simply passes `mode=jar` to the login handler, which sets `UseJar`/`UseJarm` on the per-request options.

## Troubleshooting

| Symptom | Likely cause | Suggested fix |
| --- | --- | --- |
| `invalid_response` with `c_hash` mismatch | Authorization response code was altered or signed with a different key | Verify JWKS cache is invalidated after key rotation and the response is not modified by intermediaries. |
| `invalid_state` after redirect | Cookie storing state expired or multiple tabs reused the same state | Increase session lifetime or ensure a unique login per tab. |
| `JAR is enabled but no signing credentials are configured` | `ClientSecret` missing and no custom resolver provided | Configure `Jar.SigningCredentialsResolver`. |
| `invalid_request_object` from `/authorize` | Signature alg not allowed, no registered client JWK/JWKS, `iss`/`sub` mismatch, lifetime over `Auth:RequestObjectMaxLifetimeSeconds`, or replayed `jti` | Register the client's public key, sign with an allowed asymmetric alg, and use a fresh `jti` per request. |
| `Failed to validate JARM response` with inner `IDX10501` | JWKS endpoint unavailable or response signed by unknown key | Check network connectivity and confirm the authorization server publishes the signing keys. |

For deeper diagnostics, enable debug logging on `MrWhoOidc.Client.Authorization.MrWhoAuthorizationManager` to capture validation details.
