---
title: Example Applications
type: concept
tags: [examples, demos, integration, local-development]
created: 2026-04-22
updated: 2026-07-23
related_files:
  - docs/example-applications-guide.md
  - Examples/MrWhoOidc.RazorClient/README.md
  - docker-compose.dev.yml
  - MrWhoOidc.AppHost/Program.cs
---

The repository uses example applications as integration references rather than as throwaway samples. They cover different client styles, different language stacks, and different local run modes, which makes them part of the practical product surface.

## Main Example Set

- `Examples/MrWhoOidc.OidcDemo` is the smallest interactive Razor Pages sign-in sample.
- `Examples/MrWhoOidc.RazorClient` plus `Examples/MrWhoOidc.TestApi` form the primary .NET end-to-end demo, including normal on-behalf-of and client-bound user delegation calls.
- `Examples/ReactOidcClient` shows a SPA flow using PAR, PKCE, and front-channel logout.
- `Examples/MrWhoOidc.GoWebClient` and `Examples/MrWhoOidc.GoApi` provide non-.NET integration references.

## Local Workflow Mapping

- `docker-compose.dev.yml` is the widest example surface and starts the auth server plus the dockerized samples.
- `MrWhoOidc.AppHost` focuses on the primary .NET demo pair rather than every sample.
- The Go examples remain manual-run references.

## Why This Matters

- The examples act as executable documentation for client integration.
- Changes to issuer shape, endpoint behavior, or downstream API expectations can break examples before they break formal docs.
- E2E coverage uses some of these applications directly, so they sit at the boundary between documentation and validation.
- The RazorClient `/Delegated` page performs server-side token exchange for a grant bound to `blazor-web`; TestApi displays delegator, delegate actor, grant, and authorized client claims.

## Related Pages

- [[deployment-modes]]
- [[testing-strategy]]
- [[mrwhooidc-apiservice]]