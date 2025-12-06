# Implementation Plan: Remove Client Selection from User Registration

**Branch**: `006-remove-registration-client-select` | **Date**: 2024-12-02 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/006-remove-registration-client-select/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

Remove the client selection dropdown from the user registration page to eliminate exposure of database records to unauthenticated users. The registration flow will determine tenant context from the URL path (e.g., `/t/{slug}/Registrations`) or fall back to the default tenant when no path is specified. The existing "Create new tenant" self-service option remains unchanged.

## Technical Context

**Language/Version**: C# / .NET 9  
**Primary Dependencies**: ASP.NET Core Razor Pages, Entity Framework Core, MrWhoOidc.Auth  
**Storage**: PostgreSQL via Aspire connection "authdb"  
**Testing**: MSTest with TestServer for integration tests  
**Target Platform**: Linux server / Docker containers  
**Project Type**: Web application (MrWhoOidc.WebAuth)  
**Performance Goals**: N/A (removing code, should improve slightly by eliminating client query)  
**Constraints**: Must not break existing tenant creation flow; backward compatible with existing registrations  
**Scale/Scope**: Single page modification + service update

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**Build Quality Gates**:

- [x] Zero compiler warnings (Debug and Release configurations) - Removing code, no new warnings
- [x] Zero analyzer warnings (unless documented suppressions in place) - No new code patterns
- [x] All tests pass without warnings - Will add tests for removed functionality
- [x] EF Core migrations generated using `dotnet ef migrations add` (not hand-written) - No schema changes required
- [x] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`) - No new entities
- [x] OIDC specification compliance validated with RFC references in tests - Not an OIDC protocol change

**Domain-Driven Architecture Gates**:

- [x] Domain logic stays in MrWhoOidc.Auth (no changes to Auth layer)
- [x] HTTP/UI changes in MrWhoOidc.WebAuth (registration page and service)
- [x] No HTTP concerns in Auth project

**Security Gates**:

- [x] Removes security vulnerability (exposing client list to unauthenticated users)
- [x] Tenant isolation enforced via ITenantAccessor
- [x] No new attack surface

## Project Structure

### Documentation (this feature)

```text
specs/006-remove-registration-client-select/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output (N/A - no API changes)
└── tasks.md             # Phase 2 output (/speckit.tasks command)
```

### Source Code (affected files)

```text
MrWhoOidc.WebAuth/
├── Pages/
│   └── Registrations/
│       ├── Index.cshtml        # Remove client dropdown UI
│       └── Index.cshtml.cs     # Remove ClientOptions, LoadClientsAsync
├── Services/
│   └── RegistrationService.cs  # Update to handle null ClientId properly

MrWhoOidc.UnitTests/
└── RegistrationTests.cs        # Add tests for client-less registration
```

**Structure Decision**: Modification of existing files only. No new projects, directories, or entities required. The Registration entity retains its ClientId field for backward compatibility but it will no longer be populated from the UI.

## Complexity Tracking

*No constitution violations - this is a simplification feature that removes code.*

| Aspect | Current | After Change |
|--------|---------|--------------|
| Client dropdown | Shown to unauthenticated users | Removed |
| LoadClientsAsync | Executes DB query | Removed |
| ClientId on Registration | Optional, populated from UI | Optional, always null from UI |
| Tenant resolution | Via ITenantAccessor | Unchanged |

