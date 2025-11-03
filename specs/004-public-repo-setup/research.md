# Phase 0: Research - Public Repository Setup

**Feature**: Public Repository Setup for MrWhoOidc Distribution  
**Date**: November 2, 2025  
**Status**: Complete

## Research Objectives

1. Identify which documentation files from main solution should be copied to public repository
2. Determine docker-compose configuration variants needed for different deployment scenarios
3. Identify demo applications to include and their readiness state
4. Define NuGet package documentation structure and content
5. Establish README structure and content organization
6. Define environment variable documentation approach

## Decision 1: Documentation File Selection

**Decision**: Copy 8 core deployment/operations documents from main solution `/docs` to public repo `/MrWho/docs`

**Rationale**: 
- The main solution contains 100+ documentation files covering internal development, architecture decisions, and feature backlogs
- Public repository users need only deployment, configuration, operations, and integration documentation
- Selected files provide complete deployment lifecycle coverage without exposing internal development details

**Files to Copy**:
1. `deployment-guide.md` (1200+ lines) - Comprehensive deployment procedures
2. `upgrade-guide.md` - Version migration and rollback procedures
3. `docker-compose-examples.md` - Deployment scenario examples
4. `docker-security-best-practices.md` - Security hardening guide
5. `admin-guide.md` - Admin UI usage and configuration
6. `developer-guide.md` - Integration patterns and API usage
7. `multitenancy-quick-reference.md` - Multi-tenant configuration guide
8. `key-rotation-playbook.md` - Key rotation procedures

**Adaptations Required**:
- Update file paths/links to reflect public repo structure (not main solution structure)
- Remove references to internal development tools (Aspire AppHost)
- Focus docker-compose examples on GHCR images (not local builds)
- Add explicit version compatibility notes

**Alternatives Considered**:
- **Copy all docs**: Rejected - would include internal backlogs, architecture decisions, and development workflows irrelevant to external users
- **Write from scratch**: Rejected - existing docs are comprehensive and tested; unnecessary duplication of effort
- **Link to main repo**: Rejected - forces users to navigate private repository; creates dependency on main repo structure

## Decision 2: Docker Compose Configuration Variants

**Decision**: Create 4 docker-compose variants with specific use cases

**Variants**:

1. **docker-compose.yml** (Basic/Default)
   - PostgreSQL only, no Redis
   - Self-signed TLS certificate for development
   - Single-tenant mode
   - Minimal configuration (~40 environment variables)
   - Target: Quick evaluation and development

2. **docker-compose.redis.yml** (High-Performance)
   - Extends basic with Redis caching
   - Performance improvement: 30-50% faster, 60-80% DB load reduction
   - Same security/TLS as basic
   - Target: Performance testing and medium-scale deployments

3. **docker-compose.production.yml** (Production-Hardened)
   - Includes Redis for performance
   - Multi-tenant mode enabled
   - Security hardening: non-root containers, read-only volumes, network isolation
   - Health checks with dependencies
   - Resource limits defined
   - Target: Production deployments with security requirements

4. **docker-compose.dev.yml** (Development with Email)
   - Extends basic with MailHog for email testing
   - Development logging enabled
   - Hot-reload friendly configuration
   - Target: Local development and testing email flows

**Rationale**:
- Addresses three primary use cases from spec: evaluation (basic), production (hardened), development (dev)
- Redis variant allows performance comparison without full production complexity
- Each variant builds on previous (basic → redis → production) making upgrades clear
- Matches existing docker-compose files in main solution (proven patterns)

**Configuration Documentation**:
- Each file includes 20-30 inline comments explaining configuration choices
- Environment variables grouped by category (Core, TLS, OIDC, Multi-Tenancy, Redis, Email, Logging)
- Comments explain when/why to customize each setting

**Alternatives Considered**:
- **Single docker-compose with profiles**: Rejected - harder to understand, more error-prone for newcomers
- **Kubernetes manifests**: Out of scope per spec; future enhancement
- **More variants (e.g., MySQL, SQL Server)**: Rejected - PostgreSQL is primary supported database per constitution

## Decision 3: Demo Applications Selection

**Decision**: Include 3 demo applications from existing Examples folder with updates

**Selected Demos**:

1. **dotnet-mvc-client** (from Examples/MrWhoOidc.RazorClient)
   - ASP.NET Core MVC application with OIDC authentication
   - Demonstrates: Authorization Code Flow with PKCE, logout, token refresh
   - Status: Ready - copy and update connection settings to use docker-compose IdP

2. **react-client** (from Examples/ReactOidcClient)
   - React SPA with oidc-client-ts library
   - Demonstrates: SPA authentication, silent refresh, logout
   - Status: Ready - update configuration to point to docker-compose IdP

3. **go-client** (from Examples/MrWhoOidc.GoWebClient)
   - Go web application with OIDC
   - Demonstrates: Cross-language integration, token validation
   - Status: Ready - update configuration

**Rationale**:
- Covers three major ecosystems (.NET, JavaScript/React, Go)
- All demonstrate Authorization Code Flow (most common pattern)
- Working examples from main solution - proven to work
- Demonstrates IdP works with multiple client technologies

**Updates Required**:
- Update IdP URL from localhost:8443 to configurable environment variable
- Add docker-compose integration for running demo + IdP together
- Document how to register clients in admin UI
- Add README in each demo with setup instructions

**Alternatives Considered**:
- **Include Go API demo**: Rejected - focuses on resource server, not client integration (less relevant to primary use case)
- **Create new minimal demo**: Rejected - existing demos are complete and proven
- **Include test client**: Rejected - too technical, not representative of real usage

## Decision 4: NuGet Package Documentation Structure

**Decision**: Document 3 core NuGet packages in `/MrWho/packages/README.md` with installation examples

**Packages to Document**:

1. **MrWhoOidc.Client** (when published)
   - Purpose: Client library for .NET applications integrating with MrWhoOidc
   - Installation: `dotnet add package MrWhoOidc.Client`
   - Key features: Token validation, DPoP support, helper methods
   - Example: Basic authentication setup code

2. **MrWhoOidc.Security** (when published)
   - Purpose: Security utilities (DPoP implementation, token helpers)
   - Installation: `dotnet add package MrWhoOidc.Security`
   - Key features: DPoP proof generation/validation, token binding
   - Example: DPoP configuration code

3. **MrWhoOidc.AspNetCore** (future package)
   - Purpose: ASP.NET Core middleware and extensions
   - Installation: `dotnet add package MrWhoOidc.AspNetCore`
   - Status: Placeholder for future package
   - Example: Middleware registration pattern

**Content Structure**:
```markdown
# MrWhoOidc NuGet Packages

## Available Packages
- Table with package name, version, description, NuGet link

## Package: MrWhoOidc.Client
- Installation command
- Key features (3-5 bullet points)
- Basic usage example (15-20 lines of code)
- Link to detailed API docs (when available)

[Repeat for each package]

## Version Compatibility Matrix
| IdP Version | Client Package | Security Package |
|-------------|---------------|------------------|
| 1.0.x       | 1.0.x         | 1.0.x           |
```

**Rationale**:
- Centralizes package information in one discoverable location
- Provides quick-start installation commands
- Code examples enable copy-paste integration
- Compatibility matrix prevents version mismatch issues
- Structure accommodates future packages without reorganization

**Alternatives Considered**:
- **Separate file per package**: Rejected - harder to navigate, overkill for 3-4 packages
- **In main README**: Rejected - would make README too long, poor separation of concerns
- **Wiki pages**: Rejected - less discoverable, requires separate maintenance

## Decision 5: README Structure and Content

**Decision**: Create comprehensive README (~600-800 lines) with 8 major sections

**README Structure**:

1. **Header** (~50 lines)
   - Project title and tagline
   - Badges (Docker image, license, version, multi-arch support)
   - Brief description (2-3 sentences)
   - Key features list (8-10 bullet points)

2. **Quick Start** (~100 lines)
   - 4-step deployment process
   - Prerequisites listed explicitly
   - Copy-paste commands
   - Verification steps
   - Expected outcome with screenshot or curl example
   - Links to detailed deployment guide

3. **Features** (~80 lines)
   - Core OIDC/OAuth 2.0 features
   - Enterprise features (multi-tenancy, high performance, observability)
   - Identity provider chaining
   - Each with brief explanation (1-2 sentences)

