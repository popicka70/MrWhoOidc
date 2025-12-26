# MrWhoOidc

[![Docker Image](https://img.shields.io/badge/docker-ghcr.io%2Fpopicka70%2Fmrwhooidc-blue)](https://ghcr.io/popicka70/mrwhooidc)
[![Image Size](https://img.shields.io/badge/image%20size-%3C200MB-success)](https://ghcr.io/popicka70/mrwhooidc)
[![Multi-Arch](https://img.shields.io/badge/arch-amd64%20%7C%20arm64-informational)](https://ghcr.io/popicka70/mrwhooidc)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

A production-ready OpenID Connect (OIDC) Provider with OAuth 2.0 support, built on .NET 9 with PostgreSQL and optional Redis caching.

## Quick Start with Docker

Deploy MrWhoOidc in under 10 minutes using Docker Compose:

```bash
# 1. Clone repository
git clone https://github.com/popicka70/MrWhoOidc.git
cd MrWhoOidc

# 2. Create environment configuration
cp .env.example .env
# Edit .env and set POSTGRES_PASSWORD and OIDC_PUBLIC_BASE_URL

# 3. Start services
docker compose up -d

# 4. Verify deployment
curl -k https://localhost:8443/.well-known/openid-configuration
```

**What you get:**
- ✅ Production-optimized Docker image (multi-arch: x64/ARM64)
- ✅ PostgreSQL 16 database with automatic schema migrations
- ✅ Optional Redis caching for high performance (30-50% faster)
- ✅ TLS/HTTPS support with certificate management
- ✅ Multi-tenancy support (optional)
- ✅ Health checks and graceful degradation
- ✅ Comprehensive admin UI at `/admin`

**📖 Complete Documentation:**
- **[Production Setup Guide](docs/production-setup-guide.md)** - Cloud deployment & bootstrap process
- **[Deployment Guide](docs/deployment-guide.md)** - Full deployment lifecycle (1200+ lines)
- **[Configuration Examples](docs/docker-compose-examples.md)** - Production scenarios
- **[Upgrade Guide](docs/upgrade-guide.md)** - Upgrade procedures and rollback
- **[Security Best Practices](docs/docker-security-best-practices.md)** - Hardening guide

## Features

### Core OIDC/OAuth 2.0
- OpenID Connect Provider (OP) with full discovery support
- Authorization Code Flow with PKCE
- Client Credentials Grant
- Token Exchange (RFC 8693) with DPoP support
- Back-Channel Logout (BCL) with durable outbox pattern
- JWT signing with key rotation
- Automatic EF Core migrations

### Enterprise-Ready
- **Multi-Tenancy**: Isolated data per tenant with subdomain/path routing
- **High Performance**: Optional Redis caching (60-80% DB load reduction)
- **Production Hardened**: Non-root containers, read-only volumes, network isolation
- **Observability**: Structured logging, OpenTelemetry, health endpoints
- **Zero-Downtime Upgrades**: Backward-compatible migrations, graceful degradation

### Identity Provider Chaining
- Federated authentication with upstream IdPs
- Multi-level IdP configuration support
- Token exchange for delegated access

## Docker Deployment

### Pull from GitHub Container Registry

```bash
# Pull latest version
docker pull ghcr.io/popicka70/mrwhooidc:latest

# Pull specific version (recommended for production)
docker pull ghcr.io/popicka70/mrwhooidc:v1.0.0
```

### Docker Compose (Recommended)

**Basic deployment with PostgreSQL:**

```yaml
services:
  webauth:
    image: ghcr.io/popicka70/mrwhooidc:latest
    ports:
      - "8443:8443"
    environment:
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      OIDC_PUBLIC_BASE_URL: ${OIDC_PUBLIC_BASE_URL:-https://localhost:8443}
    depends_on:
      postgres:
        condition: service_healthy
    networks:
      - internal
      - edge

  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_USER: oidc
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: authdb
    volumes:
      - postgres-data:/var/lib/postgresql/data
    networks:
      - internal

volumes:
  postgres-data:

networks:
  internal:
  edge:
```

**With Redis for high performance:**

```yaml
services:
  webauth:
    # ... (as above)
    environment:
      REDIS_ENABLED: true
      REDIS_CONNECTION_STRING: redis:6379
    depends_on:
      redis:
        condition: service_healthy
        required: false  # Graceful degradation

  redis:
    image: redis:7.2-alpine
    command: redis-server --save 60 1 --loglevel warning
    volumes:
      - redis-data:/data
    networks:
      - internal
```

### Environment Configuration

Required variables:
- `POSTGRES_PASSWORD`: PostgreSQL database password
- `OIDC_PUBLIC_BASE_URL`: Public URL for OIDC issuer

Optional features:
- `MULTITENANT_ENABLED`: Enable multi-tenancy (true/false)
- `REDIS_ENABLED`: Enable Redis caching (true/false)
- `MAIL_ENABLED`: Enable email/SMTP (true/false)

See [`.env.example`](.env.example) for complete configuration options.

### Production Deployment

For production deployments, see:
- **[Production Setup Guide](docs/production-setup-guide.md)** - Bootstrap process, environment variables, cloud platforms
- **[Production Configuration Guide](docs/deployment-guide.md#production-configuration-checklist)** - 40-item checklist
- **[Production Examples](docs/docker-compose-examples.md)** - Multi-tenancy, custom certs, SMTP, Redis
- **[Security Hardening](docs/docker-security-best-practices.md)** - Network isolation, secrets, TLS
- **[Upgrade Procedures](docs/upgrade-guide.md)** - Zero-downtime upgrades and rollback

> ⚠️ **Important**: In production, the database starts empty. You must call the `/bootstrap` endpoint to create the initial tenant and admin user. See [Production Setup Guide](docs/production-setup-guide.md) for details.

## Documentation

### Quick Links
- **[Production Setup Guide](docs/production-setup-guide.md)** - Cloud deployment & bootstrap
- **[Deployment Guide](docs/deployment-guide.md)** - Complete deployment documentation
- **[Configuration Examples](docs/docker-compose-examples.md)** - Common deployment scenarios
- **[Upgrade Guide](docs/upgrade-guide.md)** - Upgrade and rollback procedures
- **[Security Best Practices](docs/docker-security-best-practices.md)** - Production hardening
- **[Admin Guide](docs/admin-guide.md)** - Administrative operations
- **[Developer Guide](docs/developer-guide.md)** - Development setup

### Protocol Documentation
- **[OBO Client Policy](docs/obo-client-policy.md)** - On-Behalf-Of token exchange
- **[Token Exchange E2E (DPoP)](docs/obo-dpop-requiresamejkt-e2e.md)** - DPoP with RequireSameJkt
- **[IdP Chaining Configuration](docs/idp-chaining-client-configuration.md)** ⚠️ **Important for multi-level IdP setups**

## Examples

### Client Examples
- `.NET` Razor web client: `Examples/MrWhoOidc.RazorClient`
- `.NET` sample API: `Examples/MrWhoOidc.TestApi`
- `Go` web client: `Examples/MrWhoOidc.GoWebClient`
- `Go` sample API: `Examples/MrWhoOidc.GoApi`