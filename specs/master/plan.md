# Implementation Plan: License Key System

**Branch**: `license-key-system` | **Date**: October 19, 2025 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/master/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

Implement a comprehensive license key system for MrWhoOidc that enables tiered functionality (Community, Professional, Enterprise, Enterprise+) based on purchased licenses. The system will provide secure cryptographic license validation, feature gating, user/tenant limits enforcement, and admin UI for license management. Architecture follows existing MrWhoOidc patterns with domain logic in MrWhoOidc.Auth and UI components in MrWhoOidc.WebAuth.

## Technical Context

**Language/Version**: C# / .NET 9  
**Primary Dependencies**: EF Core, ASP.NET Core Minimal APIs, System.IdentityModel.Tokens.Jwt, System.Security.Cryptography  
**Storage**: PostgreSQL via Aspire connection "authdb" with EF Core migrations  
**Testing**: MSTest with TestServer for integration tests  
**Target Platform**: Linux/Windows server containers via Docker  
**Project Type**: Enterprise web application - integrates with existing MrWhoOidc architecture  
**Performance Goals**: License validation <100ms on startup, cached feature checks <1ms runtime  
**Constraints**: Offline license validation, no external license server dependency, tamper-resistant  
**Scale/Scope**: Support up to unlimited users/tenants per Enterprise license, 4 license tiers

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**Build Quality Gates**:

- [ ] Zero compiler warnings (Debug and Release configurations)
- [ ] Zero analyzer warnings (unless documented suppressions in place)
- [ ] All tests pass without warnings
- [ ] EF Core migrations generated using `dotnet ef migrations add` (not hand-written)
- [ ] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`)
- [ ] OIDC specification compliance validated with RFC references in tests

**Architecture Compliance**:

- [ ] Domain logic placed in MrWhoOidc.Auth (license validation, feature gating)
- [ ] HTTP/UI components placed in MrWhoOidc.WebAuth (admin interface, APIs)
- [ ] Integration with existing AuthDbContext and tenant isolation
- [ ] No OpenIddict or Microsoft Identity Platform dependencies

**Security Requirements**:

- [ ] Cryptographic license signing/validation implemented securely
- [ ] License keys are tamper-resistant and properly validated
- [ ] No sensitive license information exposed client-side
- [ ] Feature gating cannot be bypassed through manipulation

**Multi-Tenancy Compliance**:

- [ ] License limits respect tenant boundaries
- [ ] Tenant-level feature restrictions properly enforced
- [ ] Integration with existing ITenantAccessor pattern

## Project Structure

### Documentation (this feature)

```text
specs/master/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code Integration (existing MrWhoOidc structure)

```text
MrWhoOidc.Auth/
├── Licensing/              # NEW: License domain logic
│   ├── Models/            # License, LicenseTier, FeatureGate entities
│   ├── Services/          # ILicenseValidator, IFeatureService
│   └── Validators/        # License key validation, signature verification
├── Persistence/
│   ├── Migrations/        # NEW: License-related DB migrations
│   └── AuthDbContext.cs   # MODIFIED: Add license entities
└── Services/              # MODIFIED: Existing services with license checks

MrWhoOidc.WebAuth/
├── Pages/Admin/License/   # NEW: License management UI
├── Admin/Api/             # MODIFIED: License management endpoints
└── Infrastructure/        # MODIFIED: License validation middleware

MrWhoOidc.UnitTests/
├── Licensing/            # NEW: License system tests
├── Integration/          # MODIFIED: License integration tests
└── TestData/             # MODIFIED: License test data setup
```

**Structure Decision**: [Document the selected structure and reference the real
directories captured above]

## Complexity Tracking

*Fill ONLY if Constitution Check has violations that must be justified*

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |
