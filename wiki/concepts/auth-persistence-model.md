---
title: Auth Persistence Model
type: concept
tags: [persistence, ef-core, postgres, data-model]
created: 2026-04-22
updated: 2026-10-04
related_files:
  - MrWhoOidc.Auth/Persistence/AuthDbContext.cs
  - .github/copilot-instructions.md
---

`AuthDbContext` shows that MrWhoOidc is not just an endpoint host; it has a broad persistence model spanning tenancy, users, clients, tokens, federation, logout infrastructure, and platform-level concerns. The database shape is a reliable map of what the product actually has to manage.

## Main Domains

- Tenancy: tenants, tenant icons, and tenant-scoped membership surfaces.
- Accounts and users: user accounts, per-tenant users, WebAuthn credentials, alternative emails, confirmations, and password reset state.
- Client and token lifecycle: clients, client secrets, authorization codes, consents, tokens, PAR requests, and device codes.
- Authorization model: realms, roles, scopes, client scopes, and user assignments.
- Federation and registration: identity providers, claim mappings, provider keys, initial access tokens, and dynamic registration tokens.
- Delegation and support: durable tenant support sessions plus user-to-user delegated access grants bound to one tenant, two global accounts, and one OAuth/OIDC client.
- Operational and platform concerns: back-channel logout notifications, logout redirect references, audit events, legacy impersonation logs, licensing tables, platform settings, and data-protection keys.

## Behavioral Signals in the Context

- The overridden save methods normalize email fields and ensure generated user IDs are available before persistence.
- The context protects against duplicate `User.Id` collisions by reassigning IDs and logging the event.
- The breadth of the sets explains why new feature work often needs both protocol logic and admin/operational documentation updates.
- `AuthorizationCode` rows store the code hash plus the login context (`Sid`, `UpstreamIdp`, `UpstreamAcr`, `UpstreamAmr`, `MappedClaimsJson`), so ID tokens keep these claims regardless of which replica serves `/token`. There is no in-memory authorization-code metadata store.
- PAR rows are consumed with a conditional update inside the code-issuance transaction, making `request_uri` single-use.
- `SigningKeys` private JWKs are protected before the first save and cached only in process memory (never in the Redis tier).
- Tenant query filters hide other tenants' rows, so background jobs either query with `IgnoreQueryFilters()` or iterate tenants via `BackgroundServiceTenantHelper.ForEachActiveTenantAsync`.
- Delegated grant client binding is stored as a client foreign key. Legacy rows may remain unbound for history, but new grants require a same-tenant client and unbound grants cannot be accepted or activated.

## Why This Matters For The Wiki

- Structural features usually show up here before they are fully visible in top-level docs.
- This is the right place to derive future deeper pages for client lifecycle, federation, licensing, or tenant management.
- If a new table or domain area appears here, the wiki likely needs a corresponding entity or concept page.

## Related Pages

- [[mrwhooidc-auth]]
- [[oidc-protocol-surface]]
- [[backchannel-logout]]