---
title: MrWhoOidc.ApiService
type: entity
tags: [api, downstream-service, admin-api, bearer-tokens]
created: 2026-04-22
updated: 2026-10-04
related_files:
  - MrWhoOidc.ApiService/Program.cs
  - MrWhoOidc.Auth/Persistence/AuthDbContext.cs
  - docs/example-applications-guide.md
---

`MrWhoOidc.ApiService` is a bearer-protected admin API host started by the Aspire AppHost. It reuses the auth persistence layer, applies bearer-token-based authorization, and exposes admin-oriented CRUD endpoints over core data such as scopes and clients.

## Responsibilities

- Reuse auth persistence through `AddAuthPersistence` rather than defining a separate data store.
- Configure JWT bearer authentication and authorization policies for admin and API access.
- Expose administrative endpoints for scopes, client scopes, and client CRUD slices.
- Serve as an additional protected API in the AppHost workflow. The example apps and the E2E suite use `Examples/MrWhoOidc.TestApi` as their downstream API, not this project.

## Notes

- The current program config uses an `AdminAuth` section to define issuer and role expectations for the admin policy.
- In development fallback mode, token validation is intentionally relaxed compared with a fully configured issuer path.
- It has no DPoP-specific handling; DPoP coverage lives in WebAuth, `MrWhoOidc.Security`, and the E2E protocol tests.

## Related Pages

- [[mrwhooidc-auth]]
- [[mrwhooidc-security]]
- [[example-applications]]
- [[testing-strategy]]