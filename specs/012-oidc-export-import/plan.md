# Implementation Plan: OIDC Configuration Export/Import

**Branch**: `012-oidc-export-import` | **Date**: 2024-12-23 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/012-oidc-export-import/spec.md`

## Summary

Extend MrWhoOidc to support configuration export/import for tenants, realms, clients, and identity providers. Building on the existing `SeedManifest` schema, this feature adds:
1. Export service to serialize configurations to JSON with obfuscated or hashed secrets
2. Extended manifest schema supporting identity providers, claim mappings, and export metadata
3. Enhanced import service with transaction support, conflict resolution, and audit logging
4. Admin UI for export/import operations with preview and progress tracking

## Technical Context

**Language/Version**: C# / .NET 9  
**Primary Dependencies**: ASP.NET Core (Minimal APIs + Razor Pages), EF Core, System.Text.Json  
**Storage**: PostgreSQL via Aspire connection "authdb"  
**Testing**: MSTest with TestServer for integration tests  
**Target Platform**: Linux containers (production), Windows (development)  
**Project Type**: Web application (existing MrWhoOidc solution)  
**Performance Goals**: Export ≤30s for 100 clients, Import preview ≤10s, Import execution ≤2 minutes  
**Constraints**: No plaintext secrets in exports, transactional imports with rollback, audit logging required  
**Scale/Scope**: Multi-tenant OIDC provider supporting 1000+ clients per tenant

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**OIDC Specification Compliance**:
- [ ] Export/import does not affect protocol compliance (configuration management only)
- [ ] No protocol-level changes required

**Domain-Driven Architecture**:
- [ ] Export/import domain logic placed in MrWhoOidc.Auth (services, DTOs)
- [ ] HTTP endpoints and UI in MrWhoOidc.WebAuth (Razor Pages, minimal APIs)
- [ ] Clear boundary maintained between Auth (what/why) and WebAuth (how/when)

**Build Quality Gates**:
- [ ] Zero compiler warnings (Debug and Release configurations)
- [ ] Zero analyzer warnings (unless documented suppressions in place)
- [ ] All tests pass without warnings
- [ ] EF Core migrations generated using `dotnet ef migrations add` (not hand-written)
- [ ] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`)

**Security & Multi-Tenancy**:
- [ ] Tenant isolation enforced (exports scoped to authorized tenant)
- [ ] RBAC: platform-admin for tenant export, tenant-admin for realm/client/IdP export
- [ ] Audit logging for all export/import operations
- [ ] No plaintext secrets in exports (hashed or obfuscated only)

## Project Structure

### Documentation (this feature)

```
specs/012-oidc-export-import/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output (API contracts)
└── tasks.md             # Phase 2 output (not created by /speckit.plan)
```

### Source Code (repository root)

```
MrWhoOidc.Auth/
├── Seeding/
│   ├── SeedManifest.cs              # EXTEND: Add IdP definitions, export metadata
│   ├── ExportManifest.cs            # NEW: Root export container with metadata
│   ├── ExportOptions.cs             # NEW: Obfuscation settings
│   └── IdentityProviderSeedDef.cs   # NEW: IdP seed definition
├── Services/
│   ├── IConfigurationExportService.cs   # NEW: Export service interface
│   ├── ConfigurationExportService.cs    # NEW: Export implementation
│   ├── IConfigurationImportService.cs   # NEW: Import service interface
│   └── ConfigurationImportService.cs    # NEW: Import implementation (enhanced)
└── Persistence/
    └── Migrations/                  # If new audit entity needed

MrWhoOidc.WebAuth/
├── Pages/
│   ├── PlatformAdmin/
│   │   └── Tenants/
│   │       ├── Export.cshtml(.cs)   # NEW: Tenant export page
│   │       └── Import.cshtml(.cs)   # NEW: Tenant import page
│   └── Admin/
│       ├── Realms/
│       │   └── Export.cshtml(.cs)   # NEW: Realm export
│       ├── Clients/
│       │   └── Export.cshtml(.cs)   # NEW: Client export
│       └── Providers/
│           └── Export.cshtml(.cs)   # NEW: IdP export
├── Handlers/
│   └── ExportImportHandler.cs       # NEW: Minimal API endpoints for export/import
└── Seeding/
    └── SeedManifestApplier.cs       # EXTEND: Transaction support, conflict resolution

MrWhoOidc.UnitTests/
├── Export/
│   ├── ConfigurationExportServiceTests.cs   # NEW
│   └── ExportManifestSerializationTests.cs  # NEW
└── Import/
    ├── ConfigurationImportServiceTests.cs   # NEW
    └── ConflictResolutionTests.cs           # NEW
```

**Structure Decision**: Extends existing MrWhoOidc solution architecture. Export/import domain logic in Auth project, HTTP surface in WebAuth project. Follows established patterns from existing SeedManifest implementation.

## Complexity Tracking

*No constitution violations requiring justification.*
