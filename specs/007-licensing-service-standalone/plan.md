# Implementation Plan: Standalone Licensing Service

**Branch**: `007-licensing-service-standalone` | **Date**: 2025-12-04 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/007-licensing-service-standalone/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

Transform MrWhoOidc.KeyGen into a standalone, multi-product licensing service that can issue, manage, renew, and validate license keys for any registered product. The service will persist licenses in a database with full lifecycle management (renew, revoke, upgrade/downgrade), require OIDC authentication for all operations, and support customer-first search patterns with product-specific licensable options as key-value pairs.

## Technical Context

**Language/Version**: C# / .NET 9  
**Primary Dependencies**: ASP.NET Core (Minimal APIs + Razor Pages), EF Core, JWT libraries (existing from KeyGen)  
**Storage**: SQLite for development, PostgreSQL for production (consistent with MrWhoOidc patterns)  
**Testing**: MSTest (consistent with MrWhoOidc.UnitTests)  
**Target Platform**: Linux/Windows server (containerized deployment)  
**Project Type**: Web application - Standalone service with admin UI  
**Performance Goals**: Validation endpoint <200ms p95, bulk operations 100 licenses in <10s  
**Constraints**: OIDC authentication required, 60-day license overlap for renewals  
**Scale/Scope**: Multi-product, multi-customer licensing; customer-first search pattern

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**Build Quality Gates**:

- [x] Zero compiler warnings (Debug and Release configurations)
- [x] Zero analyzer warnings (unless documented suppressions in place)
- [x] All tests pass without warnings
- [x] EF Core migrations generated using `dotnet ef migrations add` (not hand-written)
- [x] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`)
- [x] OIDC specification compliance validated with RFC references in tests

**Architecture Compliance**:

- [x] .NET 9 target framework
- [x] No OpenIddict/Microsoft Identity Platform packages
- [x] Domain logic in core project (licensing logic separate from HTTP)
- [x] PostgreSQL via Aspire connection pattern for production
- [x] MSTest for testing
- [x] Minimal APIs + Razor Pages (no MVC controllers)

**Note**: This feature creates a **standalone service** that will eventually move to its own repository. Constitution rules about MrWhoOidc.Auth/WebAuth separation don't directly apply, but the same principles (domain vs HTTP separation) will be followed in the new service structure.

## Project Structure

### Documentation (this feature)

```text
specs/007-licensing-service-standalone/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output (OpenAPI specs)
└── tasks.md             # Phase 2 output (created by /speckit.tasks)
```

### Source Code (repository root - temporary location)

```text
LicensingService/                    # Standalone subfolder (future separate repo)
├── src/
│   ├── LicensingService.Core/       # Domain logic (entities, services, persistence)
│   │   ├── Entities/                # Customer, LicensedProduct, License, etc.
│   │   ├── Services/                # LicenseService, CustomerService, ProductService
│   │   ├── Persistence/             # DbContext, migrations, repositories
│   │   └── Cryptography/            # License signing (reuse from KeyGen)
│   │
│   └── LicensingService.Web/        # HTTP layer (APIs + UI)
│       ├── Api/                     # Minimal API endpoints
│       ├── Pages/                   # Razor Pages (admin UI)
│       ├── Authentication/          # OIDC integration
│       └── Program.cs
│
└── tests/
    ├── LicensingService.Core.Tests/ # Unit tests for domain logic
    └── LicensingService.Web.Tests/  # Integration tests for APIs
```

**Structure Decision**: Standalone subfolder structure mirrors future repository layout. Domain/HTTP separation follows constitution principles. Existing MrWhoOidc.KeyGen code will be refactored/copied as base, with new entities and services added.

## Complexity Tracking

*No constitution violations requiring justification.*

| Aspect | Decision | Rationale |
|--------|----------|-----------|
| Separate subfolder | `LicensingService/` at repo root | Mirrors future standalone repo; clean extraction path |
| Two projects | Core + Web | Domain/HTTP separation per constitution principles |
| Reuse KeyGen code | Copy + refactor cryptography | Proven signing logic; avoid duplication |

---

## Phase Completion Status

### Phase 0: Research ✅ Complete
- [x] OIDC authentication pattern resolved
- [x] License signing key management defined
- [x] 60-day overlap implementation strategy documented
- [x] Customer-first search pattern designed
- [x] Product options storage approach finalized
- [x] Audit trail implementation specified
- [x] Validation endpoint design complete
- [x] SQLite/PostgreSQL switching documented

**Output**: [research.md](./research.md)

### Phase 1: Design & Contracts ✅ Complete
- [x] Data model with all entities defined
- [x] Entity relationships and constraints documented
- [x] OpenAPI specification generated
- [x] Quickstart guide created
- [x] Agent context updated

**Outputs**:
- [data-model.md](./data-model.md)
- [contracts/openapi.yaml](./contracts/openapi.yaml)
- [quickstart.md](./quickstart.md)

### Constitution Re-Check (Post-Design) ✅ Pass

All design decisions comply with constitution principles:
- Domain/HTTP separation maintained (Core vs Web projects)
- EF Core for persistence with proper migration commands
- UUIDv7 for all entity primary keys (GuidHelper.NewId())
- MSTest for testing framework
- No prohibited packages (OpenIddict, Microsoft Identity Platform)
- Minimal APIs + Razor Pages (no MVC controllers)

---

## Next Steps

Run `/speckit.tasks` to generate implementation tasks from this plan.
