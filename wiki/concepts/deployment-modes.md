---
title: Deployment Modes
type: concept
tags: [deployment, docker, aspire, operations]
created: 2026-04-22
updated: 2026-04-22
related_files:
  - README.md
  - docker-compose.dev.yml
  - docker-compose.yml
  - MrWhoOidc.AppHost/Program.cs
  - docs/production-setup-guide.md
  - docs/deployment-guide.md
---

MrWhoOidc supports three main run modes, and they serve different jobs. The important distinction is whether the environment is optimized for fast local work, IDE-first orchestration, or production bootstrap and operations.

## Modes

- Local Docker Compose: `docker-compose.dev.yml` is the default fast-start path and includes seeded data plus example apps.
- Aspire AppHost: `MrWhoOidc.AppHost` provides orchestration for local .NET debugging and service composition.
- Production Compose: `docker-compose.yml` is production-oriented and expects explicit bootstrap behavior instead of dev auto-seeding.

## Operational Notes

- Development mode is opinionated and optimized for immediate sign-in and testing.
- Production guidance is split across setup, deployment, security, and upgrade documents.
- Wiki updates in this area should track changes in bootstrap requirements, seed behavior, service composition, or exposed ports.

## Related Pages

- [[mrwhooidc-apphost]]
- [[testing-strategy]]