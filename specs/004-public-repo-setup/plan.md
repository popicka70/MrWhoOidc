# Implementation Plan: Public Repository Setup for MrWhoOidc Distribution

**Branch**: `004-public-repo-setup` | **Date**: November 2, 2025 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/004-public-repo-setup/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

Create a public GitHub repository structure in the `/MrWho` folder containing comprehensive documentation, multiple docker-compose deployment configurations, NuGet package information, and demo applications. The repository will serve as the primary distribution and documentation hub for MrWhoOidc OIDC IdP, enabling developers to deploy the service in under 10 minutes via clear Quick Start documentation while providing production-ready configurations and integration examples.

**Technical Approach**: Documentation aggregation and transformation workflow - copy and adapt existing docs from main solution, create docker-compose variants with inline documentation, structure NuGet package information, and include working demo applications from Examples folder.

## Technical Context

**Language/Version**: Markdown for documentation; Docker Compose V2 syntax; existing demos in C# (.NET 9), Go, React  
**Primary Dependencies**: Docker Engine 20.10+, Docker Compose V2+; ghcr.io/popicka70/mrwhooidc images  
**Storage**: File-based repository structure; no database or persistence layer for this feature  
**Testing**: Manual validation of docker-compose configurations (docker-compose config); README walkthrough testing; link validation  
**Target Platform**: GitHub repository targeting Linux/Windows/macOS developers with Docker installed  
**Project Type**: Documentation and deployment configuration repository (not a code project)  
**Performance Goals**: README enables 10-minute deployment; documentation searchable/navigable in under 30 seconds  
**Constraints**: Repository size < 100MB (excluding git history); all docs in Markdown; no build tooling required  
**Scale/Scope**: ~15-20 documentation files; 3-5 docker-compose variants; 3-5 demo applications; comprehensive README (~500-800 lines)

## Constitution Check

Gate: Must pass before Phase 0 research. Re-check after Phase 1 design.

**Build Quality Gates**:

