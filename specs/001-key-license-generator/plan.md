# Implementation Plan: Key and License Management Service

**Branch**: `001-key-license-generator` | **Date**: October 28, 2025 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/001-key-license-generator/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

A standalone web service for generating cryptographic key pairs (RSA/ECDSA) for OIDC client JAR/JARM usage and license tokens. This addresses a critical security flaw where the authorization server currently generates private keys it should never possess. The service will run as a Docker container with a Razor Pages web UI, allowing administrators to generate keys, download them securely, and manage their lifecycle. Additionally, it replaces the existing command-line license generator with a user-friendly web interface.

## Technical Context

**Language/Version**: C# / .NET 9  
**Primary Dependencies**: ASP.NET Core 9.0, System.Security.Cryptography (RSA/ECDSA), System.IdentityModel.Tokens.Jwt, Microsoft.IdentityModel.Tokens  
**Storage**: SQLite for key metadata persistence (lightweight, file-based, no server dependency)  
**Testing**: MSTest with TestServer for integration tests  
**Target Platform**: Docker container (Linux/Windows), standalone web service  
**Project Type**: Web application (Razor Pages)  
**Performance Goals**: <5 seconds for key generation, <10 seconds for end-to-end generate-and-download flow  
**Constraints**: <200MB container image size, <50MB memory footprint at idle, <100MB under load  
**Scale/Scope**: Low-traffic admin tool (~10-50 key generations per day, <10 concurrent users)

## Constitution Check

**GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.**

### Compliance Assessment

**Build Quality Gates**:

- [x] Zero compiler warnings (Debug and Release configurations) - New project, will enforce from start
- [x] Zero analyzer warnings (unless documented suppressions in place) - New project, will enforce from start
- [x] All tests pass without warnings - Will write tests with zero-warning policy
- [x] EF Core migrations generated using `dotnet ef migrations add` - Using SQLite with EF Core migrations
- [x] Entity primary keys use `GuidHelper.NewId()` - Will adopt UUIDv7 for key metadata entities
- [x] OIDC specification compliance validated with RFC references in tests - N/A (not implementing OIDC protocols, generating keys for OIDC clients)

**Architecture Compliance**:

- [x] No OpenIddict/Microsoft Identity Platform packages - Standalone service, no identity platform dependencies
- [x] Domain logic separation - Will separate domain logic (key generation, license generation) from HTTP/UI layer
- [x] PostgreSQL via Aspire - **VARIANCE**: Using SQLite instead (rationale below)
- [x] .NET 9 target framework - Yes
- [x] Minimal APIs + Razor Pages (no MVC controllers) - Using Razor Pages for UI

**Security Compliance**:

- [x] Secure credential storage - No stored credentials; private keys delivered as one-time downloads
- [x] Key management best practices - Generates strong keys (RSA 2048+, ECDSA P-256+) with unique `kid`
- [x] CSRF protection - Will use `AutoValidateAntiforgeryTokenAttribute` for POST endpoints
- [x] Rate limiting - Not required (internal admin tool, low traffic)

### Justified Variances

| Variance | Justification | Alternative Rejected Because |
|----------|---------------|------------------------------|
| SQLite instead of PostgreSQL | Standalone service with minimal persistence needs (key metadata only, no relational complexity). SQLite simplifies deployment (no external DB dependency), reduces container size, and matches the low-traffic admin tool profile. | PostgreSQL via Aspire requires orchestration/external service; overkill for simple key metadata storage (~100-1000 records max). |
| No multi-tenancy | Service generates keys for OIDC clients; tenant concept lives in the OIDC server. This tool is tenant-agnostic (admins generate keys, clients associate them with tenants). | Multi-tenancy would duplicate OIDC server concerns and complicate deployment without business value. |
| No Aspire orchestration | Standalone Docker container designed for simple deployment (docker run). No orchestration needed for single-service admin tool. | Aspire adds complexity and requires .NET 9 Aspire runtime; this service should be deployable anywhere Docker runs. |

**Gate Status**: ✅ PASSED (with documented variances)

## Project Structure

### Documentation (this feature)

```text
specs/001-key-license-generator/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
MrWhoOidc.KeyGen/                      # New project for key and license generation service
├── MrWhoOidc.KeyGen.csproj
├── Program.cs                          # Service entry point
├── appsettings.json
├── appsettings.Development.json
├── Dockerfile
├── .dockerignore
├── Domain/                             # Domain logic (key gen, license gen)
│   ├── Models/
│   │   ├── KeyPairMetadata.cs
│   │   ├── LicenseToken.cs
│   │   └── KeyDownloadRecord.cs
│   ├── Services/
│   │   ├── IKeyGenerationService.cs
│   │   ├── KeyGenerationService.cs
│   │   ├── ILicenseGenerationService.cs
│   │   └── LicenseGenerationService.cs
│   └── Cryptography/
│       ├── RsaKeyGenerator.cs
│       ├── EcdsaKeyGenerator.cs
│       └── JwkSerializer.cs
├── Persistence/                        # EF Core + SQLite
│   ├── KeyGenDbContext.cs
│   ├── Migrations/                     # EF Core migrations
│   └── GuidHelper.cs                   # UUIDv7 implementation (copied from MrWhoOidc.Auth)
├── Pages/                              # Razor Pages UI
│   ├── _ViewImports.cshtml
│   ├── _ViewStart.cshtml
│   ├── Shared/
│   │   ├── _Layout.cshtml
│   │   └── _ValidationScriptsPartial.cshtml
│   ├── Index.cshtml                    # Landing page
│   ├── Index.cshtml.cs
│   ├── KeyGeneration/
│   │   ├── Generate.cshtml             # Key pair generation form
│   │   ├── Generate.cshtml.cs
│   │   ├── List.cshtml                 # Key management dashboard
│   │   └── List.cshtml.cs
│   └── LicenseGeneration/
│       ├── Generate.cshtml             # License token generation form
│       └── Generate.cshtml.cs
├── Api/                                # Minimal API endpoints for downloads
│   ├── KeyDownloadEndpoints.cs
│   └── LicenseDownloadEndpoints.cs
├── wwwroot/                            # Static assets
│   ├── css/
│   ├── js/
│   └── favicon.ico
└── Configuration/
    └── KeyGenOptions.cs                # Configuration model

MrWhoOidc.KeyGen.Tests/                # Test project
├── MrWhoOidc.KeyGen.Tests.csproj
├── Domain/
│   ├── KeyGenerationServiceTests.cs
│   ├── LicenseGenerationServiceTests.cs
│   └── Cryptography/
│       ├── RsaKeyGeneratorTests.cs
│       └── EcdsaKeyGeneratorTests.cs
├── Integration/
│   ├── KeyGenerationIntegrationTests.cs
│   └── LicenseGenerationIntegrationTests.cs
└── TestHelpers/
    └── TestDataSeeder.cs
```

