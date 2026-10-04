---
title: Deployment Modes
type: concept
tags: [deployment, docker, aspire, operations]
created: 2026-04-22
updated: 2026-10-04
related_files:
  - README.md
  - docker-compose.dev.yml
  - docker-compose.yml
  - MrWhoOidc.AppHost/AppHost.cs
  - docs/production-setup-guide.md
  - docs/deployment-guide.md
---

MrWhoOidc supports three main run modes, and they serve different jobs. The important distinction is whether the environment is optimized for fast local work, IDE-first orchestration, or production bootstrap and operations.

## Modes

- Local Docker Compose: `docker-compose.dev.yml` is the default fast-start path and includes seeded data, example apps, MailHog, and a second WebAuth instance (`webauth-upstream`, port 9443, own PostgreSQL/Redis) used as an upstream IdP.
- Aspire AppHost: `MrWhoOidc.AppHost` starts PostgreSQL (with pgAdmin), WebAuth, TestApi, and RazorClient for local .NET debugging.
- Production Compose: `docker-compose.yml` is production-oriented and expects explicit bootstrap behavior instead of dev auto-seeding.

## Operational Notes

- Development mode is opinionated and optimized for immediate sign-in and testing.
- Explicitly opted-in development auto-seeding runs before background services when an issuer/public base URL is configured; otherwise it remains request-triggered.
- Development WebAuth key rings are database-backed. The example apps share source-linked Data Protection configuration and persist separate encrypted key rings in named volumes, using the mounted development PFX.
- Listener bindings use `ASPNETCORE_URLS`; the singular `ASPNETCORE_HTTPS_PORT` configures redirection without adding conflicting listeners.
- Production guidance is split across setup, deployment, security, and upgrade documents.
- Wiki updates in this area should track changes in bootstrap requirements, seed behavior, service composition, or exposed ports.

## Related Pages

- [[mrwhooidc-apphost]]
- [[testing-strategy]]