# Implementation Plan: Identity Provider Configuration Form

**Branch**: `009-provider-form-ui` | **Date**: 2025-12-14 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/009-provider-form-ui/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

Replace the admin “Add/Edit Identity Provider” experience for OIDC providers so admins configure standard parameters through first-class form inputs with validations, while retaining an optional advanced JSON area for extended parameters. Ensure edit flows do not lose existing extended/unknown configuration and that secret updates are explicit and safe.

## Technical Context

<!--
  ACTION REQUIRED: Replace the content in this section with the technical details
  for the project. The structure here is presented in advisory capacity to guide
  the iteration process.
-->

**Language/Version**: C# / .NET 9  
**Primary Dependencies**: ASP.NET Core (Minimal APIs + Razor Pages), EF Core, System.Text.Json  
**Storage**: PostgreSQL via EF Core (Aspire-provided connection name `authdb`)  
**Testing**: MSTest (unit + integration tests)  
**Target Platform**: Server-side web application (Windows/Linux hosting)  
**Project Type**: Web application (single repo; admin UI in Razor Pages)  
**Performance Goals**: Admin form interactions remain responsive; validation feedback appears immediately on save (page post).  
**Constraints**: No OpenIddict / Microsoft Identity Platform dependencies; preserve multi-tenant isolation and admin authorization policies.  
**Scale/Scope**: Admin-only feature; impacts provider add/edit pages and configuration serialization/validation.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

[Gates determined based on `.specify/memory/constitution.md`]

**Build Quality Gates**:

- [x] Zero compiler warnings (Debug and Release configurations)
- [x] Zero analyzer warnings (unless documented suppressions in place)
- [x] All tests pass without warnings
- [x] EF Core migrations generated using `dotnet ef migrations add` (not hand-written)
- [x] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`)
- [x] OIDC specification compliance validated with RFC references in tests

## Project Structure

### Documentation (this feature)

```
specs/[###-feature]/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)
<!--
  ACTION REQUIRED: Replace the placeholder tree below with the concrete layout
  for this feature. Delete unused options and expand the chosen structure with
  real paths (e.g., apps/admin, packages/something). The delivered plan must
  not include Option labels.
-->

```
MrWhoOidc.Auth/                 # Domain + persistence (provider config schema, validation)
MrWhoOidc.WebAuth/              # HTTP surface + admin Razor Pages
  Pages/Admin/Providers/         # Add/Edit UI for identity providers
  Infrastructure/EndpointMapping/ # Admin API endpoints
MrWhoOidc.UnitTests/            # MSTest unit/integration tests
```

**Structure Decision**: Web application within a multi-project .NET solution; implement UI and HTTP concerns in MrWhoOidc.WebAuth and keep reusable configuration parsing/validation in MrWhoOidc.Auth where appropriate.

## Complexity Tracking

*Fill ONLY if Constitution Check has violations that must be justified*

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |

## Phase 0: Outline & Research

**Goal**: Resolve design decisions for form ↔ config mapping, validation, and data-preservation.

**Research outputs**: [research.md](research.md)

Key decisions to lock:

1. Preserve unknown/extended configuration keys across edit/save while still allowing standard fields to be edited safely.
2. Secret edit behavior (safe defaults, no secret reveal, explicit update).
3. Standard vs advanced conflict rule and user feedback.

## Phase 1: Design & Contracts

**Data model**: [data-model.md](data-model.md)

**Contracts**: [contracts/openapi.yaml](contracts/openapi.yaml)

**Quickstart**: [quickstart.md](quickstart.md)

Design notes (high level):

- Reuse the existing OIDC config schema for the standard form.
- Ensure that submitting standard fields does not delete unrelated configuration.
- Keep advanced JSON for extended parameters only and validate it on save.

## Constitution Check (Post-Design)

No design changes introduce constitution violations. Implementation must keep domain logic in MrWhoOidc.Auth and UI concerns in MrWhoOidc.WebAuth, with all builds/tests remaining warning-free.

## Phase 2: Implementation Plan (Outline)

1. Align Add and Edit pages on a shared OIDC form model (standard inputs) with field-level validations.
2. Implement merge-on-save so standard inputs overwrite only known keys while preserving unknown/extended config keys.
3. Implement safe secret update semantics (blank = unchanged; no secret display).
4. Keep optional advanced configuration for extended parameters, validate it, and implement deterministic conflict handling.
5. Add/adjust tests in MrWhoOidc.UnitTests to cover:
  - Required fields and URL validations.
  - Unknown key preservation across edit/save.
  - Secret not blanked when omitted.
6. Verify `dotnet build` and `dotnet test` are warning-free.