4. **Docker Deployment** (~150 lines)
   - Pull from GHCR instructions
   - Docker Compose variants explained
   - Basic deployment YAML example
   - High-performance deployment example
   - Environment variable highlights (10-15 most important)
   - Links to complete examples

5. **Documentation** (~40 lines)
   - Table of contents with links to /docs files
   - Quick links to most common tasks (deployment, upgrade, troubleshooting)
   - Link to configuration reference

6. **Integration & Demos** (~60 lines)
   - Demo applications overview
   - NuGet packages section with installation
   - Link to /demos and /packages directories
   - Code snippet for basic client setup

7. **Troubleshooting** (~80 lines)
   - 5-7 most common deployment issues
   - Each with: Problem description, Cause, Solution
   - Link to comprehensive troubleshooting guide

8. **Contributing & License** (~40 lines)
   - How to report issues
   - How to contribute
   - License information
   - Links to community/support resources

**Rationale**:
- Prioritizes Quick Start at top (addresses P1 user story)
- Progressive disclosure: README → detailed guides
- Matches GitHub repository best practices
- Searchable/navigable in < 30 seconds (per spec constraint)
- Comprehensive without overwhelming (600-800 lines vs 2000+ line deployment guide)

**Alternatives Considered**:
- **Minimal README with links**: Rejected - doesn't meet 10-minute deployment goal (too much clicking around)
- **All-in-one README**: Rejected - would be 2000+ lines, hard to navigate
- **Separate files for each section**: Rejected - users expect README to be self-contained entry point

## Decision 6: Environment Variable Documentation Approach

**Decision**: Three-tier documentation strategy for environment variables

**Tier 1: Inline in docker-compose files** (~30 most critical variables)
- Format: `# [REQUIRED/OPTIONAL] Description of what it does and when to change it`
- Example: `POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}  # [REQUIRED] Database password - CHANGE IN PRODUCTION`
- Covers: Core, TLS, OIDC public URL, multi-tenancy enable/disable

**Tier 2: .env.example file** (~60 variables)
- Format:
  ```bash
  # === Core Configuration ===
  # PostgreSQL database password (REQUIRED)
  # Security: Use strong password in production (min 16 chars, mixed case, numbers, symbols)
  POSTGRES_PASSWORD=changeme_in_production
  
  # Public base URL where IdP is accessible (REQUIRED)
  # Example: https://auth.yourdomain.com or https://localhost:8443 for dev
  OIDC_PUBLIC_BASE_URL=https://localhost:8443
  ```
- Groups: Core, TLS/Certificates, OIDC, Multi-Tenancy, Redis, Email/SMTP, Logging
- Each variable: description, security notes, example values, when to customize

**Tier 3: Complete reference in /docs/configuration-reference.md** (100+ variables)
- Format: Table with columns: Variable, Type, Default, Required, Description, Example
- Includes: All possible configuration options, advanced settings, feature flags
- Cross-referenced from docker-compose and .env.example

**Rationale**:
- Tier 1 ensures users see critical config at point of use
- Tier 2 provides copy-paste starting point with explanations
- Tier 3 serves as comprehensive reference for advanced users
- Meets spec requirement: "90% of required env vars have inline documentation"
- Follows principle of progressive disclosure

**Alternatives Considered**:
- **All documentation in README**: Rejected - would make README 1500+ lines
- **Only .env.example**: Rejected - users must switch files while editing docker-compose
- **External wiki**: Rejected - less discoverable, separate maintenance burden

## Decision 7: File Copying and Adaptation Strategy

**Decision**: Copy-and-adapt workflow with systematic link and path updates

**Process**:
1. **Copy phase**: Copy selected files from main solution to /MrWho structure
2. **Adaptation phase**: Batch update all files for:
   - Path references: `MrWhoOidc.Auth/` → `(reference main solution)`
   - Links: `docs/backlog.md` → Remove or update to public docs
   - Docker references: `build: .` → `image: ghcr.io/popicka70/mrwhooidc:latest`
   - Aspire references: Remove or replace with docker-compose equivalents
   - Version references: Add explicit version numbers (not "latest" in docs)

**Link Update Rules**:
- Internal links to public docs: Update paths
- Internal links to non-public docs: Remove or add note "See main development docs"
- External links (RFCs, Docker Hub, etc.): Keep as-is
- Code repository links: Update to point to public repo once created

