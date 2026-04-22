---
title: MrWhoOidc.ApiService
type: entity
tags: [api, downstream-service, admin-api, bearer-tokens]
created: 2026-04-22
updated: 2026-04-22
related_files:
  - MrWhoOidc.ApiService/Program.cs
  - MrWhoOidc.Auth/Persistence/AuthDbContext.cs
  - docs/example-applications-guide.md
---

`MrWhoOidc.ApiService` is the protected API surface that examples and tests can call after authentication. It reuses the auth persistence layer, applies bearer-token-based authorization, and exposes admin-oriented CRUD endpoints over core data such as scopes and clients.

## Responsibilities

- Reuse auth persistence through `AddAuthPersistence` rather than defining a separate data store.
- Configure JWT bearer authentication and authorization policies for admin and API access.
- Expose administrative endpoints for scopes, client scopes, and client CRUD slices.
- Act as a downstream protected API surface in local examples and E2E scenarios.

## Notes

- The current program config uses an `AdminAuth` section to define issuer and role expectations for the admin policy.
- In development fallback mode, token validation is intentionally relaxed compared with a fully configured issuer path.
- Because it sits between core persistence and client-facing examples, it is both an API sample and an operational surface.

## Related Pages

- [[mrwhooidc-auth]]
- [[mrwhooidc-security]]
- [[example-applications]]
- [[testing-strategy]]