# Implementation Plan: Docker Deployment Package

**Branch**: `003-docker-deployment-compose` | **Date**: 2025-11-01 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/003-docker-deployment-compose/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

This feature enables public distribution and deployment of the MrWhoOidc OIDC server through a Docker image published to GitHub Container Registry (ghcr.io). The implementation provides a production-ready Docker Compose configuration supporting PostgreSQL (required) and Redis (optional) with comprehensive documentation for operations teams. Key deliverables include: optimized multi-stage Dockerfile, GitHub Actions CI/CD workflow for automated image publishing, production-ready docker-compose.yml with health checks and security best practices, and deployment documentation covering configuration, upgrades, and backup procedures.

## Technical Context

**Language/Version**: .NET 9 (as per constitution)  
**Primary Dependencies**: ASP.NET Core, EF Core, PostgreSQL 16, Redis 7.2 (optional), Docker, Docker Compose v2  
**Storage**: PostgreSQL via Aspire connection "authdb" (existing), Docker volumes for persistence  
**Testing**: MSTest with Docker container integration tests, compose validation tests  
**Target Platform**: Linux containers (multi-architecture: x64, ARM64)  
**Project Type**: Infrastructure/DevOps (Docker packaging and CI/CD)  
**Performance Goals**: Image size <200MB compressed, startup time <30 seconds, support 1000 concurrent auth requests  
**Constraints**: Zero secrets in image, semantic versioning, compatible with Docker Engine 20.10+, Compose v2.0+  
**Scale/Scope**: Single-instance deployment (PostgreSQL + Redis), suitable for 10k-100k users per instance

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

This feature is **infrastructure/DevOps focused** and does not involve changes to core OIDC protocol logic or data models. Therefore, most constitution gates are N/A for this feature.

**Build Quality Gates**:

- [x] Zero compiler warnings (Debug and Release configurations) - N/A: No code changes to .NET projects
- [x] Zero analyzer warnings (unless documented suppressions in place) - N/A: No code changes to .NET projects
- [x] All tests pass without warnings - Will add Docker/compose validation tests
- [x] EF Core migrations generated using `dotnet ef migrations add` (not hand-written) - N/A: No schema changes
- [x] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`)  - N/A: No new entities
- [x] OIDC specification compliance validated with RFC references in tests - N/A: No protocol changes

**Domain Architecture Gates**:

- [x] No HTTP logic in MrWhoOidc.Auth - N/A: No domain logic changes
- [x] No OpenIddict/Microsoft Identity Platform packages - N/A: Infrastructure only
- [x] Minimal APIs + Razor Pages in WebAuth - N/A: No endpoint changes

**Documentation Gates**:

- [ ] Deployment documentation created in `/docs` (deployment-guide.md)
- [ ] README.md updated with Docker deployment instructions
- [ ] Markdown files follow formatting standards (MD022, MD032, MD040, MD047)
- [ ] GitHub Actions workflow documented

**Docker/DevOps Gates**:

- [ ] Dockerfile uses multi-stage build for minimal image size
- [ ] No secrets in Docker image (validated via image inspection)
- [ ] docker-compose.yml follows v2 syntax
- [ ] Health checks configured for all services
- [ ] Image labels include version, license, source URL
- [ ] Multi-architecture build (x64, ARM64) configured in GitHub Actions

**Status**: ✅ PASS - All applicable gates identified, most N/A for infrastructure feature

## Project Structure

### Documentation (this feature)

```text
specs/003-docker-deployment-compose/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command) - N/A for this feature
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command) - N/A for this feature
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)

This feature adds Docker/CI-CD infrastructure without modifying the existing .NET project structure.

