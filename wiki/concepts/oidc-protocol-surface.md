---
title: OIDC Protocol Surface
type: concept
tags: [oidc, oauth, endpoints, protocol]
created: 2026-04-22
updated: 2026-07-23
related_files:
  - MrWhoOidc.WebAuth/Program.cs
  - MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs
  - docs/developer-guide.md
  - docs/oidc-idp-feature-reference.md
---

MrWhoOidc exposes the standard OIDC and OAuth surfaces through `MrWhoOidc.WebAuth`, while keeping protocol logic and persistence-heavy behavior in `MrWhoOidc.Auth`. The split matters: WebAuth is the HTTP shell, Auth is the behavioral core.

## Main Surfaces

- Discovery and JWKS live in the WebAuth host and are intended to be externally consumable.
- Authorization, token, userinfo, and logout are exposed from the WebAuth surface with handler classes and Razor Pages as needed.
- Admin and tenant management flows share the same host but are conceptually separate from the protocol endpoints.

## Layer Boundary

- `MrWhoOidc.WebAuth` should own routing, endpoint composition, Razor Pages, discovery exposure, and response shaping.
- `MrWhoOidc.Auth` should own protocol validation, persistence, key material, token-related business rules, and durable data access.
- This boundary is reinforced in the repository guidance and should stay stable when new features are added.

## Notable Flows

- Authorization Code with PKCE is a first-class path for browser-based examples.
- Client Credentials and Token Exchange are present for service-to-service and delegated scenarios.
- Client-bound delegated exchange uses an explicit private `delegation_id` parameter. The authenticated confidential client must match the grant's bound client; delegated tokens preserve delegator `sub`, delegate `act.sub`, grant ID, and authorized client.
- DPoP support is part of the repo’s security posture and shows up in both tests and downstream example integrations.

## Related Pages

- [[mrwhooidc-auth]]
- [[mrwhooidc-webauth]]
- [[backchannel-logout]]
- [[testing-strategy]]