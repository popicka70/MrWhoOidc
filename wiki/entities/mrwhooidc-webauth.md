---
title: MrWhoOidc.WebAuth
type: entity
tags: [webauth, endpoints, ui, admin]
created: 2026-04-22
updated: 2026-04-22
related_files:
  - MrWhoOidc.WebAuth/Program.cs
  - MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs
  - MrWhoOidc.WebAuth/Background/BackchannelLogoutDispatcher.cs
  - docs/admin-guide.md
---

`MrWhoOidc.WebAuth` is the HTTP and UI host for the identity provider. It exposes the protocol endpoints, discovery/JWKS surfaces, and the tenant/admin UX through minimal APIs and Razor Pages.

## Responsibilities

- Register and expose protocol endpoints such as discovery, authorize, token, userinfo, and logout.
- Host the admin and self-service UI surfaces.
- Coordinate background delivery features such as back-channel logout dispatch.
- Translate domain behavior into HTTP routes, responses, and page flows.

## Notes

- The codebase guidance says WebAuth should stay focused on HTTP composition and UI, not accumulate core protocol business logic.
- Because it is the main externally visible surface, architectural changes here often need matching updates in both docs and examples.

## Related Pages

- [[mrwhooidc-auth]]
- [[oidc-protocol-surface]]
- [[backchannel-logout]]
- [[deployment-modes]]