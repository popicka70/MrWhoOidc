---
title: MrWhoOidc.AppHost
type: entity
tags: [aspire, orchestration, local-development]
created: 2026-04-22
updated: 2026-04-22
related_files:
  - MrWhoOidc.AppHost/Program.cs
  - README.md
---

`MrWhoOidc.AppHost` is the Aspire-based local orchestration entry point. It is not the production host shape; it exists to make local service composition and debugging easier inside the .NET workflow.

## Responsibilities

- Compose local services and dependencies for IDE-driven development.
- Offer an alternative to Docker Compose when working inside the .NET toolchain.
- Keep local wiring discoverable for contributors who prefer Aspire to raw container commands.

## Related Pages

- [[deployment-modes]]
- [[mrwhooidc-webauth]]