**Validation**:
- Run markdown link checker on all files
- Verify all docker-compose files with `docker-compose config`
- Test README Quick Start end-to-end
- Verify all code examples compile/run

**Rationale**:
- Preserves existing quality and completeness of documentation
- Systematic approach prevents broken links and outdated references
- Validation ensures repository is immediately usable

## Decision 8: Demo Integration with Docker Compose

**Decision**: Each demo includes docker-compose override file for integrated testing

**Pattern**:
```yaml
# demos/dotnet-mvc-client/docker-compose.demo.yml
version: '3.8'
services:
  demo-client:
    build: .
    environment:
      OIDC_AUTHORITY: https://webauth:8443
      OIDC_CLIENT_ID: demo-mvc-client
      OIDC_CLIENT_SECRET: ${CLIENT_SECRET}
    depends_on:
      - webauth
    networks:
      - edge

# Usage: docker-compose -f ../../docker-compose.yml -f docker-compose.demo.yml up
```

**Benefits**:
- Users can run IdP + demo together with one command
- Demonstrates real integration patterns
- Network configuration handled correctly (container-to-container)
- Easy to stop/start for testing

**Demo Documentation**:
Each demo includes README with:
1. Prerequisites (Docker, optionally .NET/Node/Go SDK for local development)
2. Quick run with docker-compose (no SDK needed)
3. Local development setup (with SDK, debugging)
4. Client registration steps in admin UI
5. Expected behavior and screenshots

**Rationale**:
- Lowers barrier to trying demos (no SDK installation required)
- Demonstrates docker-compose extensibility pattern
- Matches production deployment model (containerized)

## Implementation Priority

Based on spec priorities and dependencies:

**Phase 1 (P1 - Quick Start)**:
1. Create docker-compose.yml (basic)
2. Create .env.example with critical variables
3. Create README with Quick Start section
4. Create health-check.sh script
5. Validate deployment end-to-end

**Phase 2 (P2 - Production)**:
1. Create remaining docker-compose variants (redis, production, dev)
2. Copy and adapt deployment documentation
3. Create docs/configuration-reference.md
4. Create docs/troubleshooting.md
5. Validate all docker-compose configs

**Phase 3 (P3 - Integration)**:
1. Copy and adapt demo applications
2. Create demo docker-compose integration
3. Create packages/README.md with NuGet info
4. Create packages/integration-examples.md
5. Validate demos work with deployed IdP

## Risk Mitigation

**Risk 1: Broken links in copied documentation**
- Mitigation: Automated link checker + manual validation
- Impact: High (poor user experience)
- Probability: Medium (many internal links in docs)

**Risk 2: Docker compose configurations don't work**
- Mitigation: Test each variant end-to-end; add automated validation
- Impact: High (blocks deployment)
- Probability: Low (based on existing working configs)

**Risk 3: Version compatibility issues**
- Mitigation: Explicit version matrix in README and packages docs
- Impact: Medium (users deploy incompatible versions)
- Probability: Medium (multiple moving parts)

**Risk 4: Documentation staleness**
- Mitigation: Add "Last updated" dates; document sync process with main repo
- Impact: Medium (outdated info leads to support burden)
- Probability: High (main solution evolves)

## Success Metrics Validation

Mapping research decisions to spec success criteria:

- **SC-001** (10-minute deployment): Quick Start section with 4-step process enables this
- **SC-002** (90% env var documentation): Three-tier documentation strategy covers this
- **SC-003** (3 docker-compose configs): Four variants exceeds requirement
- **SC-004** (100% deployment scenario coverage): Eight copied docs cover all scenarios
- **SC-005** (Working demo app): Three demos exceed requirement
- **SC-006** (docker-compose validation): Automated validation planned
- **SC-007** (5 troubleshooting issues): Troubleshooting.md + README section covers this
- **SC-008** (80% doc mirroring): Eight core docs represent ~80% of deployment-relevant content
- **SC-009** (NuGet package docs): packages/README.md with examples covers this

## Next Steps

Phase 1 (Design & Contracts) will produce:
1. **quickstart.md**: Detailed walkthrough script for 10-minute deployment test
2. **data-model.md**: N/A for this feature (no data model)
3. **contracts/**: N/A for this feature (no API contracts)

Phase 2 (Tasks) will break down implementation into trackable work items following priority order above.
