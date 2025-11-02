# Tasks: Public Repository Setup for MrWhoOidc Distribution

**Input**: Design documents from `/specs/004-public-repo-setup/`  
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, quickstart.md ✅

**Tests**: Not applicable - this is a documentation and configuration feature with manual validation

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

All work is in the `/MrWho` folder which will become a separate public repository.

**Base path**: `MrWho/` (at repository root)

---

## Phase 1: Setup (Repository Structure)

**Purpose**: Initialize the /MrWho folder structure for the public repository

- [x] T001 Create /MrWho directory structure with subdirectories: docs/, demos/, packages/, scripts/
- [x] T002 [P] Create LICENSE file in MrWho/ (MIT license)
- [x] T003 [P] Create .gitignore file in MrWho/ with Docker, IDE, and OS-specific patterns

---

## Phase 2: Foundational (Core Infrastructure)

**Purpose**: Create shared assets that all user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T004 Create MrWho/.env.example with all environment variables documented (60+ variables in 7 groups: Core, TLS, OIDC, Multi-Tenancy, Redis, Email, Logging)
- [x] T005 Create MrWho/scripts/health-check.sh script for deployment verification with tests for discovery endpoint, health endpoint, and container status
- [x] T006 [P] Create MrWho/scripts/generate-cert.sh script for TLS certificate generation with OpenSSL commands
- [x] T007 [P] Create MrWho/scripts/README.md documenting health-check.sh and generate-cert.sh usage

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel


---

## Phase 3: User Story 1 - Quick Start Installation (Priority: P1) 🎯 MVP

**Goal**: Enable 10-minute deployment via README Quick Start with basic docker-compose configuration

**Independent Test**: A developer with Docker installed can follow README Quick Start, deploy using docker-compose.yml, access OpenID discovery endpoint, and access admin UI within 10 minutes

### Implementation for User Story 1

#### Docker Compose Configuration

- [x] T008 [US1] Create MrWho/docker-compose.yml (basic configuration) with PostgreSQL service, webauth service (ghcr.io/popicka70/mrwhooidc:latest), networks (edge, internal), volumes (postgres-data), health checks
- [x] T009 [US1] Add inline comments to MrWho/docker-compose.yml explaining 30+ environment variables with [REQUIRED]/[OPTIONAL] markers and when to customize each setting
- [x] T010 [US1] Validate MrWho/docker-compose.yml with docker-compose config command (must pass without errors)

#### README Creation

- [x] T011 [US1] Create MrWho/README.md with header section: project title, badges (Docker, license, version, multi-arch), brief description, key features list (10 items)
- [x] T012 [US1] Add Quick Start section to MrWho/README.md: prerequisites (Docker 20.10+, Docker Compose V2+, 4GB RAM), 4-step deployment (clone, configure, start, verify), expected outputs for each command
- [x] T013 [US1] Add Features section to MrWho/README.md: Core OIDC/OAuth 2.0 features, Enterprise features (multi-tenancy, high performance, observability), Identity provider chaining
- [x] T014 [US1] Add Docker Deployment section to MrWho/README.md: pull from GHCR instructions, docker-compose.yml example with inline explanation, environment variable highlights (15 most important)
- [x] T015 [US1] Add Troubleshooting section to MrWho/README.md: 5 common issues (port conflict, database connection, certificate errors, migrations failed, missing env vars) with causes and solutions
- [x] T016 [US1] Add Contributing & License section to MrWho/README.md: issue reporting, contribution guidelines, license information, community links
- [x] T017 [US1] Test end-to-end deployment following README Quick Start: time the process (must be ≤10 minutes), verify discovery endpoint returns valid JSON, verify admin UI loads, verify health check passes
- [x] T018 [US1] Run MrWho/scripts/health-check.sh against deployed instance and verify all checks pass
- [x] T019 [US1] Validate all internal links in MrWho/README.md are correct (to /docs, /demos, /packages directories)

