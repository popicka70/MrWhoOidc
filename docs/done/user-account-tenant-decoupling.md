# User Account / Tenant Decoupling Plan

**Status:** Draft (2025-11-17)
**Owner:** Platform Architecture
**Related Areas:** MrWhoOidc.Auth, MrWhoOidc.WebAuth, multi-tenancy, admin UX

## Goals

- Allow `UserAccount` entities to exist independently from tenants.
- Support linking a single user to multiple tenants via membership records carrying tenant-specific roles, clients, and settings.
- Separate "account login" (authenticating to the OP) from "tenant-scoped IdP usage" (issuing tokens for a tenant/client).
- Introduce first-class tenant selection for admins when no client context is present, and ensure client-driven logins automatically scope to correct tenant membership.

## Current Coupling Snapshot (Baseline)

| Area | Current Behavior | Pain Points |
| --- | --- | --- |
| Data model (`AuthDbContext`) | `User` row carries `TenantId`; `WebAuthnCredential`, `Role`, `Realm`, `Client`, etc. also embed tenant keys | No tenantless users; user cannot span tenants; cascading constraints everywhere |
| Services (`UserService`, stores) | All lookups scoped via `ITenantAccessor`; cache keys include tenant id | Impossible to query a user outside current tenant context |
| Middleware (`TenantResolutionMiddleware`) | Ensures authenticated user belongs to the tenant inferred from `/t/{slug}` | Blocks legitimate multi-tenant admins; no notion of multiple memberships |
| Admin UX (`TenantAwarePageModel`, Razor pages) | Requires single current tenant; filters per tenant | Cannot manage global users; no tenant picker |

## Target Architecture

1. **Global User Account**
   - New `UserAccount` table with credential + security profile (password hashes, MFA, recovery info, alternative emails).
   - Removes `TenantId` column from `User` (or replaces `User` with `UserAccount`).
2. **Membership Join**
   - `UserTenantMembership` (UserAccountId, TenantId, Status, DefaultRealmId, DisplayName)
   - Related tables: `UserTenantRoleAssignment`, `UserTenantClientAssignment`, `UserTenantSettings` (JSON blob for per-tenant preferences).
3. **Service Split**
   - `IUserAccountService` for global account lifecycle.
   - `IUserTenantMembershipService` for tenant assignments and policy enforcement.
4. **Context Handling**
   - `ITenantAccessor` gains `CurrentMembership` (null until user picks a tenant).
   - Middleware validates that, for tenant-specific endpoints, the user selected a membership that matches the route.
5. **UX Flow**
   - Post-login landing page shows tenant picker if user has multiple admin memberships or none mapped to the requested client.
   - Client-initiated authorization automatically narrows membership to tenant owning the client; prompt only if ambiguous.

## Implementation Workstreams

1. **Data & EF Layer**
   - Add new tables/migrations for `UserAccount` + membership join.
   - Update foreign keys in dependent entities to reference `UserAccountId`.
   - Provide backfill migration to create accounts from existing users.
2. **Services & Middleware**
   - Refactor `UserService`, token issuance, and tenant validation logic to operate on accounts + memberships.
   - Introduce membership-aware caching strategies.
   - Update `TenantResolutionMiddleware` to allow multiple memberships and enforce correct selection.
3. **Authentication Flow**
   - Modify login controller/pages to authenticate `UserAccount` first, then resolve membership based on client or prompt.
   - Ensure tokens carry both `sub` (account id) and `tenant`/`membership` claims when relevant.
4. **Admin UX**
   - Create tenant selection UI component and session storage.
   - Update admin pages to operate on memberships (list user once, show per-tenant badges, assign roles per membership).
   - Add global account management section (profile, MFA, linked identities) outside tenant context.
5. **Telemetry & Feature Flags**
   - Wrap changes in feature flags (`UserAccountDecoupling`, `TenantPickerUX`).
   - Emit metrics for membership selection, ambiguous tenant prompts, and cross-tenant admin actions.

## Implementation Status (2025-11-17)

- ✅ Added `UserAccount` and `UserTenantMembership` entities, including normalization logic and EF configuration.
- ✅ Generated migration `AddUserAccountEntities` creating the new tables without touching legacy `User` data.
- ✅ Introduced `IUserAccountService` and `IUserTenantMembershipService` with EF-backed implementations registered via DI.
- ✅ Added `UserAccountFeatureOptions` feature flags (`UserAccountDecouplingEnabled`, `TenantPickerUxEnabled`) and DI plumbing.
- ✅ Seeder now dual-writes admin/alice accounts + memberships whenever the decoupling flag is enabled.
- ✅ Added `IUserAccountProvisioner` and wired it into seeding, tenant provisioning, and admin UI creation flows for consistent dual-write behavior.
- ✅ Registration approvals now create `UserAccount` + membership records (when flagged) so new tenants/admins immediately participate in the new model.
- ⏳ No runtime callers yet; next slices should dual-write from existing `UserService` and expose membership read APIs.

## Migration Strategy

1. **Phase 0 – Schema Prep**
   - Introduce new tables + indexes.
   - Dual-write via triggers or EF interceptors (existing code still reads from old columns).
2. **Phase 1 – Dual Write**
   - Services write to both `User` (legacy) and `UserAccount`/membership tables.
   - Add integration tests to ensure dual consistency.
3. **Phase 2 – Read Switch**
   - Feature flag to switch reads to new model.
   - Middleware/handlers start using memberships for validation.
4. **Phase 3 – UX Rollout**
   - Tenant picker and account portal go live for pilot tenants.
   - Monitor telemetry for membership selection success/failure.
5. **Phase 4 – Cleanup**
   - Remove legacy `TenantId` columns and old code paths after confidence built.

## Open Questions / Decisions Needed

- Should `UserAccount` live in same schema as auth or separate service? (Impacts migrations.)
- How do we handle external IdP links (per tenant vs global)?
- Do platform admins bypass tenant picker or require explicit selection per action?
- Token format changes: include membership identifier? compatibility concerns?

## Next Steps

1. Begin dual-writing from runtime user flows (`UserService`, registration) into `UserAccount` + memberships and expose lookup APIs.
2. Wire remaining user creation paths (external IdP provisioning, platform tenant creation) into the provisioner and start dual-writing from `UserService` updates.
3. Spike tenant picker UX (Razor Page + API) tied to feature flag.
4. Draft ADR documenting token claim changes for multi-tenant accounts.
5. Extend unit tests in `MrWhoOidc.UnitTests` to cover membership resolution and cross-tenant flows.
6. Schedule staging rollout with telemetry dashboards for membership usage.
