# Implementation Plan: Auth Architecture Cleanup

**Branch**: `015-auth-architecture-cleanup` | **Date**: 2025-12-27 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/015-auth-architecture-cleanup/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

Comprehensive architectural refactoring of MrWhoOidc.Auth and MrWhoOidc.WebAuth to achieve clean separation of concerns. The refactoring addresses 8 layer violations, 3 god classes (TokenService 723 lines, AuthorizeHandler 708 lines, AuthDbContext 1772 lines), 6 areas of code duplication, 5 security concerns, and 4 logic flaws. Implementation follows a 5-phase approach: (1) security fixes, (2) layer violation corrections, (3) god class decomposition, (4) duplication removal, (5) code quality improvements.

## Technical Context

**Language/Version**: C# / .NET 9  
**Primary Dependencies**: ASP.NET Core, EF Core, Microsoft.IdentityModel.Tokens, Fido2NetLib  
**Storage**: PostgreSQL via Aspire connection "authdb"  
**Testing**: MSTest with TestServer for integration tests  
**Target Platform**: Linux server (Docker containers), Windows for development  
**Project Type**: Multi-project solution (MrWhoOidc.Auth domain, MrWhoOidc.WebAuth HTTP surface)  
**Performance Goals**: Token endpoint maintains <200ms p95 under 1000 concurrent requests  
**Constraints**: Zero breaking changes to public OIDC endpoints, all existing tests must pass  
**Scale/Scope**: 50+ services/handlers to refactor, 24 functional requirements across 5 phases

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**Domain Architecture Gates** (from Constitution §II):

- [x] Core OIDC domain logic stays in MrWhoOidc.Auth (Protocols, Persistence, Crypto, Services)
- [x] HTTP surface stays in MrWhoOidc.WebAuth (Handlers, Pages, Background workers)
- [x] Clear boundary enforced: Auth = "what/why", WebAuth = "how/when"
- [x] No HTTP concerns (controllers, middleware, Razor) in Auth layer

**Build Quality Gates** (from Constitution §VI):

- [ ] Zero compiler warnings (Debug and Release configurations)
- [ ] Zero analyzer warnings (unless documented suppressions in place)
- [ ] All tests pass without warnings
- [x] EF Core migrations generated using `dotnet ef migrations add` (not hand-written)
- [x] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`)
- [x] OIDC specification compliance validated with RFC references in tests

**Security Gates** (from Constitution §V):

- [x] Argon2id/BCrypt for password storage (no changes to existing)
- [x] Tenant isolation via ITenantAccessor enforced at data layer
- [x] Rate limiting on admin APIs preserved

**Prohibited Patterns** (from Constitution - Governance):

- [x] No HTTP logic in MrWhoOidc.Auth (this refactoring enforces this)
- [x] No hardcoded connection strings
- [x] No hand-edited DB schema (no schema changes in this refactoring)

## Project Structure

### Documentation (this feature)

```text
specs/015-auth-architecture-cleanup/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
MrWhoOidc.Auth/
├── Crypto/                    # Key management (existing)
├── Options/                   # Configuration options
│   └── OidcOptions.cs         # NEW: Move from WebAuth
├── Protocols/                 # OIDC/OAuth constants (existing)
├── Services/
│   ├── Authentication/        # NEW: Client auth abstraction
│   │   ├── IClientAuthenticationService.cs
│   │   ├── ClientSecretAuthenticator.cs
│   │   └── PrivateKeyJwtAuthenticator.cs
│   ├── Authorization/         # Existing + refactored
│   │   ├── IAuthorizeService.cs
│   │   ├── AuthorizeService.cs
│   │   └── ConsentService.cs  # Fix: Add transaction
│   ├── Tokens/                # NEW: Decomposed from TokenService
│   │   ├── ITokenService.cs   # Simplified orchestrator
│   │   ├── AuthorizationCodeExchanger.cs
│   │   ├── RefreshTokenExchanger.cs
│   │   ├── ClientCredentialsTokenFactory.cs
│   │   ├── AccessTokenClaimBuilder.cs
│   │   ├── TokenLifetimeResolver.cs
│   │   ├── RoleClaimBuilder.cs
│   │   ├── OpaqueTokenPolicy.cs
│   │   └── LogoutTokenService.cs  # NEW: Move from WebAuth
│   ├── Users/
│   │   ├── IRegistrationService.cs  # NEW: Move from WebAuth
│   │   └── RegistrationService.cs   # Domain logic only
│   └── MtlsThumbprintResolver.cs    # NEW: Centralized mTLS
├── Observability/
│   └── GlobalAuthMetrics.cs   # RENAME: From OidcMetrics
└── Utils/
    └── CryptoHelper.cs        # Remove legacy wrappers in callers

MrWhoOidc.WebAuth/
├── Handlers/
│   ├── AuthorizeHandler.cs    # REFACTOR: <200 lines, orchestrator only
│   ├── TokenHandler.cs        # Existing thin adapter
│   └── Logout/
│       └── LogoutTokenBuilder.cs  # MOVE JWT creation to Auth
├── Services/
│   ├── ClientAuthenticator.cs # REFACTOR: HTTP extraction only
│   └── RegistrationService.cs # REFACTOR: Delegate to Auth
└── Observability/
    └── OidcEndpointMetrics.cs # RENAME: Distinguish from Auth
```

**Structure Decision**: This refactoring does NOT add new projects. It reorganizes existing code within MrWhoOidc.Auth and MrWhoOidc.WebAuth to enforce clean layer boundaries per Constitution §II.

## Complexity Tracking

*No constitution violations requiring justification. All changes align with existing architecture.*

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| N/A | This refactoring removes violations, not adds them | - |