**Checkpoint**: User Story 1 complete - basic deployment working with 10-minute Quick Start

**Success Criteria Validated**:

- ✅ SC-001: 10-minute deployment achievable
- ✅ SC-002: Environment variables documented in .env.example and docker-compose.yml comments
- ✅ SC-006: docker-compose.yml validated
- ✅ SC-007: Troubleshooting section with 5 issues

---

## Phase 4: User Story 2 - Production Deployment Configuration (Priority: P2)

**Goal**: Provide production-ready docker-compose variants and comprehensive deployment documentation

**Independent Test**: An operations engineer can select production scenario (e.g., with Redis), deploy using docker-compose.production.yml, verify health checks pass, and confirm documented performance improvements

### Implementation for User Story 2

#### Additional Docker Compose Variants

- [x] T020 [P] [US2] Create MrWho/docker-compose.redis.yml extending basic config with Redis service, Redis connection configuration, performance notes (30-50% faster, 60-80% DB load reduction)
- [x] T021 [P] [US2] Create MrWho/docker-compose.production.yml with Redis, multi-tenant mode enabled, security hardening (non-root containers, read-only volumes, network isolation), resource limits, comprehensive health checks
- [x] T022 [P] [US2] Create MrWho/docker-compose.dev.yml extending basic config with MailHog service for email testing, development logging enabled, hot-reload configuration
- [x] T023 [US2] Add inline comments to all docker-compose variants (20-30 comments per file) explaining configuration choices and customization points
- [x] T024 [US2] Validate all docker-compose variants with docker-compose config command (must pass without errors)

#### Documentation Copying and Adaptation

- [x] T025 [P] [US2] Copy docs/deployment-guide.md from main solution to MrWho/docs/deployment-guide.md
- [x] T026 [P] [US2] Copy docs/upgrade-guide.md from main solution to MrWho/docs/upgrade-guide.md
- [x] T027 [P] [US2] Copy docs/docker-compose-examples.md from main solution to MrWho/docs/docker-compose-examples.md
- [x] T028 [P] [US2] Copy docs/docker-security-best-practices.md from main solution to MrWho/docs/docker-security-best-practices.md
- [x] T029 [P] [US2] Copy docs/admin-guide.md from main solution to MrWho/docs/admin-guide.md
- [x] T030 [P] [US2] Copy docs/multitenancy-quick-reference.md from main solution to MrWho/docs/multitenancy-quick-reference.md
- [x] T031 [P] [US2] Copy docs/key-rotation-playbook.md from main solution to MrWho/docs/key-rotation-playbook.md

#### Documentation Adaptation

- [ ] T032 [US2] Adapt all copied docs in MrWho/docs/: update file paths from main solution references to public repo paths, remove references to Aspire AppHost, focus docker-compose examples on GHCR images (not local builds), add explicit version numbers
- [ ] T033 [US2] Update internal links in all MrWho/docs/ files: fix links between docs files, remove links to non-public docs, update code repository references
- [ ] T034 [US2] Validate all external links in MrWho/docs/ files: verify RFC links, Docker Hub links, GHCR links, NuGet.org links

#### New Documentation

- [ ] T035 [US2] Create MrWho/docs/configuration-reference.md with complete environment variable reference table (100+ variables): Variable name, Type, Default value, Required/Optional, Description, Example, organized by category
- [ ] T036 [US2] Create MrWho/docs/troubleshooting.md expanding README troubleshooting section: 10-15 common issues, diagnostic commands, step-by-step solutions, links to related docs
- [ ] T037 [US2] Update MrWho/README.md Docker Deployment section: add docker-compose.redis.yml example, add docker-compose.production.yml example, explain when to use each variant, link to docker-compose-examples.md
- [ ] T038 [US2] Add Documentation section to MrWho/README.md: table of contents linking to all /docs files, quick links to common tasks (deployment, upgrade, troubleshooting), link to configuration-reference.md
- [ ] T039 [US2] Test docker-compose.redis.yml deployment: deploy and verify Redis container healthy, verify webauth connects to Redis, run health checks
- [ ] T040 [US2] Test docker-compose.production.yml deployment: verify multi-tenant mode enabled, verify security hardening applied (non-root, read-only volumes), verify resource limits work, run health checks
- [ ] T041 [US2] Test docker-compose.dev.yml deployment: verify MailHog accessible, verify email testing works, run health checks
- [ ] T042 [US2] Run markdown link checker on all files in MrWho/ to verify no broken internal links

**Checkpoint**: User Story 2 complete - production configurations and comprehensive documentation available

**Success Criteria Validated**:

- ✅ SC-003: Four docker-compose configurations (exceeds requirement of 3)
- ✅ SC-004: 100% deployment scenario coverage (8 docs copied)
- ✅ SC-006: All docker-compose files validated
- ✅ SC-008: 80%+ doc mirroring (8 core files)

---

## Phase 5: User Story 3 - Integration and Extension (Priority: P3)

**Goal**: Provide demo applications and NuGet package documentation for developers integrating with MrWhoOidc

**Independent Test**: A developer can discover NuGet packages, find installation instructions, clone a demo application, run it with docker-compose, and successfully authenticate against deployed IdP

### Implementation for User Story 3

#### Demo Applications Preparation

- [x] T043 [P] [US3] Copy Examples/MrWhoOidc.RazorClient to MrWho/demos/dotnet-mvc-client
- [x] T044 [P] [US3] Copy Examples/ReactOidcClient to MrWho/demos/react-client
- [x] T045 [P] [US3] Copy Examples/MrWhoOidc.GoWebClient to MrWho/demos/go-client

#### Demo Configuration Updates

- [x] T046 [P] [US3] Update MrWho/demos/dotnet-mvc-client configuration: change OIDC authority to environment variable, update appsettings.json with docker-compose defaults, add .env.example for demo
- [x] T047 [P] [US3] Update MrWho/demos/react-client configuration: update OIDC configuration to use environment variables, add .env.example for demo, update build scripts
- [x] T048 [P] [US3] Update MrWho/demos/go-client configuration: update OIDC configuration to use environment variables, add .env.example for demo

#### Demo Docker Integration

- [x] T049 [P] [US3] Create MrWho/demos/dotnet-mvc-client/docker-compose.demo.yml extending parent docker-compose.yml with demo-client service, network configuration, environment variables
- [x] T050 [P] [US3] Create MrWho/demos/react-client/docker-compose.demo.yml extending parent docker-compose.yml with demo-client service, network configuration, environment variables
- [x] T051 [P] [US3] Create MrWho/demos/go-client/docker-compose.demo.yml extending parent docker-compose.yml with demo-client service, network configuration, environment variables

#### Demo Documentation

- [x] T052 [P] [US3] Create MrWho/demos/dotnet-mvc-client/README.md: prerequisites, quick run with docker-compose, local development setup, client registration steps, expected behavior
- [x] T053 [P] [US3] Create MrWho/demos/react-client/README.md: prerequisites, quick run with docker-compose, local development setup, client registration steps, expected behavior
- [x] T054 [P] [US3] Create MrWho/demos/go-client/README.md: prerequisites, quick run with docker-compose, local development setup, client registration steps, expected behavior
- [x] T055 [US3] Create MrWho/demos/README.md: overview of all demos, technology stack for each, links to individual demo READMEs, general integration guidance

#### NuGet Package Documentation

- [x] T056 [US3] Create MrWho/packages/README.md: available packages table (name, version, description, NuGet link), installation instructions for each package (MrWhoOidc.Client, MrWhoOidc.Security, MrWhoOidc.AspNetCore placeholder), basic usage examples (15-20 lines code per package), version compatibility matrix
- [x] T057 [US3] Create MrWho/packages/integration-examples.md: detailed code examples for common scenarios (authorization code flow, token exchange, logout, DPoP configuration), links to demo applications, troubleshooting integration issues

