---
title: MrWhoOidc.Auth
type: entity
tags: [auth, persistence, crypto, core]
created: 2026-04-22
updated: 2026-10-04
related_files:
  - MrWhoOidc.Auth/Persistence/AuthDbContext.cs
  - MrWhoOidc.Auth/Persistence/Migrations
  - .github/copilot-instructions.md
---

`MrWhoOidc.Auth` is the core domain project for protocol behavior, persistence, crypto, and key-management concerns. It should hold the non-visual OIDC logic that other projects expose or consume.

## Responsibilities

- Own database entities and EF Core persistence.
- Hold protocol validation and token-related business logic.
- Manage key material, signing behavior, and related crypto concerns.
- Provide the durable data model used by higher-level HTTP surfaces.

## Notes

- The repository guidance explicitly says new non-visual OIDC logic belongs here.
- PostgreSQL is expected through the named Aspire connection `authdb`, not through hard-coded connection strings.
- Migration changes should stay under `MrWhoOidc.Auth/Persistence/Migrations`.
- `KeyRotationHostedService` runs once per active tenant through `BackgroundServiceTenantHelper.ForEachActiveTenantAsync`, which isolates per-tenant failures.
- `UserAccountService.UpdatePasswordAsync` rotates the security stamp and revokes the account's tokens on every password update.
- `KeyStore` keeps private JWKs out of the distributed cache tier.

## Related Pages

- [[mrwhooidc-webauth]]
- [[backchannel-logout]]
- [[oidc-protocol-surface]]