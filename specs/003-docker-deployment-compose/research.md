# Phase 0: Research & Technical Decisions

**Feature**: Docker Deployment Package  
**Date**: 2025-11-01  
**Status**: Complete

## Overview

This document consolidates research findings and technical decisions for deploying MrWhoOidc as a public Docker image with production-ready Docker Compose configuration.

## Research Areas

### 1. Docker Image Optimization for .NET 9

**Decision**: Use multi-stage Dockerfile with Microsoft's official .NET 9 runtime images

**Rationale**:

- Microsoft provides optimized runtime images: `mcr.microsoft.com/dotnet/aspnet:9.0`
- Multi-stage build separates build dependencies from runtime, reducing final image size by 70-80%
- Alpine Linux variant available for even smaller footprint (but may have compatibility issues)
- Ubuntu Chiseled images offer minimal attack surface with no shell or package manager
- Target: <200MB compressed (achievable with chiseled or alpine-based runtime)

**Alternatives Considered**:

- Single-stage build: Rejected - includes SDK (~900MB) in final image
- Self-contained deployment: Rejected - larger image size, harder to patch runtime vulnerabilities
- Distroless images: Considered but Microsoft Chiseled images are the .NET-optimized equivalent

**References**:

- [.NET Docker images](https://hub.docker.com/_/microsoft-dotnet-aspnet/)
- [Chiseled Ubuntu images](https://devblogs.microsoft.com/dotnet/announcing-dotnet-chiseled-containers/)

### 2. Container Registry Selection

**Decision**: GitHub Container Registry (ghcr.io)

**Rationale**:

- Free for public open-source repositories
- Native GitHub Actions integration (uses `GITHUB_TOKEN`, no separate credentials)
- Supports multi-architecture images (x64, ARM64) via manifest lists
- Automatically linked to repository for discoverability
- 500MB-10GB storage per package (sufficient for our image size target)
- Mature and reliable (GitHub-operated infrastructure)

**Alternatives Considered**:

- Docker Hub: Free tier limits (200 container pulls per 6 hours for anonymous users), requires separate account
- Quay.io: Good option but requires separate account and CI integration
- AWS ECR Public: Considered but adds AWS dependency and more complex setup

**Registry URL Format**: `ghcr.io/popicka70/mrwhooidc:tag`

**References**:

- [GitHub Container Registry docs](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry)

### 3. Image Tagging Strategy

**Decision**: Semantic versioning with multiple tag formats

**Tagging Convention**:

- `latest` - Always points to the most recent stable release
- `v1.2.3` - Full semantic version (immutable, specific release)
- `v1.2` - Minor version pointer (updates with patches)
- `v1` - Major version pointer (updates with minor/patches)
- `main` - Bleeding edge from main branch (development/testing only)
- `sha-<commit>` - Specific commit (for rollback scenarios)

**Rationale**:

- Follows Docker community best practices
- Enables pinned deployments (v1.2.3) or automatic patch updates (v1.2)
- `latest` provides simple getting-started experience
- Commit-based tags support exact reproducibility

**Alternatives Considered**:

- Date-based tags (YYYYMMDD): Rejected - not semantic, harder to understand relationships
- `stable`/`edge` only: Rejected - lacks version granularity for upgrades

**References**:

- [Docker tagging best practices](https://docs.docker.com/develop/dev-best-practices/)
- [Semantic Versioning 2.0](https://semver.org/)

### 4. Multi-Architecture Build

**Decision**: Support x64 (amd64) and ARM64 (aarch64) via Docker buildx

**Rationale**:

- x64: Standard for most cloud VMs and on-premise servers
- ARM64: Growing adoption (AWS Graviton, Apple Silicon, Raspberry Pi for edge deployments)
- GitHub Actions supports buildx with QEMU emulation for cross-platform builds
- Manifest lists automatically route to correct architecture per client
- Marginal CI time increase (~2x build time) for significant user benefit

**Build Configuration**:

```yaml
platforms: linux/amd64,linux/arm64
```

**Alternatives Considered**:

- x64 only: Rejected - excludes growing ARM64 user base
- Native ARM64 runners: Considered but adds cost; QEMU emulation sufficient for build step

**References**:

- [Docker buildx multi-platform builds](https://docs.docker.com/build/building/multi-platform/)
- [GitHub Actions buildx action](https://github.com/docker/build-push-action)

### 5. Health Check Strategy

**Decision**: Implement HTTP health checks for all services with dependency ordering

**Health Check Configuration**:

- **PostgreSQL**: Use `pg_isready` command (interval: 10s, retries: 5, start period: 10s)
- **Redis**: Use `redis-cli ping` command (interval: 10s, retries: 5)
- **MrWhoOidc.WebAuth**: HTTP GET to `/health` endpoint (interval: 30s, timeout: 3s, retries: 3)
  - Should check: DB connectivity, Redis connectivity (if enabled), key material loaded

**Rationale**:

- Enables `depends_on` with `condition: service_healthy` for proper startup ordering
- Prevents race conditions where app tries to connect before DB is ready
- Supports orchestration systems (Docker Swarm, Kubernetes) for automated recovery
- Health endpoint should be unauthenticated but lightweight

**Alternatives Considered**:

- No health checks: Rejected - leads to startup race conditions and poor UX
- TCP socket checks only: Rejected - doesn't validate service readiness (e.g., DB accepting connections != migrations complete)

**References**:

- [Docker Compose health checks](https://docs.docker.com/compose/compose-file/compose-file-v3/#healthcheck)
- [ASP.NET Core health checks](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks)

### 6. Environment Variable Configuration Strategy

**Decision**: Comprehensive environment variable support with `.env` file examples

**Configuration Approach**:

- Required variables: Database connection, Redis connection (if enabled), public base URL
- Optional variables: Multi-tenancy, SMTP, logging level, feature flags
- Provide `.env.example` with all options documented
- Use Docker Compose variable substitution: `${VARIABLE:-default_value}`
- Sensitive values (DB password, secrets): Never hardcoded, always environment variables

**Key Variables**:

```bash
# Required
POSTGRES_PASSWORD=<strong-password>
OIDC_PUBLIC_BASE_URL=https://your-domain.com

# Optional
REDIS_ENABLED=true
MULTITENANT_ENABLED=true
SMTP_HOST=smtp.example.com
```

**Rationale**:

- 12-factor app principles (config via environment)
- Prevents secrets in version control
- Supports different environments (dev/staging/prod) from same compose file
- `.env` file keeps local config clean and documented

**Alternatives Considered**:

- Docker secrets: Considered but requires Swarm mode, overkill for single-instance deployment
- Config files in volumes: Rejected - environment variables are more Docker-native and easier to orchestrate

**References**:

- [12-Factor App Config](https://12factor.net/config)
- [Docker Compose environment variables](https://docs.docker.com/compose/environment-variables/)

### 7. Volume Strategy for Data Persistence

**Decision**: Named volumes for PostgreSQL and Redis data with backup documentation

**Volume Configuration**:

```yaml
volumes:
  postgres-data:
    driver: local
  redis-data:
    driver: local
```

**Rationale**:

- Named volumes are managed by Docker, survive container deletion
- Portable across Docker hosts with backup/restore procedures
- Better performance than bind mounts for database workloads
- Clear separation between data and configuration

**Backup Strategy** (documented, not automated):

```bash
# Backup PostgreSQL
docker exec mrwhooidc-postgres pg_dump -U oidc authdb > backup.sql

# Restore PostgreSQL
cat backup.sql | docker exec -i mrwhooidc-postgres psql -U oidc authdb
```

**Alternatives Considered**:

- Bind mounts: Rejected - requires manual directory creation, permission issues, less portable
- External volume drivers (NFS, cloud storage): Out of scope - users can configure if needed
- Automated backups: Out of scope - should be handled by user's infrastructure

**References**:

- [Docker volumes](https://docs.docker.com/storage/volumes/)
- [PostgreSQL backup best practices](https://www.postgresql.org/docs/current/backup.html)

### 8. Network Isolation Strategy

**Decision**: Two Docker networks - internal (database/cache) and edge (public services)

**Network Configuration**:

```yaml
networks:
  internal:
    driver: bridge
    internal: true  # No external internet access
  edge:
    driver: bridge   # External access allowed
```

**Service Assignment**:

- **internal only**: PostgreSQL, Redis (database tier)
- **both networks**: MrWhoOidc.WebAuth (needs DB access + external connectivity)
- **edge only**: (future) reverse proxy, monitoring exporters

**Rationale**:

- Defense in depth: Database cannot be directly accessed from outside or make outbound connections
- Limits attack surface if application is compromised
- Follows container security best practices (principle of least privilege)
- No performance overhead (both bridge networks on same host)

**Alternatives Considered**:

- Single network: Rejected - no isolation between tiers
- Host network: Rejected - bypasses Docker networking isolation
- Overlay network: Overkill for single-host deployment

**References**:

- [Docker network security](https://docs.docker.com/network/network-tutorial-overlay/#use-a-user-defined-bridge-network)
- [OWASP Docker Security](https://cheatsheetseries.owasp.org/cheatsheets/Docker_Security_Cheat_Sheet.html)

### 9. TLS Certificate Management

**Decision**: Volume-mounted certificates with clear documentation for certificate provisioning

**Approach**:

- Certificates provided by user (not generated by container)
- Mounted via read-only volume: `./certs:/https:ro`
- Support for PFX format (standard for .NET) with password via environment variable
- Development: Self-signed cert included in repo (for local testing only)
- Production: Users must provide valid certificates (Let's Encrypt, commercial CA, internal CA)

**Configuration**:

```yaml
environment:
  ASPNETCORE_Kestrel__Certificates__Default__Path: /https/aspnetapp.pfx
  ASPNETCORE_Kestrel__Certificates__Default__Password: ${CERT_PASSWORD}
volumes:
  - ./certs:/https:ro
```

**Rationale**:

- Flexibility: Users can use any certificate source (Let's Encrypt, commercial CA, corporate PKI)
- Security: No auto-generation prevents insecure default certificates in production
- Simplicity: No complex ACME client integration in container
- Standard practice: Most deployments behind reverse proxy handle TLS there anyway

**Alternatives Considered**:

- Automatic Let's Encrypt: Rejected - adds complexity, requires port 80/443 access, renewal challenges
- Generate self-signed on startup: Rejected - insecure for production, browser warnings
- No TLS (HTTP only): Rejected - OIDC spec requires HTTPS for production

**Documentation Requirements**:

- How to generate self-signed cert for testing
- How to use Let's Encrypt with Certbot + manual renewal
- How to deploy behind reverse proxy (recommended for production)

**References**:

- [ASP.NET Core HTTPS configuration](https://learn.microsoft.com/en-us/aspnet/core/security/docker-https)
- [Let's Encrypt](https://letsencrypt.org/)

### 10. Database Migration Strategy

**Decision**: Automatic migrations on application startup with idempotency

**Implementation**:

- EF Core migrations applied during `WebAuth` startup (before Kestrel starts)
- Idempotent: Safe to run multiple times, only applies missing migrations
- Startup failure if migrations fail (fail-fast principle)
- Logs migration activity for troubleshooting

**Rationale**:

- Zero-config deployment: No separate migration step required
- Idempotent operations prevent issues with container restarts
- Fail-fast: Better to fail startup than run with wrong schema
- Follows existing MrWhoOidc pattern (Aspire development already does this)

**Startup Sequence**:

1. Container starts
2. App reads connection string from environment
3. EF Core applies pending migrations
4. App performs health checks (DB connectivity, key material)
5. Kestrel starts accepting HTTP requests

**Rollback Strategy**:

- For upgrades: Stop container, restore previous image version, restore DB backup if needed
- Migrations are forward-only; rollback requires DB restore from backup

**Alternatives Considered**:

- Separate migration container: Rejected - adds complexity, coordination challenges
- Manual migration step: Rejected - poor UX, error-prone
- Schema-less deployment: N/A - relational DB requires schema

**References**:

- [EF Core migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/)
- [Database migration patterns](https://www.martinfowler.com/articles/evodb.html)

### 11. Redis Optional Integration Pattern

**Decision**: Graceful degradation when Redis unavailable (not fail-fast)

**Implementation**:

- Redis connection configurable via environment: `REDIS_ENABLED=true|false`
- If enabled but unavailable: Log warnings, continue without cache (slower but functional)
- Use `abortConnect=false` in connection string to prevent startup failure
- Circuit breaker pattern for Redis operations (fast-fail after N errors)

**Rationale**:

- Redis is performance optimization, not functional requirement
- Prevents Redis outage from causing OIDC server downtime
- Users can start with PostgreSQL only, add Redis later for performance
- Follows resilience engineering principles (graceful degradation)

**Implementation Details**:

```csharp
// Pseudo-code for cache access
try {
    var cached = await redis.GetAsync(key);
    if (cached != null) return cached;
} catch (RedisException) {
    _logger.LogWarning("Redis unavailable, falling back to database");
}
// Continue with database query
```

**Alternatives Considered**:

- Redis required: Rejected - increases deployment complexity, reduces availability
- No Redis support: Rejected - limits scalability for high-traffic deployments
- In-memory cache only: Considered but doesn't support distributed scenarios

**References**:

- [StackExchange.Redis configuration](https://stackexchange.github.io/StackExchange.Redis/Configuration)
- [Circuit breaker pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/circuit-breaker)

### 12. GitHub Actions CI/CD Workflow

**Decision**: Automated build and publish on Git tags with branch-based testing

**Workflow Triggers**:

- **On push to main**: Build and tag as `main` (development/testing)
- **On Git tag** `v*`: Build and tag as `latest`, `vX.Y.Z`, `vX.Y`, `vX`
- **On pull request**: Build only (no push) to validate Dockerfile

**Workflow Steps**:

1. Checkout code
2. Set up Docker Buildx (multi-platform support)
3. Log in to GHCR using `GITHUB_TOKEN`
4. Extract metadata (tags, labels) using `docker/metadata-action`
5. Build and push image to GHCR with all applicable tags
6. Sign image with Sigstore/cosign (optional but recommended)

**Rationale**:

- Automation reduces human error and ensures consistency
- Tag-based releases follow GitFlow/trunk-based development practices
- PR builds validate changes without publishing
- Multi-platform builds ensure ARM64 support
- Image signing provides supply chain security (verify authenticity)

**Alternatives Considered**:

- Manual builds: Rejected - error-prone, not reproducible
- Build on every commit: Rejected - too noisy, `main` tag sufficient for development
- Separate workflow for each architecture: Rejected - buildx handles multi-platform elegantly

**References**:

- [GitHub Actions for Docker](https://docs.docker.com/build/ci/github-actions/)
- [docker/build-push-action](https://github.com/docker/build-push-action)
- [docker/metadata-action](https://github.com/docker/metadata-action)

## Summary of Decisions

| Area | Decision | Key Benefit |
|------|----------|-------------|
| Base Image | .NET 9 ASP.NET runtime (chiseled/alpine) | <200MB image size |
| Registry | GitHub Container Registry (ghcr.io) | Free, native GitHub integration |
| Tagging | Semantic versioning + `latest` | Flexible versioning strategy |
| Architecture | Multi-arch (x64, ARM64) | Broad platform support |
| Health Checks | HTTP + DB connectivity checks | Reliable startup ordering |
| Configuration | Environment variables + `.env` example | 12-factor compliance |
| Persistence | Named Docker volumes | Data durability, portability |
| Networking | Internal + edge networks | Security isolation |
| TLS | Volume-mounted certificates | Flexibility, security |
| Migrations | Automatic on startup | Zero-config deployment |
| Redis | Optional with graceful degradation | High availability |
| CI/CD | GitHub Actions with buildx | Automated, reproducible builds |

## Next Steps (Phase 1)

1. Create optimized Dockerfile with multi-stage build
2. Update docker-compose.yml with production configuration
3. Create GitHub Actions workflow (docker-publish.yml)
4. Write deployment documentation (deployment-guide.md)
5. Create example configurations (docker-compose-examples.md)
6. Add Docker validation tests to MrWhoOidc.UnitTests