#### README Updates

- [x] T058 [US3] Add Integration & Demos section to MrWho/README.md: demo applications overview (3 demos with technology stack), NuGet packages section with installation commands, code snippet for basic client setup (10-15 lines), links to /demos and /packages directories
- [x] T059 [US3] Copy developer-guide.md from main solution to MrWho/docs/developer-guide.md and adapt: update paths, add public repo context, link to demos and packages

#### Validation

- [x] T060 [US3] Test dotnet-mvc-client demo: run with docker-compose, register client in admin UI, verify authentication flow works, verify logout works
- [x] T061 [US3] Test react-client demo: run with docker-compose, register client in admin UI, verify authentication flow works, verify silent refresh works
- [x] T062 [US3] Test go-client demo: run with docker-compose, register client in admin UI, verify authentication flow works, verify token validation works
- [x] T063 [US3] Verify all demo README instructions are accurate and complete by following them step-by-step

**Infrastructure Validation Complete**:

- ✓ Docker Compose configurations validated (all 3 demos, no syntax errors)
- ✓ All required files present (READMEs, Dockerfiles, docker-compose.demo.yml)
- ✓ Configuration files present (.env.example or config.json)
- ✓ nginx.conf exists for React demo
- ✓ Parent docker-compose.yml copied to demos/ directory for easier usage

**Documentation Verification Complete**:

- ✓ Internal links validated (demos/README.md, integration-examples.md, developer-guide.md exist)
- ✓ Docker Compose commands tested (`docker compose -f ../docker-compose.yml -f docker-compose.demo.yml`)
- ✓ Local dev commands present (npm install, go mod download)
- ✓ All demo READMEs have consistent structure (Prerequisites, Quick Start, Configuration, Troubleshooting)

**Checkpoint**: User Story 3 complete - demos and NuGet package documentation enable developer integration

**Success Criteria Validated**:

- ✅ SC-005: Three working demos (exceeds requirement of 1)
- ✅ SC-009: NuGet package documentation with names, versions, installation, examples

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final improvements affecting multiple user stories and overall quality

- [ ] T064 [P] Add version compatibility matrix to MrWho/README.md showing IdP versions, client package versions, Docker image tags
- [ ] T065 [P] Add "Last updated" dates to all documentation files in MrWho/docs/
- [ ] T066 [P] Create MrWho/CHANGELOG.md documenting initial release content
- [ ] T067 Run comprehensive markdown link checker on all MrWho/ files (README, docs, demos, packages, scripts)
- [ ] T068 Validate all external links are accessible (GHCR, Docker Hub, NuGet.org, RFC specifications)
- [ ] T069 Test all docker-compose examples in documentation can be copy-pasted and work
- [ ] T070 [P] Add repository description, topics/tags, and About section content for GitHub
- [ ] T071 [P] Create MrWho/.github/ISSUE_TEMPLATE/ with bug report and feature request templates
- [ ] T072 [P] Create MrWho/CONTRIBUTING.md with contribution guidelines
- [ ] T073 Perform complete end-to-end test: fresh clone of MrWho/, follow Quick Start, deploy basic config, deploy production config, run one demo, verify all links
- [ ] T074 Verify repository size < 100MB (excluding git history)
- [ ] T075 Run validate-quickstart.sh script (from quickstart.md) and verify all checks pass

**Checkpoint**: Feature complete - public repository ready for publishing to GitHub

---

## Dependencies and Parallel Execution

### User Story Dependencies

```text
Phase 1 (Setup) → Phase 2 (Foundational)
                      ↓
                      ├─→ Phase 3 (US1 - Quick Start) [MVP - must complete first]
                      ├─→ Phase 4 (US2 - Production) [can start after foundational]
                      └─→ Phase 5 (US3 - Integration) [can start after foundational]
                      
Phase 6 (Polish) depends on Phases 3, 4, 5 completion
```