```text
# Root level files
MrWhoOidc/
├── Dockerfile                           # NEW: Production Dockerfile for MrWhoOidc.WebAuth
├── docker-compose.yml                   # MODIFIED: Production-ready compose file
├── docker-compose.dev.yml               # EXISTING: Development compose (kept separate)
├── .dockerignore                        # NEW: Exclude unnecessary files from build context
├── README.md                            # MODIFIED: Add Docker deployment section
│
# GitHub Actions workflows
├── .github/
│   └── workflows/
│       └── docker-publish.yml           # NEW: Build and publish Docker image
│
# Documentation
├── docs/
│   ├── deployment-guide.md              # NEW: Comprehensive deployment documentation
│   ├── docker-compose-examples.md       # NEW: Example configurations (single-tenant, multi-tenant, etc.)
│   └── upgrade-guide.md                 # NEW: Version upgrade procedures
│
# Existing projects (no changes to structure)
├── MrWhoOidc.WebAuth/                   # Target project for Docker image
├── MrWhoOidc.Auth/                      # Domain logic (no changes)
├── MrWhoOidc.UnitTests/                 # Will add Docker validation tests
└── [other existing projects...]
```

**Structure Decision**: Infrastructure-only feature adds Docker artifacts at repository root and GitHub Actions workflow. No changes to .NET project structure or domain logic. The existing development `docker-compose.yml` will be renamed to `docker-compose.dev.yml`, and a new production-optimized `docker-compose.yml` will be created for public use.

## Complexity Tracking

No constitution violations for this feature. This is an infrastructure addition that follows established Docker and CI/CD best practices without introducing architectural complexity to the .NET codebase.

---

## Phase 0: Research (✅ Complete)

**Status**: Complete  
**Artifact**: [research.md](./research.md)

### Research Summary

Completed comprehensive research covering 12 key technical areas:

1. **Docker Image Optimization**: Multi-stage Dockerfile with .NET 9 chiseled runtime (<200MB target)
2. **Container Registry**: GitHub Container Registry (ghcr.io) for free public hosting
3. **Image Tagging**: Semantic versioning strategy (latest, v1.2.3, v1.2, v1, main, sha-commit)
4. **Multi-Architecture**: x64 and ARM64 support via Docker buildx
5. **Health Checks**: HTTP health endpoints with dependency ordering
6. **Configuration**: Environment variables with .env examples (12-factor compliance)
7. **Volume Strategy**: Named volumes for PostgreSQL and Redis with backup procedures
8. **Network Isolation**: Internal (database) and edge (public) networks for security
9. **TLS Management**: Volume-mounted certificates with clear provisioning documentation
10. **Migration Strategy**: Automatic EF Core migrations on startup (idempotent)
11. **Redis Integration**: Optional with graceful degradation pattern
12. **CI/CD Workflow**: GitHub Actions with automated multi-platform builds

All technical decisions documented with rationale, alternatives considered, and references.

---

## Phase 1: Design & Contracts (✅ Complete)

**Status**: Complete  
**Artifacts**:

- [data-model.md](./data-model.md) - N/A (no data model changes)
- [quickstart.md](./quickstart.md) - Complete deployment guide
- [contracts/](./contracts/) - N/A (no API contract changes)

### Design Summary

**Data Model**: No changes required - existing entities support Docker deployment without modification.

**Quick Start Guide**: Created comprehensive quickstart covering:

- Minimal 10-minute deployment procedure
- Configuration options (Redis, multi-tenancy, SMTP, TLS)
- Upgrade procedures with database backup
- Troubleshooting common issues
- Architecture overview and environment variable reference
- Security considerations and next steps

**API Contracts**: N/A - This feature does not introduce new API endpoints or modify existing contracts.

**Agent Context**: Updated `.github/copilot-instructions.md` with Docker/Docker Compose technology stack.

---

## Phase 2: Implementation Planning

**Status**: Ready for `/speckit.tasks` command

**Next Steps**:

1. Run `/speckit.tasks` to generate implementation tasks breakdown
2. Implement Dockerfile with multi-stage build
3. Update docker-compose.yml for production
4. Create GitHub Actions workflow (docker-publish.yml)
5. Write comprehensive deployment documentation
6. Add Docker validation tests
7. Update README.md with Docker deployment section

**Ready for Implementation**: All research complete, design validated, constitution checks passed.