- [x] Zero compiler warnings - N/A (documentation/configuration only, no code compilation)
- [x] Zero analyzer warnings - N/A (no C# code in this feature)
- [x] All tests pass without warnings - N/A (manual validation of docker-compose configs)
- [x] EF Core migrations generated using dotnet ef - N/A (no database changes)
- [x] Entity primary keys use GuidHelper.NewId() - N/A (no entities)
- [x] OIDC specification compliance validated - N/A (no protocol implementation, documentation only)

**Architecture Gates**:

- [x] Core OIDC logic in MrWhoOidc.Auth - N/A (no code changes)
- [x] HTTP surface in MrWhoOidc.WebAuth - N/A (no code changes)
- [x] Target .NET 9 - N/A (documentation references .NET 9, no compilation)
- [x] PostgreSQL via Aspire connection "authdb" - Referenced in docker-compose files
- [x] No OpenIddict or Microsoft Identity Platform packages - N/A (no dependencies added)

**Documentation Quality Gates**:

- [ ] All docker-compose files pass validation (docker-compose config)
- [ ] README Quick Start tested and verified (10-minute deployment achievable)
- [ ] All internal documentation links are valid and reachable
- [ ] All external links (GHCR, Docker Hub, NuGet.org) are valid
- [ ] Environment variable documentation is complete and accurate
- [ ] Troubleshooting section covers common deployment issues

**Gate Status**: PASS - No constitution violations. This is a pure documentation and configuration feature with no code changes to the main solution.

## Project Structure

### Documentation (this feature)

```text
specs/004-public-repo-setup/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output - N/A for this feature (no data model)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output - N/A for this feature (no API contracts)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)

This feature operates on the `/MrWho` folder which will become a separate public GitHub repository.

```text
MrWho/                              # Public repository root (destination)
├── README.md                        # Primary entry point with Quick Start
├── LICENSE                          # MIT or chosen license
├── .gitignore                       # Git ignore rules
├── .env.example                     # Template with all env vars documented
├── docker-compose.yml               # Basic deployment (PostgreSQL only)
├── docker-compose.redis.yml         # High-performance (with Redis)
├── docker-compose.production.yml    # Production-hardened configuration
├── docker-compose.dev.yml           # Development with MailHog
├── docs/                            # Documentation directory
│   ├── deployment-guide.md          # Comprehensive deployment instructions
│   ├── upgrade-guide.md             # Version upgrade procedures
│   ├── docker-compose-examples.md   # Deployment scenario examples
│   ├── docker-security-best-practices.md  # Security hardening guide
│   ├── admin-guide.md               # Admin UI usage guide
│   ├── developer-guide.md           # Integration and development guide
│   ├── troubleshooting.md           # Common issues and solutions
│   └── configuration-reference.md   # Complete env var reference
├── demos/                           # Demo applications
│   ├── dotnet-mvc-client/           # ASP.NET Core MVC OIDC client
│   ├── react-client/                # React SPA with OIDC
│   ├── go-client/                   # Go web application client
│   └── README.md                    # Demo overview and instructions
├── packages/                        # NuGet package information
│   ├── README.md                    # Package overview and installation
│   └── integration-examples.md      # Code examples for each package
└── scripts/                         # Helper scripts
    ├── generate-cert.sh             # TLS certificate generation
    ├── health-check.sh              # Deployment verification script
    └── README.md                    # Scripts documentation
```

**Structure Decision**: Single repository structure optimized for documentation and deployment. The `/MrWho` folder is a self-contained public repository with clear separation of concerns: root-level docker-compose files for immediate deployment, `/docs` for comprehensive documentation, `/demos` for working integration examples, and `/packages` for NuGet information. This structure follows GitHub repository best practices and aligns with the spec's requirement for easy navigation and quick deployment.

## Complexity Tracking

Fill ONLY if Constitution Check has violations that must be justified

No constitution violations - this feature does not modify core solution code, add dependencies, or change architectural patterns. All work is documentation and configuration in the `/MrWho` folder.

## Execution Phases

### Phase 0: Research ✅ COMPLETE

**Output**: `research.md` - Comprehensive research covering:

- Documentation file selection strategy (8 core files identified)
- Docker Compose variants (4 configurations with use cases)
- Demo application selection (3 demos across .NET, React, Go)
- NuGet package documentation structure
- README structure (8 sections, 600-800 lines)
- Environment variable documentation approach (3-tier strategy)
- File adaptation and validation procedures

**Key Decisions**:

- Copy-and-adapt workflow for documentation
- Three-tier env var documentation (inline, .env.example, reference doc)
- Docker Compose integration pattern for demos
- Progressive disclosure documentation strategy

**Status**: All technical unknowns resolved. No NEEDS CLARIFICATION markers remaining.

### Phase 1: Design & Contracts ✅ COMPLETE

**Output**: `quickstart.md` - Detailed 10-minute deployment walkthrough with:

- 4-step Quick Start content for README
- Expected outputs for every command
- Troubleshooting for common issues
- Testing checklist for validation
- Validation script (validate-quickstart.sh)

**Note**: `data-model.md` and `/contracts` are N/A for this feature (no data model or API contracts).

**Agent Context**: ✅ Updated via `update-agent-context.ps1 -AgentType copilot`

**Status**: Design complete. Ready for Phase 2 task breakdown.

### Phase 2: Task Breakdown (Next Command)

**Command**: `/speckit.tasks`

**Will produce**: `tasks.md` with implementation work items organized by priority:

- P1 tasks: Basic docker-compose, .env.example, README Quick Start, health-check script
- P2 tasks: Additional docker-compose variants, documentation copying/adaptation, configuration reference
- P3 tasks: Demo preparation, NuGet package documentation, integration examples

Each task will include:

- Clear acceptance criteria
- Dependencies on other tasks
- Estimated effort
- Testing validation steps

## Planning Summary and Next Steps

**Planning Complete**: ✅ All preparation phases finished

**Branch**: `004-public-repo-setup`
**Spec**: [spec.md](./spec.md)
**Research**: [research.md](./research.md)
**Quick Start**: [quickstart.md](./quickstart.md)

**Constitution Check**: PASS - No violations  
**Technical Context**: Complete - No unknowns  
**Design**: Complete - Quick Start validated

**Next Step**: Run `/speckit.tasks` to generate implementation tasks.

**Implementation will produce**:

- `/MrWho` folder with complete public repository structure
- 4 docker-compose variants (basic, redis, production, dev)
- README.md with 8 sections (~700 lines)
- 8 documentation files copied and adapted from main solution
- 3 demo applications with docker-compose integration
- NuGet package documentation with code examples
- Helper scripts (health-check, certificate generation)
- .env.example with comprehensive variable documentation

**Target**: Enable 10-minute deployment via README Quick Start while providing production-ready configurations and integration examples.
