# Client OBO Policy

This page describes the per-client On-Behalf-Of (OBO) / Token Exchange policy knobs and shows minimal configuration examples. These settings control which callers can perform token exchange, which audiences and scopes are allowed, lifetime caps, DPoP bridging behavior, and delegation depth.

## Fields on `Client`

- `OboEnabled` (bool?)
  - Enables OBO for the client. `null` or `true` => enabled; `false` => disabled (rejects with `unauthorized_client`).
- `OboAllowedCallersJson` (string[])
  - Allow-list of `client_id` values permitted to perform exchange. Empty/null => no restriction.
- `OboAllowedSourceAudiencesJson` (string[])
  - Allow-list for the subject token's `aud`. If non-empty and subject has `aud`, it must be contained.
- `OboAllowedTargetAudiencesJson` (string[])
  - Allow-list for the requested target audience/resource. Falls back to server `ApiAudiences` when empty.
- `OboAllowedScopesJson` (string[])
  - Allowed scopes for exchanged tokens. Resulting scopes = `requested ∩ subject ∩ allowed`. Empty => no per-client restriction (still narrowed to subject and request).
- `OboMaxDelegationDepth` (int?)
  - Maximum delegation depth for exchanges (default `1`: single hop). Enforced for opaque subjects via `Token.DelegationDepth`. Exceeded => `invalid_grant` (`max_delegation_depth_exceeded`).
- `OboMaxLifetimeMinutes` (int?)
  - Maximum lifetime for exchanged tokens (default `15`). Effective lifetime = min(subject remaining, this cap, default).
- `OboDpopMode` (enum)
  - DPoP bridging policy for DPoP-bound subject tokens:
  - `Deny` (default): reject exchanges when subject has `cnf.jkt` (`invalid_request` with `dpop_bridging_not_supported`).
  - `RequireSameJkt`: require endpoint DPoP proof and same `jkt` as subject; outgoing token bound (`cnf.jkt`) to that key.
  - `AllowSameJktOnly`: only allow when subject is DPoP-bound and same `jkt` proof is presented; outgoing token bound to that key.

Notes
- JWT subjects are always limited to single-hop by refusing a subject with an `act` claim.
- Opaque subjects track `DelegationDepth` in storage and are checked against `OboMaxDelegationDepth`.
- Current implementation validates the endpoint DPoP proof, enforces same-key requirement per policy, and enforces `ath` binding to the `subject_token` (Phase 2 complete).

## Examples

Example A — Deny DPoP bridging (default), single hop, allow basic exchange to `api-b` with narrowed scopes
```json
{
  "ClientId": "caller-app",
  "OboEnabled": true,
  "OboAllowedCallersJson": ["caller-app"],
  "OboAllowedSourceAudiencesJson": ["api-a"],
  "OboAllowedTargetAudiencesJson": ["api-b"],
  "OboAllowedScopesJson": ["read", "email"],
  "OboMaxDelegationDepth": 1,
  "OboMaxLifetimeMinutes": 15,
  "OboDpopMode": "Deny"
}
```

Example B — Require same DPoP key bridging with depth 2 and 10-minute cap
```json
{
  "ClientId": "caller-app",
  "OboEnabled": true,
  "OboAllowedCallersJson": ["caller-app"],
  "OboAllowedSourceAudiencesJson": ["api-a"],
  "OboAllowedTargetAudiencesJson": ["api-b"],
  "OboAllowedScopesJson": ["read", "write"],
  "OboMaxDelegationDepth": 2,
  "OboMaxLifetimeMinutes": 10,
  "OboDpopMode": "RequireSameJkt"
}
```

## Admin UI (coming soon)

A per-client OBO editor will surface these fields under the Clients section in the Admin UI. Screenshots and guidance will be added here once the pages are available.

---

See also: `docs/idp-chaining-backlog.md` (section 11) for the implementation status and acceptance criteria, and `docs/obo-dpop-requiresamejkt-e2e.md` for an end-to-end walkthrough of the RequireSameJkt flow.