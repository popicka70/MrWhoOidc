# Implementation Plan: External IdP Registration

**Branch**: `013-external-idp-registration` | **Date**: 2025-12-25 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/013-external-idp-registration/spec.md`

## Summary

Add external identity provider (IdP) registration options to the user registration page. Users visiting the registration page will see buttons for enabled external IdPs alongside the traditional manual form. Clicking an IdP button initiates external authentication, and upon success, a new user account is created using claims from the IdP. Administrators can control which IdPs appear on the registration page via a new `AllowRegistration` property on the IdentityProvider entity.

**Technical approach**: Extend the existing `IdentityProvider` entity with `AllowRegistration` boolean. Modify the registration page to query for registration-enabled IdPs and render buttons that redirect to the existing external authentication flow with a registration-specific callback. Leverage existing `RegistrationService` (with `isExternalIdp=true`) and `ExternalOidcUserProvisioner` infrastructure.

## Technical Context

**Language/Version**: C# / .NET 9  
**Primary Dependencies**: ASP.NET Core (Minimal APIs + Razor Pages), EF Core 9, PostgreSQL  
**Storage**: PostgreSQL via Aspire connection "authdb"  
**Testing**: MSTest with TestServer for integration tests  
**Target Platform**: Linux containers (Docker), cross-platform development  
**Project Type**: Web application (multi-project: MrWhoOidc.Auth domain + MrWhoOidc.WebAuth HTTP surface)  
**Performance Goals**: Registration page loads IdP options in <2 seconds; registration completion <30 seconds (excluding external IdP time)  
**Constraints**: Must maintain tenant isolation; graceful degradation when no IdPs configured  
**Scale/Scope**: Multi-tenant SaaS; default tenant registration page  

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**OIDC Specification Compliance**: ✅ This feature does not modify OIDC protocol flows—it extends the registration UI to initiate existing external authentication flows.

**Domain-Driven Architecture**: ✅ Entity changes (`AllowRegistration` property) go in `MrWhoOidc.Auth/Persistence`. UI/page changes go in `MrWhoOidc.WebAuth/Pages/Registrations`.

**Build Quality Gates**:

- [x] Zero compiler warnings (Debug and Release configurations)
- [x] Zero analyzer warnings (unless documented suppressions in place)
- [x] All tests pass without warnings
- [x] EF Core migrations generated using `dotnet ef migrations add` (not hand-written)
- [x] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`)
- [ ] OIDC specification compliance validated with RFC references in tests (N/A—not a protocol change)

**No Constitution Violations**: This feature follows established patterns:

- Entity changes in Auth project
- UI changes in WebAuth project
- Uses existing external OIDC flow infrastructure
- No new projects required

## Project Structure

### Documentation (this feature)

```text
specs/013-external-idp-registration/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output (API contracts)
└── tasks.md             # Phase 2 output (created by /speckit.tasks)
```

### Source Code (changes to existing structure)

```text
MrWhoOidc.Auth/
├── Persistence/
│   ├── AuthDbContext.cs          # Add AllowRegistration to IdentityProvider entity
│   └── Migrations/               # New migration for AllowRegistration column

MrWhoOidc.WebAuth/
├── Pages/
│   ├── Registrations/
│   │   ├── Index.cshtml          # Add IdP buttons section
│   │   └── Index.cshtml.cs       # Add IdP loading logic
│   └── Admin/
│       └── Providers/
│           ├── Edit.cshtml       # Add AllowRegistration toggle
│           └── Edit.cshtml.cs    # Handle AllowRegistration field
├── Handlers/
│   └── External/
│       └── ExternalOidcUserProvisioner.cs  # Handle registration flow variant
└── Services/
    └── RegistrationService.cs    # Already supports isExternalIdp (no changes needed)

MrWhoOidc.UnitTests/
└── ExternalIdpRegistrationTests.cs  # New test file
```

**Structure Decision**: Follows existing MrWhoOidc architecture—entity changes in Auth/Persistence, UI in WebAuth/Pages, leveraging existing external OIDC infrastructure.

## Complexity Tracking

*No constitution violations—standard feature addition following established patterns.*