**Structure Decision**: Web application structure with domain separation. The service follows MrWhoOidc architectural principles (domain logic separate from HTTP/UI) but as a standalone project rather than extending MrWhoOidc.WebAuth. This allows independent deployment, simplified Docker packaging, and clear separation from the authorization server codebase.

## Complexity Tracking

### Complexity Justification

This section documents architectural decisions that add complexity and their justification:

| Decision | Why Needed | Simpler Alternative Rejected Because |
|----------|------------|-------------------------------------|
| Separate project (not in MrWhoOidc.WebAuth) | Eliminates security risk of private key generation in auth server; enables independent deployment and lifecycle; reduces auth server attack surface | Adding to MrWhoOidc.WebAuth perpetuates architectural flaw (auth server should never generate client private keys) |
| SQLite for persistence | Key metadata must survive container restarts; audit trail required | In-memory storage loses history; PostgreSQL overkill for ~100-1000 records |
| Domain layer separation | Testable key generation logic; reusable crypto services; clear security boundary | Direct implementation in Razor Pages mixes concerns and reduces testability |

## Planning Summary

### Artifacts Generated

**Phase 0 - Research**:

- [research.md](./research.md) - Technology decisions, best practices, resolved open questions

**Phase 1 - Design & Contracts**:

- [data-model.md](./data-model.md) - Entity definitions, database schema, relationships
- [contracts/api-spec.md](./contracts/api-spec.md) - HTTP API contracts (Razor Pages + Minimal APIs)
- [quickstart.md](./quickstart.md) - Developer implementation guide with checklist

**Agent Context**:

- Updated `.github/copilot-instructions.md` with:
  - Language: C# / .NET 9
  - Framework: ASP.NET Core 9.0, System.Security.Cryptography, System.IdentityModel.Tokens.Jwt
  - Database: SQLite for key metadata persistence

### Constitution Re-check

**After Phase 1 design, all gates remain PASSED**:

- ✅ Zero-warning policy will be enforced from project creation
- ✅ EF Core migrations will be used for all schema changes
- ✅ GuidHelper (UUIDv7) will be copied from MrWhoOidc.Auth and used for all primary keys
- ✅ Domain separation maintained (cryptography/services separate from HTTP/UI)
- ✅ Documented variances justified (SQLite, no Aspire, no multi-tenancy)

### Next Steps

1. **Run `/speckit.tasks`** to generate implementation tasks from this plan
2. **Create project structure** as defined in Project Structure section
3. **Follow quickstart.md** implementation checklist (Phase 1-10)
4. **Write tests alongside implementation** (not after)
5. **Enforce zero-warning policy** from first commit
6. **Test Docker build early** to catch containerization issues

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Standalone project | Security: Separate key generation from auth server |
| SQLite | Lightweight persistence for low-traffic admin tool |
| Razor Pages | Simple form-based UI, consistent with MrWhoOidc.WebAuth |
| One-time private key download | Security: Never persist private keys on server |
| UUIDv7 for kids | Time-ordered GUIDs for performance and collision prevention |
| No authentication | Deployment-environment responsibility (VPN, reverse proxy) |

### Success Criteria Mapping

From [spec.md](./spec.md), these success criteria are addressed by the design:

- **SC-001** (generate/download <10s): In-memory key generation ensures fast response
- **SC-002** (valid JWT signing): Standard System.Security.Cryptography APIs ensure spec compliance
- **SC-003** (JWKS import compatibility): JwkSerializer produces RFC 7517-compliant output
- **SC-004** (license generation <15s): License generation is simple JWT signing, <1s expected
- **SC-005** (license validation passes): Uses existing licensing key infrastructure
- **SC-006** (Docker startup <30s): Lightweight SQLite and Razor Pages ensure fast startup
- **SC-007** (browser compatibility): Standard Razor Pages with progressive enhancement
- **SC-008** (persistence across restarts): Docker volume mount for SQLite database
- **SC-009** (OIDC server cleanup): Enables removal of misplaced key generation code
- **SC-010** (complete JWK parameters): JwkSerializer includes all required fields per RFC 7517

### Implementation Phases Priority

**Phase 1 (P1 - MVP)**: Key pair generation + download (Phases 1-5 in quickstart)

**Phase 2 (P2)**: License generation + UI (Phases 6-7 in quickstart)

**Phase 3 (P3)**: Key lifecycle management (Phases 8-10 in quickstart)

**Phase 4**: Docker containerization + deployment docs (Phase 9 in quickstart)

**Ready to proceed**: ✅ Run `/speckit.tasks` to break down into actionable implementation tasks

