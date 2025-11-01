# Implementation Plan: System-Wide URL Convention Standardization to kebab-case

**Branch**: `002-url-kebab-case-conversion` | **Date**: 2025-11-01 | **Spec**: [spec.md](spec.md)  
**Input**: Feature specification from `/specs/002-url-kebab-case-conversion/spec.md`

**Note**: This plan covers Phase 0 (Research) and Phase 1 (Design). Task breakdown is handled by `/speckit.tasks` command separately.

## Summary

Convert all URL routes in the MrWhoOidc solution from mixed PascalCase (`/Admin/Providers`, `/Auth/External/Callback`) to consistent kebab-case (`/admin/providers`, `/auth/external/callback`). This is a clean break migration with no backward compatibility - old PascalCase URLs will return 404. External IdPs and RP clients receive 30-day advance notice. All existing email confirmation tokens are invalidated.

**Scope**: 50+ URL occurrences across OIDC protocol endpoints, Razor Pages (admin UI and user-facing pages), navigation links, programmatic URL construction, and API routes.

**Approach**: Update ASP.NET Core Razor Pages routing via `@page` directives (physical file/folder structure can remain PascalCase for .NET convention compatibility), update minimal API endpoint mappings, update all navigation links in layouts, update all programmatic URL construction call sites, update test assertions, create migration documentation.

## Technical Context

**Language/Version**: C# 13 / .NET 9  
**Primary Dependencies**: ASP.NET Core Razor Pages, ASP.NET Core Minimal APIs, EF Core 9 (PostgreSQL)  
**Storage**: PostgreSQL via Aspire connection "authdb" (no schema changes needed)  
**Testing**: MSTest with TestServer for integration tests  
**Target Platform**: Linux/Windows server (Docker containers)  
**Project Type**: Web application (backend HTTP APIs + Razor Pages UI)  
**Performance Goals**: No performance impact expected (URL routing is compile-time)  
**Constraints**: 
- Clean break migration (no backward compatibility)
- 30-day advance notice for external integrations
- Must maintain OIDC specification compliance
- Tenant-aware routing must continue to work (`/t/{slug}/...` prefix)
**Scale/Scope**: 
- 15+ Razor Page folders to update
- 20+ minimal API endpoint routes to update
- 30+ navigation links in layout files
- 10+ programmatic URL construction sites
- 50+ test assertions to update

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**Build Quality Gates**:

- [x] Zero compiler warnings (Debug and Release configurations) - **NO NEW CODE**: This is a refactoring of existing routes
- [x] Zero analyzer warnings (unless documented suppressions in place) - **NO NEW CODE**: URL strings only
- [x] All tests pass without warnings - Tests will be updated to expect kebab-case URLs
- [x] EF Core migrations generated using `dotnet ef migrations add` - **N/A**: No database schema changes
- [x] Entity primary keys use `GuidHelper.NewId()` - **N/A**: No new entities
- [x] OIDC specification compliance validated with RFC references in tests - **MUST VERIFY**: Discovery document and protocol flows must remain compliant

**Domain Architecture Gates**:

- [x] Domain logic in `MrWhoOidc.Auth` (no HTTP concerns) - **N/A**: Route changes are HTTP layer only (MrWhoOidc.WebAuth)
- [x] HTTP handling in `MrWhoOidc.WebAuth` (no business logic) - **COMPLIANT**: Routes are presentation layer concern
- [x] No OpenIddict or Microsoft Identity Platform packages - **N/A**: No new dependencies

**Documentation Gates**:

- [ ] Update developer guide with new URL conventions
- [ ] Create migration documentation with before/after URL mappings
- [ ] Document 30-day notice process for external parties
- [ ] Update any existing docs referencing old URL patterns

**Security Gates**:

- [x] No security impact - URLs are presentation layer, authorization policies unchanged
- [x] Tenant isolation preserved - `TenantAwareUrlBuilder` handles kebab-case paths without modification

**GATE STATUS**: ✅ **PASS** - No constitutional violations. This is a presentation-layer refactoring compatible with all architectural principles.

## Project Structure

### Documentation (this feature)

```
specs/002-url-kebab-case-conversion/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output - ASP.NET Core routing research
├── data-model.md        # Phase 1 output - N/A for this feature (no entities)
├── quickstart.md        # Phase 1 output - Migration guide for developers
├── contracts/           # Phase 1 output - N/A for this feature (no new APIs)
├── checklists/
│   └── requirements.md  # Spec quality checklist (already complete)
└── spec.md             # Feature specification (already complete)
```

### Source Code (repository root)

```
MrWhoOidc.WebAuth/                           # HTTP layer - PRIMARY FOCUS
├── Infrastructure/
│   └── EndpointMapping/
│       ├── EndpointMappingExtensions.cs     # Update: Protocol endpoint routes
│       └── AdminApiEndpointMappingExtensions.cs  # Verify: Already kebab-case
├── Pages/                                   # Update: Razor Pages routing
│   ├── Admin/                               # Add @page directives or rename folders
│   │   ├── Providers/ (Edit.cshtml, etc.)
│   │   ├── Clients/
│   │   ├── Users/
│   │   ├── Realms/
│   │   ├── Scopes/
│   │   ├── Branding.cshtml
│   │   └── Settings.cshtml
│   ├── PlatformAdmin/
│   │   ├── Tenants/
│   │   ├── Impersonation.cshtml
│   │   └── ImpersonationHistory/
│   ├── Account/
│   │   ├── Profile.cshtml
│   │   ├── Sessions.cshtml
│   │   └── WebAuthn.cshtml
│   ├── Password/
│   ├── Registrations/
│   ├── Auth/
│   │   ├── WebAuthn.cshtml
│   │   └── Qr.cshtml
│   └── Shared/
│       ├── _Layout.cshtml                   # Update: All asp-page attributes
│       ├── _AuthLayout.cshtml               # Update: Navigation links
│       ├── _TenantContextBanner.cshtml      # Update: Admin links
│       └── _ImpersonationBanner.cshtml      # Update: Control links
├── Extensions/
│   └── TenantAwareUrlBuilder.cs             # Verify: Handles kebab-case (no changes expected)
└── Handlers/                                # Verify: Handler logic unchanged

MrWhoOidc.Auth/                              # Domain layer - NO CHANGES NEEDED
└── [All files unchanged - domain logic unaffected]

MrWhoOidc.UnitTests/                         # Tests - UPDATE ASSERTIONS
├── [Update all test files expecting specific URL patterns]
└── TestDataSeeder.cs                        # Verify: Test data setup

docs/                                        # Documentation - UPDATE REFERENCES
├── developer-guide.md                       # Update: Example URLs
├── admin-guide.md                           # Update: Screenshots and URL references
├── idp-chaining-client-configuration.md     # Update: Redirect URI examples
└── [Other docs with URL references]
```

**Structure Decision**: Web application with existing separation between domain (`MrWhoOidc.Auth`) and HTTP (`MrWhoOidc.WebAuth`) layers. This refactoring touches **only** the HTTP/presentation layer in `MrWhoOidc.WebAuth` plus test assertions in `MrWhoOidc.UnitTests`. No domain logic changes required.

## Complexity Tracking

*This feature has NO constitutional violations - table is empty.*

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| N/A | N/A | N/A |
