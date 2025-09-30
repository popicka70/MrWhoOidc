# JAR & JARM Integration Guide

This guide explains how the `MrWhoOidc.Client` package enables signed authorization requests (JAR) and JWT-secured authorization responses (JARM).

## Enabling JAR

1. Ensure your client registration in MrWhoOidc has a shared secret or a signing key pair.
2. Configure the client options:

   ```json
   "MrWhoOidc": {
     "ClientId": "web-client",
     "ClientSecret": "<secret>",
     "Jar": {
       "Enabled": true,
       "SigningAlgorithm": "HS256",
       "Lifetime": "00:05:00"
     }
   }
   ```

   - Set `Jar.SigningAlgorithm` to `HS256` for symmetric secrets or `RS256` if you return asymmetric credentials from `Jar.SigningCredentialsResolver`.
   - Provide a custom `Jar.SigningCredentialsResolver` when keys rotate externally.

3. When you call `IMrWhoAuthorizationManager.BuildAuthorizeRequestAsync`, the helper emits a signed JWT request object containing all authorization parameters.

## Enabling JARM

1. Expose a `JWKS` document from the authorization server with the signing keys used for JARM responses.
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
| `JAR is enabled but no signing credentials are configured` | `ClientSecret` missing and no custom resolver provided | Add a client secret or configure `Jar.SigningCredentialsResolver`. |
| `Failed to validate JARM response` with inner `IDX10501` | JWKS endpoint unavailable or response signed by unknown key | Check network connectivity and confirm the authorization server publishes the signing keys. |

For deeper diagnostics, enable debug logging on `MrWhoOidc.Client.Authorization.MrWhoAuthorizationManager` to capture validation details.
