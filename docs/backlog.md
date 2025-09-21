# MrWhoOidc – User Management and OIDC Enhancements Backlog

Status legend
- [ ] Not started
- [~] In progress
- [x] Done

Scope
Implement user management with primary/alternative emails, per-client user assignment, realms and roles, role exposure in userinfo/tokens, and client-assigned scopes with enforcement. Target runtime: .NET 9.

Note: Admin management UI now lives in `MrWhoOidc.WebAuth` (the OIDC server). Previous references to `MrWhoOidc.Web` for admin UI have been moved to `MrWhoOidc.WebAuth`.

Milestones (suggested order)
0 ? 1 ? 5/3/4 ? 6 ? 7 ? 8/9 ? 10/11 ? 12 ? 13/14

Epic 0 – Baseline and decisions
- Story 0.1: Document current OIDC stack and flows
  - AC:
    - [ ] Architecture doc created (projects, OpenIddict/Auth pipeline, Identity usage, DB schema)
    - [ ] Current handlers and filters listed
    - [ ] Token and userinfo current shape captured
  - Target: `MrWhoOidc.Auth`

Epic 1 – Data model and migrations
- Story 1.1: Add core entities and relationships
  - Entities:
    - Realm(Id, Name, Slug, IsActive)
    - Role(Id, Name, RealmId, IsActive)
    - Scope(Name, Description, IsExposed)
    - ClientScope(ClientId, ScopeName)
    - UserAlternativeEmail(Id, UserId, Email, IsVerified, VerifiedAt)
    - UserClientAssignment(UserId, ClientId, RealmId, IsActive)
    - UserRoleAssignment(UserId, RoleId, ClientId, RealmId, IsActive)
    - Split role assignments: UserRealmRoleAssignment(UserId, RoleId, RealmId, IsActive), UserClientRoleAssignment(UserId, RoleId, ClientId, IsActive)
  - AC:
    - [x] EF Core entities created with navigation props and constraints
    - [x] Migrations added and applied
    - [x] Unique indexes (email per user, role name per realm, scope name global, client+scope unique)
    - [x] Seeds: default realm, admin role, common scopes (openid, profile, email, offline_access, roles)
  - Target: `MrWhoOidc.Auth`

Epic 2 – Identity model extensions
- Story 2.1: Extend user model with primary/alternative emails
  - AC:
    - [x] `ApplicationUser` has `PrimaryEmail`, `EmailVerifiedAt`
    - [x] Navigation and CRUD for alternative emails
    - [~] Email normalization/validation in place
    - [x] Migration applied
  - Target: `MrWhoOidc.Auth`
- Story 2.2: Email verification for all emails
  - AC:
    - [ ] Verification token model and persistence
    - [ ] Endpoints to verify/resend verification
    - [ ] Status updates for primary/alternative emails
  - Target: `MrWhoOidc.Auth`, `MrWhoOidc.ApiService`, `MrWhoOidc.Web`

Epic 3 – Client and scope management
- Story 3.1: CRUD for scopes
  - AC:
    - [x] Create/update/delete/list scopes
    - [x] Validation; cannot delete in-use scope
  - Target: `MrWhoOidc.WebAuth`
- Story 3.2: Assign scopes to clients
  - AC:
    - [x] Persist `ClientScope`
    - [x] List effective scopes per client
    - [x] Prevent duplicates
  - Target: `MrWhoOidc.WebAuth`
- Story 3.3: Enforce requested scopes ? assigned client scopes
  - AC:
    - [x] Authorization requests fail with invalid_scope when requesting disallowed scopes
    - [ ] Unit tests for scope enforcement
  - Target: `MrWhoOidc.Auth`

Epic 4 – Role and realm management
- Story 4.1: CRUD roles per realm
  - AC:
    - [~] Create/update/delete/list roles scoped to realm (UI in WebAuth)
    - [ ] Cannot delete role in use (update guard to consider both realm and client assignments)
  - Target: `MrWhoOidc.WebAuth`
- Story 4.2: CRUD realms
  - AC:
    - [~] Create/update/delete/list realms; toggle IsActive
    - [ ] Cannot delete realm in use
  - Target: `MrWhoOidc.WebAuth`

Epic 5 – Assignments and enforcement
- Story 5.1: Assign users to clients (optionally per realm)
  - AC:
    - [x] UI to add/remove user-client (+realm) assignments
    - [x] Validation and deduplication
  - Target: `MrWhoOidc.WebAuth`
- Story 5.2: Assign roles to users
  - AC:
    - [~] UI for assigning roles separately as realm-role and client-role (bulk ops TBD)
  - Target: `MrWhoOidc.WebAuth`
- Story 5.3: Enforce “user can only log into assigned clients”
  - AC:
    - [x] During auth flow, requests for unassigned client (and realm, if applicable) are rejected consistently (pre-consent)
    - [ ] Tests for allowed vs disallowed login
  - Target: `MrWhoOidc.Auth`
- Story 5.4: Realm awareness in auth flow
  - AC:
    - [~] Realm inferred from client or explicit parameter; validated; stored in ticket
    - [~] Error states handled (realm inactive/unknown)
  - Target: `MrWhoOidc.Auth`, `MrWhoOidc.WebAuth`

