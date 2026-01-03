# Implementation Plan: Pairwise Subject Identifiers

**Branch**: `016-pairwise-subject-ids` | **Date**: 2026-01-03 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/016-pairwise-subject-ids/spec.md`

## Summary

Implement OpenID Connect pairwise subject identifiers so that end-users receive a different `sub` claim per client (or per sector identifier), preventing cross-application correlation. The design adds tenant-scoped persistence for pairwise mappings and integrates subject selection into existing token issuance and UserInfo flows. The provider metadata is updated to advertise support for both `public` and `pairwise` subject identifier types.

Primary research source: `docs/future-plans/pairwise-subject-identifiers.md`

## Technical Context

**Language/Version**: .NET 10, C#  
**Primary Dependencies**: ASP.NET Core Minimal APIs + Razor Pages, EF Core, System.IdentityModel.Tokens.Jwt  
**Storage**: PostgreSQL via Aspire connection "authdb"  
**Testing**: MSTest with TestServer-style integration tests  
**Target Platform**: Linux containers (Docker), Windows development  
**Project Type**: Multi-project solution (MrWhoOidc.Auth, MrWhoOidc.WebAuth, MrWhoOidc.UnitTests)  
**Performance Goals**: No material latency increase for token issuance; pairwise lookup should be a single indexed read in steady state  
**Constraints**: OIDC specification compliance; strict tenant isolation; do not add OpenIddict/Microsoft Identity Platform packages; zero warnings  
**Scale/Scope**: Multi-tenant SaaS; feature impacts token issuance, UserInfo, discovery, and admin client configuration

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**OIDC Specification Compliance**:

- [x] Pairwise subject identifiers are an OIDC-defined feature; implementation aligns with OIDC Core subject identifier requirements
- [x] Provider metadata will advertise both supported subject types

**Domain-Driven Architecture**:

- [x] Subject identifier computation and persistence belong in `MrWhoOidc.Auth`
- [x] Discovery metadata and HTTP endpoint behavior belong in `MrWhoOidc.WebAuth`

**Technology Stack & Constraints**:

- [x] No OpenIddict or Microsoft Identity Platform packages will be added
- [x] PostgreSQL is used via Aspire-provided connection "authdb"
- [x] EF Core migrations will be generated via tooling (not hand-written)

**Build Quality Gates**:

- [ ] Zero compiler warnings (Debug and Release configurations)
- [ ] Zero analyzer warnings (unless documented suppressions in place)
- [ ] All tests pass without warnings
- [x] EF Core migrations generated using `dotnet ef migrations add` (not hand-written)
- [x] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`)
- [ ] OIDC specification compliance validated with RFC references in tests

## Project Structure

### Documentation (this feature)

```text
specs/016-pairwise-subject-ids/
├── plan.md              # This file
├── spec.md              # Feature requirements + clarifications
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── service-contracts.md
│   └── admin-api.md
└── tasks.md             # Phase 2 output
```

### Source Code (existing repository structure)

```text
MrWhoOidc.Auth/
├── Persistence/
│   ├── AuthDbContext.cs                # Client + new pairwise mapping entity (MODIFY)
│   └── Migrations/                     # New migration (ADD)
├── Protocols/
│   └── OidcConstants.cs                # SubjectTypes already present (NO CHANGE)
└── Services/
    ├── SubjectIdentifiers/             # New services for sector + pairwise mapping (ADD)
    └── Token/                          # Token issuance services that attach `sub` (MODIFY)

MrWhoOidc.WebAuth/
├── Handlers/
│   ├── DiscoveryHandler.cs             # Advertise subject_types_supported (MODIFY)
│   └── UserInfoHandler.cs              # Ensure `sub` behavior is consistent (MODIFY/VERIFY)
├── Pages/
│   └── Admin/Clients/                  # Client config UI (MODIFY)
└── Services/
    ├── ConfigurationExportService.cs   # Include new client fields (MODIFY)
    └── ConfigurationImportService.cs   # Include new client fields (MODIFY)

MrWhoOidc.UnitTests/
├── Services/SubjectIdentifiers/         # Unit tests for sector + pairwise mapping (ADD)
└── Integration/                         # Integration tests for token/userinfo/discovery (ADD/EXTEND)
```

**Structure Decision**: Extend existing multi-project architecture per constitution: domain logic in `MrWhoOidc.Auth`, HTTP/discovery/admin UI in `MrWhoOidc.WebAuth`, tests in `MrWhoOidc.UnitTests`.

## Complexity Tracking

*Filled only for constitution/documentation mismatches that require justification.*

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Constitution states ".NET 9 only" but repo targets .NET 10 | Repository is already on .NET 10 (`<TargetFramework>net10.0</TargetFramework>` in projects). This feature follows existing repo baseline. | Downgrading the entire solution to .NET 9 is out of scope and would risk unrelated regressions. |

## Constitution Check (Post-Design)

**OIDC Specification Compliance**: ✅ PASS

- Pairwise identifier behavior is implemented as a standards-aligned subject selection policy
- Metadata advertises both subject identifier types

**Domain-Driven Architecture**: ✅ PASS

- Pairwise mapping and sector resolution live in `MrWhoOidc.Auth`
- HTTP endpoints and discovery remain in `MrWhoOidc.WebAuth`

**Technology Stack & Constraints**: ✅ PASS

- No prohibited packages are required
- PostgreSQL and EF Core migrations remain the persistence mechanism

**Zero-Warning Policy**: ⏳ PENDING (implementation phase)

- Build/test gates remain unchecked until code is written

## Phase Summary

### Phase 0: Research ✅ Complete

**Output**: `research.md`

Resolved decisions:

- Persisted pairwise mapping per (tenant, user, sector)
- Sector identifier resolution + validation approach for configured sector identifier references
- Pairwise `sub` generation: CSPRNG random bytes encoded as base64url (no padding), persisted per mapping
- Runtime behavior: if `sector_identifier_uri` is configured but unreachable/invalid at issuance time, issuance fails (no fallback)

### Phase 1: Design ✅ Complete

**Outputs**:

- `data-model.md`
- `contracts/service-contracts.md`
- `contracts/admin-api.md`
- `quickstart.md`

### Phase 2: Task Breakdown ✅ Complete

**Output**: `tasks.md`

## Next Steps

Proceed to implementation using `tasks.md` in priority order (US1 → US2 → US3), maintaining zero-warning policy and adding required unit/integration tests.
