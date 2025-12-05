# Implementation Plan: Global User Credentials

**Branch**: `008-global-user-credentials` | **Date**: 2025-12-05 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/008-global-user-credentials/spec.md`

## Summary

Transition from per-tenant password storage to a single global credential per user. Currently, the `User` entity stores `PasswordHash`, `TotpSecret`, and `TotpEnabled` per tenant, meaning a user with access to multiple tenants has separate passwords for each. The target architecture centralizes credentials in the existing `UserAccount` entity (already in schema) and authenticates against it globally, with `UserTenantMembership` providing tenant-specific access without duplicating credentials.

## Technical Context

**Language/Version**: .NET 9, C# 13  
**Primary Dependencies**: EF Core 9, ASP.NET Core Minimal APIs, Razor Pages, HybridCache  
**Storage**: PostgreSQL via Aspire connection "authdb"  
**Testing**: MSTest with TestServer for integration tests  
**Target Platform**: Linux containers (Docker), Windows development  
**Project Type**: Multi-project solution (MrWhoOidc.Auth, MrWhoOidc.WebAuth)  
**Performance Goals**: Password verification under 200ms, zero-downtime migration  
**Constraints**: Backward compatibility during migration; existing sessions must remain valid  
**Scale/Scope**: Multi-tenant SaaS with potentially thousands of users per tenant

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**OIDC Specification Compliance**:

- [x] No OIDC protocol changes required (credential storage is internal implementation)
- [x] Token issuance unchanged (`sub` claim continues to use User/UserAccount ID)

**Domain-Driven Architecture**:

- [x] Credential/authentication logic belongs in `MrWhoOidc.Auth` (domain layer)
- [x] Login pages/handlers in `MrWhoOidc.WebAuth` (HTTP layer)
- [x] Clear separation maintained

**.NET 9 Technology Stack**:

- [x] Using .NET 9 across all projects
- [x] PostgreSQL with EF Core migrations
- [x] No OpenIddict or Microsoft Identity Platform packages

**Integration Test Coverage**:

- [x] Tests exist for password verification in `UserServiceTests.cs`
- [x] Multi-tenant isolation tests in `DataIsolationTests.cs`
- [x] Will add new tests for global credential authentication

**Security & Multi-Tenancy**:

- [x] Argon2id password hashing (already used)
- [x] Lockout mechanism exists (needs globalization)
- [x] Tenant isolation maintained via `UserTenantMembership`

**Build Quality Gates**:

- [ ] Zero compiler warnings (Debug and Release configurations)
- [ ] Zero analyzer warnings (unless documented suppressions)
- [ ] All tests pass without warnings
- [x] EF Core migrations generated using `dotnet ef migrations add` (not hand-written)
- [x] Entity primary keys use `GuidHelper.NewId()` (UserAccount already uses this)
- [x] No OIDC specification changes required

## Project Structure

### Documentation (this feature)

```text
specs/008-global-user-credentials/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output (internal service contracts)
└── tasks.md             # Phase 2 output (created by /speckit.tasks)
```

### Source Code (existing repository structure)

```text
MrWhoOidc.Auth/
├── Persistence/
│   ├── AuthDbContext.cs          # UserAccount, User entities (MODIFY)
│   └── Migrations/               # New migration for credential removal from User
├── Services/
│   ├── UserService.cs            # Current per-tenant auth (MODIFY)
│   ├── UserAccountService.cs     # Global account service (EXTEND)
│   ├── UserAccountProvisioner.cs # Dual-write logic (EXISTS)
│   └── GlobalAuthenticationService.cs  # NEW: Global credential auth
├── MultiTenancy/
│   └── ITenantAccessor.cs        # Tenant context (NO CHANGE)
└── Options/
    └── UserAccountFeatureOptions.cs  # Feature flags (EXISTS)

MrWhoOidc.WebAuth/
├── Pages/
│   ├── Login.cshtml.cs           # Login handler (MODIFY)
│   ├── Profile/                  # Password change (MODIFY)
│   └── Admin/Users/              # Admin password reset (MODIFY)
├── Services/
│   └── TenantSwitchingService.cs # Post-login tenant selection (NO CHANGE)
└── Middleware/
    └── TenantResolutionMiddleware.cs  # Tenant context (MINOR MODIFY)

MrWhoOidc.UnitTests/
├── GlobalAuthenticationTests.cs   # NEW: Test global credential auth
├── CredentialMigrationTests.cs    # NEW: Test migration logic
└── MultiTenancy/
    └── DataIsolationTests.cs      # EXTEND: Add global auth tests
```

**Structure Decision**: Uses existing multi-project structure per constitution. No new projects needed—extends `MrWhoOidc.Auth` services and modifies `MrWhoOidc.WebAuth` handlers.

## Complexity Tracking

*No constitution violations requiring justification.*

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| N/A | N/A | N/A |

## Constitution Check (Post-Design)

*Re-evaluated after Phase 1 design completion.*

**OIDC Specification Compliance**: ✅ PASS

- No protocol changes required
- Token claims unchanged (`sub` = UserAccount.Id = User.Id)
- Discovery/JWKS endpoints unaffected

**Domain-Driven Architecture**: ✅ PASS

- `IGlobalAuthenticationService` placed in `MrWhoOidc.Auth` (domain)
- Login handler modifications in `MrWhoOidc.WebAuth` (HTTP layer)
- Clean separation between credential logic and HTTP handling

**.NET 9 Technology Stack**: ✅ PASS

- All changes target .NET 9
- PostgreSQL via EF Core migrations
- No prohibited packages added

**Integration Test Coverage**: ✅ PASS (planned)

- New test files specified: `GlobalAuthenticationTests.cs`, `CredentialMigrationTests.cs`
- Extension of existing `DataIsolationTests.cs`
- Cross-tenant authentication tests defined in quickstart

**Security & Multi-Tenancy**: ✅ PASS

- Global lockout prevents distributed attacks across tenants
- Argon2id hashing preserved
- Tenant isolation maintained through membership model
- Audit logging for credential changes

**Zero-Warning Policy**: ⏳ PENDING (implementation phase)

- Will verify during implementation
- Build quality gates remain unchecked until code is written

## Phase Summary

### Phase 0: Research ✅ Complete

**Output**: `research.md`

- Assessed existing infrastructure (UserAccount, UserAccountProvisioner, feature flags)
- Analyzed authentication flow changes needed
- No need to migrate passwords. We'll create a new database.
- Resolved lockout globalization approach
- Confirmed token claim compatibility

### Phase 1: Design ✅ Complete

**Outputs**:

- `data-model.md` - Entity definitions, new fields, indexes
- `contracts/service-contracts.md` - IGlobalAuthenticationService, result types
- `quickstart.md` - Implementation guide, test examples, troubleshooting

**Agent Context Updated**: ✅

- `.github/copilot-instructions.md` updated with feature technologies

## Next Steps

This plan is ready for **Phase 2: Task Breakdown** via `/speckit.tasks`.

The task breakdown will create:

- `tasks.md` with prioritized implementation tasks
- Task dependencies and estimated effort
- Test-driven development checkpoints