Epic 6 – Claims, tokens, and userinfo
- Story 6.1: Define supported scopes and claims
  - AC:
    - [x] Discovery document advertises `openid profile email offline_access roles`
    - [x] Custom claim docs: `roles`, `realm`
  - Target: `MrWhoOidc.Auth`
- Story 6.2: Add roles to ID/access token and userinfo when `roles` scope granted
  - AC:
    - [~] Roles limited to current client+realm
    - [~] Claim `roles` issued only when scope granted (update TokenService to union realm-role and client-role assignments)
    - [ ] Unit tests
  - Target: `MrWhoOidc.Auth`
- Story 6.3: Add emails to userinfo
  - AC:
    - [x] `email` and `email_verified` per OIDC
    - [x] `emails` array (custom) when `email` scope granted; configurable to include only verified
  - Target: `MrWhoOidc.Auth`
- Story 6.4: Include realm claim
  - AC:
    - [x] `realm` claim added to tokens/userinfo reflecting active realm
  - Target: `MrWhoOidc.Auth`

Epic 7 – Admin API / backend
- Story 7.1: Users CRUD + email management
- Story 7.2: Clients CRUD + scope assignments
- Story 7.3: Roles CRUD (per realm)
- Story 7.4: Realms CRUD
- Story 7.5: User-client assignments
- Story 7.6: User-role assignments (realm-role and client-role)
  - AC for all:
    - [~] OpenAPI documented (if/when APIs are exposed)
    - [~] Validation with consistent error contracts
    - [x] RBAC-protected admin UI (WebAuth `"admin"` policy)
  - Target: `MrWhoOidc.WebAuth` (UI-first; APIs optional)

Epic 8 – Admin UI
- Story 8.1: Users pages (primary/alternate emails, verification, assignments)
- Story 8.2: Clients pages (assign scopes; view users with access)
- Story 8.3: Roles pages (per realm)
- Story 8.4: Realms pages
- Story 8.5: Scopes pages
  - AC for all:
    - [ ] Paging, filtering, search
    - [ ] Success/error toasts; guards against losing unsaved changes
  - Target: `MrWhoOidc.WebAuth`

Epic 9 – Auth UI updates
- Story 9.1: Optional realm selection or display
  - AC:
    - [ ] If realm not inferable, allow selecting a realm; else show inferred realm; handle errors gracefully
  - Target: `MrWhoOidc.WebAuth`

Epic 10 – Security, auditing, and policies
- Story 10.1: Audit log
  - AC:
    - [ ] Persist who changed what (users, roles, assignments, scopes) with timestamps
  - Target: `MrWhoOidc.Auth`
- Story 10.2: Input validation and normalization
  - AC:
    - [ ] Email normalization/canonicalization; role/scope naming rules; consistent errors
  - Target: All server projects
- Story 10.3: Permissions for Admin API/UI
  - AC:
    - [x] Admin UI: Only admins (per realm/global) can manage entities; bootstrap admin flow
    - [ ] Admin API endpoints (if exposed) protected by RBAC
  - Target: `MrWhoOidc.WebAuth`, `MrWhoOidc.ApiService`

Epic 11 – Tests
- Story 11.1: Unit tests for handlers and claim issuance
- Story 11.2: Integration tests for auth flows
  - AC:
    - [ ] End-to-end: user assigned vs not assigned; scope enforcement; role claims in userinfo/tokens; realm validation
  - Target: All server projects

Epic 12 – Migrations, seeding, and data upgrade
- Story 12.1: Seed defaults
  - AC:
    - [x] Default realm, scopes, admin role, admin user; sample client with scopes
- Story 12.2: Data migration/backfill
  - AC:
    - [x] Attach existing users/clients to default realm; safe rollout plan
  - Target: `MrWhoOidc.Auth`

Epic 13 – Observability and ops
- Story 13.1: Structured logging and metrics
  - AC:
    - [ ] Logs around authorization decisions (no PII)
    - [ ] Metrics for rejected auths due to assignment/scope
  - Target: `MrWhoOidc.Auth`

Epic 14 – Documentation
- Story 14.1: Admin guide
  - AC:
    - [ ] How to create realms/roles/scopes, assign users; how roles appear in userinfo
- Story 14.2: OIDC integration guide for clients
  - AC:
    - [ ] Required scopes; interpreting `roles` and `realm` claims
  - Target: `MrWhoOidc.Auth`, `MrWhoOidc.ApiService`, `MrWhoOidc.WebAuth`

Cross-cutting decisions and notes
- Role assignments are split: realm-level (UserRealmRoleAssignment) and client-level (UserClientRoleAssignment).
- Claim names: use `roles` (custom), `realm` (custom), standard `email`/`email_verified`.
- Scope gating: only include custom claims when corresponding scopes are granted.
- Enforcement points: request validation handlers and consent handling in the OIDC pipeline.
- Data integrity: soft-delete flags where needed (IsActive) and unique constraints.
- .NET 9: target frameworks aligned across projects; ensure EF Core and OpenIddict versions compatible with .NET 9.
