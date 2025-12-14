# Implementation Plan: Auto-Assign New Users To Client

**Branch**: `010-auto-assign-client` | **Date**: 2025-12-14 | **Spec**: [specs/010-auto-assign-client/spec.md](spec.md)
**Input**: Feature specification from `/specs/010-auto-assign-client/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

Add a per-client setting to auto-assign newly created users (local registration and first-time external IdP sign-in) to the client that initiated the sign-in journey. Ensure the assignment is derived from the validated authorization/client context (not arbitrary user input) and is tenant-safe.

## Technical Context

<!--
  ACTION REQUIRED: Replace the content in this section with the technical details
  for the project. The structure here is presented in advisory capacity to guide
  the iteration process.
-->

**Language/Version**: C# on .NET 9  
**Primary Dependencies**: ASP.NET Core (minimal APIs + Razor Pages), EF Core, PostgreSQL provider  
**Storage**: PostgreSQL (via Aspire-provided connection name `authdb`)  
**Testing**: MSTest (unit + integration tests)  
**Target Platform**: Server-hosted ASP.NET Core (local dev via Aspire; production containerized)  
**Project Type**: Web application (MrWhoOidc.WebAuth) + domain/persistence (MrWhoOidc.Auth)  
**Performance Goals**: No meaningful regression in sign-in latency for first-time users (auto-assignment should add only a minimal DB write in the happy path)  
**Constraints**: OIDC spec compliance, strict tenant isolation, zero-warning policy  
**Scale/Scope**: Small additive feature affecting first-time user provisioning and client admin UI

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**Constitution alignment (required):**

- OIDC/OAuth behavior must remain spec-compliant (no protocol shortcuts)
- Domain logic belongs in MrWhoOidc.Auth; HTTP surface/UI changes belong in MrWhoOidc.WebAuth
- .NET 9 everywhere; PostgreSQL via Aspire `authdb` (no hard-coded connection strings)
- No OpenIddict / Microsoft Identity Platform dependencies
- Tenant isolation must be enforced at the data/service layer

**Build Quality Gates**:

- [ ] Zero compiler warnings (Debug and Release configurations)
- [ ] Zero analyzer warnings (unless documented suppressions in place)
- [ ] All tests pass without warnings
- [ ] EF Core migrations generated using `dotnet ef migrations add` (not hand-written)
- [ ] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`)
- [ ] OIDC specification compliance validated with RFC references in tests (where this feature affects protocol outcomes)

**Post-design re-check (after Phase 1 artifacts)**:

- No new projects introduced; changes stay within existing Auth/WebAuth boundaries.
- No new identity packages are required.
- Data changes are limited to a single additive per-client setting (migration required) and reusing existing assignment entities.

## Project Structure

### Documentation (this feature)

```
specs/010-auto-assign-client/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)
```
MrWhoOidc.Auth/
  Persistence/
    AuthDbContext.cs                    # Client entity + EF configuration
    Migrations/                         # New migration for the per-client setting

MrWhoOidc.WebAuth/
  Handlers/
    AuthorizeHandler.cs                 # Source of validated client context (login journey)
    ExternalOidcHandler.cs              # External start/callback passes client context
    External/
      ExternalOidcUserProvisioner.cs    # External auto-provisioning path (new user creation)
  Pages/
    Login.cshtml                        # Registration link should preserve ReturnUrl
    Registrations/Index.cshtml(.cs)     # Local registration (may need to accept ReturnUrl)
    Admin/Clients/Add.cshtml(.cs)       # Add client UI
    Admin/Clients/Edit.cshtml(.cs)      # Edit client UI
  Services/
    RegistrationService.cs              # Registration approval assigns client when clientId is supplied

MrWhoOidc.UnitTests/
  (new/updated tests covering assignment behavior)
```

**Structure Decision**: Update existing domain entity (Client) and existing provisioning flows; no new projects.

## Complexity Tracking

*Fill ONLY if Constitution Check has violations that must be justified*

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |
