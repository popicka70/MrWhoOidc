# Token Exchange E2E: DPoP RequireSameJkt

This document walks through an end-to-end Token Exchange where the subject token is DPoP-bound and the caller client policy requires same-key bridging (`OboDpopMode = RequireSameJkt`).

Status
- Implemented: same-key check and outgoing `cnf.jkt` binding.
- Pending: verify `/token` DPoP proof with `ath` bound to the `subject_token` (Phase 2).

## Pre-requisites
- Feature flag enabled: `Auth:Features:EnableTokenExchange`.
- Caller client configured with OBO policy, e.g.:
```json
{
  "ClientId": "caller-app",
  "OboEnabled": true,
  "OboAllowedCallersJson": ["caller-app"],
  "OboAllowedTargetAudiencesJson": ["api-b"],
  "OboDpopMode": "RequireSameJkt"
}
```
- Subject access token is DPoP-bound to JKT `K` (has claim `cnf: { jkt: K }`).
- Caller presents a valid DPoP proof for `/token` with the same JKT `K`.

## Flow
1) Acquire subject access token (user token) for API A with DPoP binding (JKT `K`).
2) Caller (client `caller-app`) prepares Token Exchange request:
   - `grant_type = urn:ietf:params:oauth:grant-type:token-exchange`
   - `subject_token = <jwt access token bound with cnf.jkt=K>`
   - `subject_token_type = urn:ietf:params:oauth:token-type:access_token`
   - `audience` or `resource` = `api-b`
   - optional `scope` subset of subject scopes
3) Caller includes a `DPoP` header on the `/token` request using the same key pair (thus same `jkt = K`).
4) Server behavior:
   - Validates client authentication (confidential or allowed `private_key_jwt`).
   - Validates DPoP proof for `/token` endpoint.
   - Validates subject token (signature/iss/exp/nbf, `aud` vs `ApiAudiences`, single-hop by rejecting `act`).
   - Enforces OBO policy (`IOboPolicyService`) for caller: caller allow-list, source/target audiences, scopes, lifetime.
   - Enforces `RequireSameJkt`: `jkt` from DPoP must match subject `cnf.jkt`; binds outgoing token `cnf.jkt = K`.
5) Response: new access token (JWT or opaque) with:
   - `sub` = subject `sub`
   - `act` = `{ "sub": "caller-app" }`
   - `aud` = `api-b`
   - `scope` = narrowed set
   - `cnf.jkt = K` when JWT; opaque tokens persist `CnfJkt`

## Minimal test harness (pseudo)

- Issue a subject token with `cnf.jkt` = `K`:
  - Use `DPoP` for `/authorize` and `/token` to bind.
- Perform TE request (PowerShell curl-style):

```powershell
# Construct DPoP proof JWT for POST https://as.example.com/token using key K
$proof = New-DPoPProof -Hts 'POST' -Htu 'https://as.example.com/token' -Jwk $K

$body = @{
  grant_type = 'urn:ietf:params:oauth:grant-type:token-exchange'
  subject_token = '<SUBJECT_TOKEN>'
  subject_token_type = 'urn:ietf:params:oauth:token-type:access_token'
  audience = 'api-b'
  scope = 'read'
}

Invoke-RestMethod -Method Post -Uri 'https://as.example.com/token' -Headers @{ 'Authorization'='Basic <client creds>'; 'DPoP'=$proof } -Body $body
```

Validate:
- Parse the returned access token (if JWT) and check `act` and `cnf.jkt` = `K`.
- Call API B with the new token using DPoP key `K`.

## Troubleshooting
- `invalid_request` + `dpop_same_key_required`: The DPoP proof was missing or used a different key than the subject token.
- `invalid_request` + `dpop_bridging_not_supported`: Client policy is `Deny` for bridging, or subject is DPoP-bound and bridging disabled.
- `insufficient_scope`: Requested scopes not included in subject or not allowed per policy.
- `invalid_target`: Target audience not allowed per policy/server audiences.

## Next steps (Phase 2)
- Enforce `ath` binding where the DPoP proof's `ath` must match the hash of the `subject_token` to prevent token substitution during exchange.

---

See also: `docs/obo-client-policy.md` and `docs/idp-chaining-backlog.md`.