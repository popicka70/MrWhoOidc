# Implementation Plan: Platform QR Login at DiscoverTenant

**Branch**: `014-platform-qr-login` | **Date**: 2025-12-26 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/014-platform-qr-login/spec.md`

## Summary

Enable QR code authentication as an optional login method on the `/DiscoverTenant` page, controlled by a system-wide platform setting. This requires:

1. A new **Platform Settings** admin page for system-wide configuration
2. A **PlatformSettings** database entity to persist platform-wide toggles
3. Integration of QR login UI into the DiscoverTenant page (conditionally rendered)
4. Reuse of existing QR login infrastructure (QrLoginHandler, QrLoginService, Qr.cshtml)

## Technical Context

**Language/Version**: C# / .NET 9  
**Primary Dependencies**: ASP.NET Core Minimal APIs, Razor Pages, EF Core, HybridCache  
**Storage**: PostgreSQL via Aspire connection "authdb"  
**Testing**: MSTest with TestServer  
**Target Platform**: Linux server (Docker) / Windows dev  
**Project Type**: Web application (multi-project solution)  
**Performance Goals**: Platform settings load <50ms (cached), QR login flow same as existing  
**Constraints**: No OpenIddict, domain logic in Auth project, HTTP in WebAuth  
**Scale/Scope**: Single platform settings row, existing QR infrastructure handles scale

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**Build Quality Gates**:

- [ ] Zero compiler warnings (Debug and Release configurations)
- [ ] Zero analyzer warnings (unless documented suppressions in place)
- [ ] All tests pass without warnings
- [ ] EF Core migrations generated using `dotnet ef migrations add` (not hand-written)
- [ ] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`)
- [ ] OIDC specification compliance validated with RFC references in tests

**Domain Separation Gates**:

- [ ] Core logic in MrWhoOidc.Auth (PlatformSettings entity, IPlatformSettingsService)
- [ ] HTTP/UI in MrWhoOidc.WebAuth (Platform Settings page, DiscoverTenant updates)
- [ ] No HTTP concerns in Auth layer

**Security Gates**:

- [ ] Platform Settings page protected by `platform-admin` policy
- [ ] CSRF protection via antiforgery tokens
- [ ] No secrets logged or exposed

## Project Structure

### Documentation (this feature)

```text
specs/014-platform-qr-login/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output (API contracts)
└── tasks.md             # Phase 2 output (created by /speckit.tasks)
```

### Source Code (repository root)

```text
MrWhoOidc.Auth/
├── Persistence/
│   ├── AuthDbContext.cs            # Add DbSet<PlatformSettings>
│   ├── PlatformSettings.cs         # NEW: Entity
│   └── Migrations/                 # EF migration for PlatformSettings table
├── Services/
│   ├── IPlatformSettingsService.cs # NEW: Interface
│   └── PlatformSettingsService.cs  # NEW: Implementation with caching

MrWhoOidc.WebAuth/
├── Pages/
│   ├── DiscoverTenant.cshtml       # Add QR login button (conditional)
│   ├── DiscoverTenant.cshtml.cs    # Inject IPlatformSettingsService
│   └── PlatformAdmin/
│       ├── Settings.cshtml         # NEW: Platform settings page
│       └── Settings.cshtml.cs      # NEW: Page model
├── Shared/
│   └── _AdminLayout.cshtml         # Add Platform Settings nav link (platform-admin only)

MrWhoOidc.UnitTests/
├── PlatformSettingsServiceTests.cs # NEW: Unit tests
└── DiscoverTenantQrLoginTests.cs   # NEW: Integration tests
```

**Structure Decision**: Follows existing project layout. New entity in Auth/Persistence, service in Auth/Services, UI in WebAuth/Pages/PlatformAdmin. Reuses existing QR login infrastructure.

## Complexity Tracking

*No constitution violations anticipated - straightforward feature using established patterns.*

| Aspect | Approach | Rationale |
|--------|----------|-----------|
| Platform settings storage | Single-row DB table | Simpler than appsettings.json for runtime changes, cacheable |
| QR login on DiscoverTenant | Reuse existing Qr.cshtml flow | No duplication of QR infrastructure |
| Admin access | `platform-admin` policy | Consistent with PlatformAdmin/* pages |