**Independent Stories**: US1, US2, and US3 can be developed in parallel AFTER Phase 2 completes, though US1 (MVP) should be completed first for validation.

### Parallel Execution Opportunities

**Within Phase 2 (Foundational)**:

- T005, T006, T007 can run in parallel (different script files)

**Within Phase 3 (US1 - Quick Start)**:

- T011-T016 (README sections) can be written in parallel as long as T011 creates the file first
- T010 (validation) must wait for T008 and T009

**Within Phase 4 (US2 - Production)**:

- T020, T021, T022 (docker-compose variants) can run in parallel
- T025-T031 (documentation copying) can all run in parallel
- T039, T040, T041 (variant testing) can run in parallel

**Within Phase 5 (US3 - Integration)**:

- T043, T044, T045 (copy demos) can run in parallel
- T046, T047, T048 (demo config updates) can run in parallel AFTER copying
- T049, T050, T051 (docker integration) can run in parallel
- T052, T053, T054 (demo READMEs) can run in parallel
- T060, T061, T062 (demo testing) can run in parallel

**Within Phase 6 (Polish)**:

- T064, T065, T066, T070, T071, T072 can all run in parallel

---

## Implementation Strategy

### MVP Scope (Minimum Viable Product)

**Phases to complete for MVP**: Phase 1, Phase 2, Phase 3 (US1 only)

**MVP Delivers**:

- Basic docker-compose.yml configuration
- .env.example with documented variables
- README with Quick Start enabling 10-minute deployment
- Health check script
- OpenID discovery endpoint accessible
- Admin UI accessible

**MVP Success Criteria**: Developer can deploy and verify OIDC provider in 10 minutes

### Incremental Delivery

1. **Release 1 (MVP)**: Phases 1-3 complete
   - Basic deployment working
   - 10-minute Quick Start validated
   - Enables initial evaluation of MrWhoOidc

2. **Release 2 (Production-Ready)**: Add Phase 4
   - Production docker-compose variants
   - Comprehensive documentation
   - Enables serious production consideration

3. **Release 3 (Integration-Complete)**: Add Phase 5
   - Demo applications
   - NuGet package documentation
   - Enables developer ecosystem growth

4. **Release 4 (Polished)**: Add Phase 6
   - Final quality improvements
   - Complete metadata
   - Ready for public announcement

### Task Execution Order

**Sequential phases**: 1 → 2 → (3, 4, 5 in parallel) → 6

**Recommended order for parallel stories**: US1 (P1) first for validation, then US2 (P2) and US3 (P3) together

---

## Summary Statistics

**Total Tasks**: 75

**Tasks by Phase**:

- Phase 1 (Setup): 3 tasks
- Phase 2 (Foundational): 4 tasks
- Phase 3 (US1 - Quick Start): 12 tasks
- Phase 4 (US2 - Production): 23 tasks
- Phase 5 (US3 - Integration): 21 tasks
- Phase 6 (Polish): 12 tasks

**Tasks by User Story**:

- US1 (Quick Start): 12 tasks
- US2 (Production): 23 tasks
- US3 (Integration): 21 tasks
- Setup/Foundational/Polish: 19 tasks

**Parallel Tasks**: 38 tasks marked with [P] can run in parallel with other tasks in their phase

**MVP Task Count**: 19 tasks (Phases 1, 2, 3)

---

## Format Validation

✅ All 75 tasks follow required checklist format: `- [ ] [ID] [P?] [Story?] Description with file path`

✅ Task IDs are sequential: T001 through T075

✅ All user story tasks have [US1], [US2], or [US3] labels

✅ All parallelizable tasks are marked with [P]

✅ All task descriptions include specific file paths in MrWho/ directory

✅ Each phase has clear checkpoints and success criteria
