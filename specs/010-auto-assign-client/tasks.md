---
description: "Task list for feature implementation"
---

# Tasks: Auto-Assign New Users To Client

**Input**: Design documents from `/specs/010-auto-assign-client/`

**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Not included (the feature specification did not explicitly request tests).

**Organization**: Tasks are grouped by user story (US1–US3) to enable independent implementation and validation.

## Format: `T### [P?] [US#] Description`
- **[P]**: Can run in parallel (different files, no dependencies)
- **[US#]**: Which user story this task belongs to (US1, US2, US3)
- Tasks include exact file paths and/or commands

---

## Phase 1: Setup (Shared Infrastructure)

- [ ] T001 Confirm Constitution gates for this change (no new identity packages; domain logic in Auth, UI/HTTP in WebAuth) per specs/010-auto-assign-client/plan.md
- [ ] T002 Run baseline build/tests before changes: `dotnet build` + `dotnet test` from repo root (MrWhoOidc.slnx)

---

## Phase 2: Foundational (Blocking Prerequisites)

**⚠️ CRITICAL**: No user story work should begin until this phase is complete.

- [ ] T003 Add per-client flag `AutoAssignNewUsersToClient` (default false) to `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (class `Client`)
- [ ] T004 Create EF Core migration adding the new column to Clients in `MrWhoOidc.Auth/Persistence/Migrations/` and update `MrWhoOidc.Auth/Persistence/Migrations/AuthDbContextModelSnapshot.cs`
- [ ] T005 [P] Identify any seed/client creation code paths that must set/assume the new property (e.g., `MrWhoOidc.WebAuth/Services/TenantSeedingService.cs`, `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Create.cshtml.cs`) and ensure they remain correct with default=false
- [ ] T006 Implement a server-side “client context resolver” for local registration that can derive a validated client context from ReturnUrl (JAR/PAR aware) without trusting raw query strings (new file suggested: `MrWhoOidc.WebAuth/Services/ReturnUrlClientContextResolver.cs`)

**Checkpoint**: Foundation ready — user story implementation can begin.

---

## Phase 3: User Story 1 — Auto-assign a brand-new user during login (Priority: P1) 🎯 MVP

**Goal**: When a brand-new user is created during a client-specific sign-in journey, assign them to that client if the client enables the new flag.

**Independent Test**: Start a sign-in journey for a client with auto-assign enabled; create a brand-new local user or complete first-time external IdP sign-in; confirm a `UserClientAssignment` exists for that user and client.

### Implementation

- [ ] T010 [P] [US1] Preserve ReturnUrl when navigating to registration from login page by updating the register link in `MrWhoOidc.WebAuth/Pages/Login.cshtml` to pass `asp-route-returnUrl="@Model.ReturnUrl"`
- [ ] T011 [P] [US1] Preserve ReturnUrl when navigating to registration from provider-selection page by updating the register link in `MrWhoOidc.WebAuth/Pages/Auth/Providers/Select.cshtml` to pass `asp-route-returnUrl="@Model.ReturnUrl"`
- [ ] T012 [US1] Add `ReturnUrl` handling to local registration page model and view: `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs` (+ hidden field in `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml` if needed)
- [ ] T013 [US1] Use the resolver from T006 inside `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs` to resolve the validated target client (if any), load the `Client` entity for the current tenant, and only pass `clientId` into `CreateAndMaybeApproveRegistrationAsync(...)` when `Client.AutoAssignNewUsersToClient == true`
- [ ] T014 [US1] Update external provisioning to load client policy using the validated `clientId` from state (fallback to ReturnUrl parsing only if needed): `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs`
- [ ] T015 [US1] Gate external auto-approval path assignment: only pass `clientEntity.Id` into `CreateAndMaybeApproveRegistrationAsync(...)` when `clientEntity.AutoAssignNewUsersToClient == true` (otherwise pass null): `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs`
- [ ] T016 [US1] Implement external auto-provision assignment for newly created users: when `userWasCreated == true` and `clientEntity.AutoAssignNewUsersToClient == true`, create `UserClientAssignment` (idempotent) in `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs`
- [ ] T017 [US1] Emit audit-relevant record on successful registration approval auto-assignment (user identity + client + time) in `MrWhoOidc.WebAuth/Services/RegistrationService.cs`
- [ ] T018 [US1] Emit audit-relevant record on successful external auto-provision auto-assignment (user identity + client + time) in `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs`

**Checkpoint**: US1 is functional and manually verifiable via quickstart.

---

## Phase 4: User Story 2 — Configure the behavior per client (Priority: P2)

**Goal**: Admins can enable/disable auto-assign per client and see it persist.

**Independent Test**: Create/edit a client in admin UI; verify the setting persists and affects new-user assignment behavior.

### Implementation

- [ ] T020 [US2] Add `AutoAssignNewUsersToClient` to `MrWhoOidc.WebAuth/Pages/Admin/Clients/Add.cshtml.cs` (`ClientInput` + map to `Client` entity)
- [ ] T021 [US2] Render `AutoAssignNewUsersToClient` checkbox in `MrWhoOidc.WebAuth/Pages/Admin/Clients/Add.cshtml`
- [ ] T022 [US2] Add `AutoAssignNewUsersToClient` to `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs` (`ClientInput` + populate in `OnGetAsync` + persist in save/update handler)
- [ ] T023 [US2] Render `AutoAssignNewUsersToClient` checkbox in `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml`

**Checkpoint**: US2 is functional; toggling changes persisted client state.

---

## Phase 5: User Story 3 — Safe handling and clear outcomes (Priority: P3)

**Goal**: Auto-assignment only happens when the flow is legitimately tied to a validated client sign-in attempt; never changes existing users.

**Independent Test**: Attempt flows with missing/invalid ReturnUrl or missing/invalid client context; confirm no assignment occurs. Confirm existing users are never auto-assigned.

### Implementation

- [ ] T025 [US3] Ensure the local ReturnUrl-to-client resolution path rejects invalid/non-local ReturnUrl and returns “no client context” rather than trusting unvalidated input: `MrWhoOidc.WebAuth/Services/ReturnUrlClientContextResolver.cs`
- [ ] T026 [US3] Enforce tenant boundaries when resolving/loading the client for auto-assignment (client tenant must match current tenant): `MrWhoOidc.WebAuth/Services/ReturnUrlClientContextResolver.cs`, `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs`, `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs`
- [ ] T027 [US3] Ensure external “already linked identity” path never performs auto-assignment (`ext is not null`) in `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs`
- [ ] T028 [US3] Ensure external “email links to existing user” paths never perform auto-assignment in `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs`
- [ ] T029 [US3] Ensure external auto-provision auto-assignment only runs when `userWasCreated == true` in `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs`
- [ ] T030 [US3] Make assignment idempotent under concurrency (avoid duplicates): keep/introduce “exists then insert” checks for registration approval and external auto-provision in `MrWhoOidc.WebAuth/Services/RegistrationService.cs` and `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs`

**Checkpoint**: US3 safety conditions hold under manual tampering attempts.

---

## Phase 6: Polish & Cross-Cutting

- [ ] T031 Run the feature quickstart end-to-end and adjust any docs only if behavior differs: `specs/010-auto-assign-client/quickstart.md`
- [ ] T032 Run full build/tests after changes: `dotnet build` + `dotnet test` from repo root (MrWhoOidc.slnx)

---

## Dependencies & Execution Order

- Phase 1 → Phase 2 are prerequisites for all user stories.
- US1 depends on: T003–T006.
- US2 depends on: T003–T004 (flag persisted) and then UI wiring.
- US3 depends on: US1 implementation paths existing; can be implemented as follow-up hardening.

## Parallel Opportunities

- T010 and T011 can be done in parallel.
- T005 and T006 can be done in parallel.
- UI tasks (T020–T023) can proceed in parallel with provisioning tasks after Phase 2 completes.
