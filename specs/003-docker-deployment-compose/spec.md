# Feature Specification: Docker Deployment Package

**Feature Branch**: `003-docker-deployment-compose`  
**Created**: 2025-11-01  
**Status**: Draft  
**Input**: User description: "I'd like to deploy our OIDC server image to public docker repo. I want to create an initial docker compose to install the server with postgres database and optionally with redis."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Deploy OIDC Server with PostgreSQL (Priority: P1)

An operations engineer needs to deploy the MrWhoOidc OIDC server to a production or staging environment. They pull the public Docker image and use the provided Docker Compose file to launch the server with a PostgreSQL database for persistence.

**Why this priority**: This is the minimum viable deployment - the OIDC server requires a database to function, making PostgreSQL + OIDC server the essential core deployment scenario.

**Independent Test**: Can be fully tested by running `docker compose up` with the PostgreSQL configuration and verifying the OIDC discovery endpoint returns valid metadata. Delivers a functional OIDC server capable of handling authentication flows.

**Acceptance Scenarios**:

1. **Given** the Docker image is published to a public repository, **When** an engineer runs `docker compose up` with the base configuration, **Then** the OIDC server starts successfully and connects to PostgreSQL
2. **Given** the OIDC server is running, **When** accessing the discovery endpoint (`.well-known/openid-configuration`), **Then** valid OIDC metadata is returned
3. **Given** the OIDC server is running, **When** the PostgreSQL container restarts, **Then** the OIDC server reconnects automatically without data loss
4. **Given** a clean environment, **When** running the deployment for the first time, **Then** the database schema is automatically initialized

---

### User Story 2 - Deploy with Redis for Performance (Priority: P2)

An operations engineer wants to add Redis caching to improve performance in a high-traffic production environment. They enable Redis in the Docker Compose configuration to benefit from session caching and distributed cache capabilities.

**Why this priority**: Redis is optional but valuable for production deployments. It enhances performance but is not required for core functionality, making it a P2 enhancement.

**Independent Test**: Can be tested by enabling Redis in the compose file and verifying cache hit metrics or observing improved response times for repeated requests. Delivers performance optimization as a measurable benefit.

**Acceptance Scenarios**:

1. **Given** the base deployment is running, **When** Redis is enabled in the configuration, **Then** the OIDC server connects to Redis and uses it for caching
2. **Given** Redis is enabled, **When** the Redis container becomes unavailable, **Then** the OIDC server continues to function with degraded performance (graceful fallback)
3. **Given** Redis is enabled, **When** configuration is provided for Redis persistence, **Then** cached data survives Redis container restarts

---

### User Story 3 - Configure Environment for Production (Priority: P1)

An operations engineer needs to configure the OIDC server for their specific environment, including base URLs, database credentials, TLS certificates, and tenant settings. They customize the Docker Compose environment variables and volume mounts.

**Why this priority**: Production deployments require environment-specific configuration. Without proper configuration, the OIDC server cannot be used in real-world scenarios, making this equally critical as P1.

**Independent Test**: Can be tested by providing custom configuration values and verifying the server uses them (checking discovery endpoint for correct issuer URL, testing database connectivity with custom credentials, verifying TLS certificate usage).

**Acceptance Scenarios**:

1. **Given** custom environment variables are provided, **When** the deployment starts, **Then** the OIDC server uses the provided configuration values
2. **Given** custom TLS certificates are mounted, **When** the server starts, **Then** HTTPS connections use the provided certificates
3. **Given** database credentials are configured, **When** the deployment starts, **Then** the server connects using the provided credentials
4. **Given** invalid configuration is provided, **When** the deployment starts, **Then** clear error messages indicate which configuration values are incorrect

---

### User Story 4 - Pull Image from Public Registry (Priority: P1)

A DevOps engineer discovers MrWhoOidc and wants to evaluate or deploy it. They find the public Docker image in a registry (Docker Hub or GitHub Container Registry), pull it, and deploy using the documented compose file without needing to build from source.

**Why this priority**: Public image availability is the core requirement stated by the user. Without this, users must build from source, which defeats the purpose of this feature.

**Independent Test**: Can be tested by removing all source code from the test environment and successfully deploying using only the public image reference and compose file. Delivers immediate deployment capability without build tools.

**Acceptance Scenarios**:

1. **Given** the image is published to a public registry, **When** a user references the image in docker compose, **Then** the image is pulled successfully without authentication
2. **Given** a new version is released, **When** the image tag is updated in the compose file, **Then** users can pull and deploy the new version
3. **Given** the compose file is downloaded, **When** examining the image reference, **Then** it clearly indicates the registry location and tag convention

---

### User Story 5 - Upgrade Deployment (Priority: P2)

An operations engineer needs to upgrade their running OIDC server deployment to a new version. They update the image tag, run the deployment command, and the system handles database migrations automatically.

**Why this priority**: Upgrades are important for ongoing operations but secondary to initial deployment. This is a P2 operational concern.

**Independent Test**: Can be tested by deploying version N, then updating to version N+1 with schema changes, and verifying data persistence and automatic migration execution.

**Acceptance Scenarios**:

1. **Given** an existing deployment is running, **When** the image tag is updated to a newer version, **Then** the new version starts and database migrations run automatically
2. **Given** database migrations are required, **When** the upgrade occurs, **Then** existing data is preserved and the schema is updated correctly
3. **Given** an upgrade fails, **When** attempting to start the new version, **Then** clear error messages indicate the failure reason and data remains intact

---

### Edge Cases

- What happens when PostgreSQL connection fails during startup? (Server should retry with exponential backoff and provide clear error messages)
- What happens when Redis is configured but unavailable? (Server should start with degraded performance and log warnings, not fail completely)
- What happens when running the compose file without TLS certificates? (Server should either generate self-signed certificates for dev or fail with clear instructions for production)
- What happens when multiple replicas of the OIDC server are started? (Should work correctly with shared PostgreSQL and Redis, handling distributed sessions)
- What happens when the database already exists but the schema is outdated? (Migrations should run automatically on startup)
- What happens when disk space is exhausted for database volumes? (Clear error messages indicating storage issues)

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST provide a public Docker image hosted on a well-known container registry (Docker Hub or GitHub Container Registry)
- **FR-002**: System MUST provide a Docker Compose file that deploys the OIDC server with PostgreSQL database
- **FR-003**: System MUST support optional Redis integration via Docker Compose configuration
- **FR-004**: Docker Compose file MUST include health checks for PostgreSQL and Redis containers
- **FR-005**: System MUST document all required environment variables for production deployment
- **FR-006**: System MUST document optional environment variables for Redis, multi-tenancy, and mail configuration
- **FR-007**: System MUST support custom TLS certificate mounting via Docker volumes
- **FR-008**: System MUST automatically apply database migrations on startup
- **FR-009**: Docker Compose file MUST use named volumes for PostgreSQL and Redis data persistence
- **FR-010**: System MUST gracefully handle Redis unavailability when Redis is enabled (degraded mode, not failure)
- **FR-011**: Docker Compose file MUST support both development and production deployment scenarios
- **FR-012**: System MUST document image tagging strategy (latest, semantic versioning, stable)
- **FR-013**: System MUST provide example configuration for common deployment scenarios (single-tenant, multi-tenant, with/without Redis)
- **FR-014**: Docker image MUST be multi-architecture (support both x64 and ARM64 architectures)
- **FR-015**: System MUST document minimum resource requirements (CPU, memory, storage) for each service
- **FR-016**: System MUST provide network configuration that isolates database traffic from public access
- **FR-017**: Docker Compose file MUST include restart policies for production resilience
- **FR-018**: System MUST document backup and restore procedures for PostgreSQL data volumes

### Key Entities

- **Docker Image**: Published container image containing the compiled MrWhoOidc OIDC server, available on public registry with versioned tags
- **Docker Compose Configuration**: YAML file defining services (webauth, postgres, redis), networks, volumes, and environment variables
- **PostgreSQL Database**: Persistent data store for OIDC entities (clients, users, sessions, consents, keys)
- **Redis Cache**: Optional distributed cache for session data and performance optimization
- **TLS Certificates**: Security credentials for HTTPS connections, mountable via volumes
- **Environment Configuration**: Set of variables defining deployment-specific settings (URLs, credentials, features)
- **Data Volumes**: Persistent storage for PostgreSQL data and Redis snapshots

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Operations engineers can deploy a functional OIDC server from scratch in under 10 minutes using only the compose file and public image
- **SC-002**: The deployment handles 1,000 concurrent authentication requests without performance degradation when Redis is enabled
- **SC-003**: Database migrations execute successfully during startup without manual intervention in 100% of version upgrades
- **SC-004**: Redis failure results in degraded performance but not service outage (99% of requests still succeed)
- **SC-005**: The Docker image size is under 200MB (compressed) for efficient distribution and storage
- **SC-006**: Clear documentation allows 90% of users to complete their first deployment without external support
- **SC-007**: Deployment with PostgreSQL only uses less than 512MB combined memory under normal load
- **SC-008**: Deployment with Redis enabled uses less than 768MB combined memory under normal load
- **SC-009**: Health checks detect service failures within 30 seconds and trigger automatic restarts
- **SC-010**: 95% of users can successfully customize environment variables for their production environment on first attempt

## Assumptions *(mandatory)*

- **A-001**: Users deploying the OIDC server have basic Docker and Docker Compose knowledge
- **A-002**: Target deployment environments have outbound internet access to pull images from public registries
- **A-003**: The OIDC server will be deployed behind a reverse proxy (nginx, Traefik) in production for advanced TLS termination and load balancing
- **A-004**: Standard PostgreSQL 16 is sufficient for database requirements (no special extensions needed)
- **A-005**: Redis 7.2 provides adequate caching capabilities for the OIDC server
- **A-006**: The current docker-compose.yml in the repository serves as a starting point for the production-ready version
- **A-007**: Users deploying to production will provide their own valid TLS certificates (not self-signed)
- **A-008**: The Docker Compose file will be versioned alongside the Docker image to ensure compatibility
- **A-009**: Container registry will be GitHub Container Registry (ghcr.io) as it's free for public open-source projects and integrates with GitHub Actions
- **A-010**: Docker Compose V2 (compose plugin) syntax will be used, as V1 (docker-compose) is deprecated
- **A-011**: The OIDC server requires no special Linux capabilities or privileged mode
- **A-012**: Deployment documentation will be provided in the repository README and in a dedicated deployment guide

## Out of Scope

- **OOS-001**: Kubernetes deployment manifests (Helm charts, kustomize) - future consideration
- **OOS-002**: Automated database backup scheduling - users must implement their own backup strategy
- **OOS-003**: Monitoring and alerting setup (Prometheus, Grafana) - separate infrastructure concern
- **OOS-004**: Multi-node PostgreSQL clustering or replication - single-instance deployment only
- **OOS-005**: Redis Cluster or Sentinel configuration - single-instance Redis only
- **OOS-006**: Automated TLS certificate generation/renewal (Let's Encrypt) - users provide certificates
- **OOS-007**: Cloud-specific deployment scripts (AWS ECS, Azure Container Instances, GCP Cloud Run)
- **OOS-008**: Secrets management integration (HashiCorp Vault, AWS Secrets Manager)
- **OOS-009**: Log aggregation configuration (ELK, Splunk)
- **OOS-010**: CI/CD pipeline setup for automated deployments - users implement their own pipelines

## Dependencies

- **D-001**: Docker image build and push workflow must be configured in GitHub Actions
- **D-002**: GitHub Container Registry authentication and permissions must be configured
- **D-003**: Dockerfile must be optimized for production (multi-stage build, minimal base image)
- **D-004**: Database migration system must support automatic execution on startup
- **D-005**: OIDC server must support configuration via environment variables for all deployment-critical settings
- **D-006**: Documentation must be updated with deployment instructions before image publication
- **D-007**: Version tagging strategy must be established and documented

## Constraints

- **C-001**: Docker image must not contain any secrets, API keys, or sensitive configuration
- **C-002**: Docker Compose file must not hardcode production credentials (use environment variable placeholders)
- **C-003**: Image must comply with container security best practices (non-root user, minimal attack surface)
- **C-004**: Licensing information must be clearly indicated in image labels and documentation
- **C-005**: Image publication must follow semantic versioning for predictable upgrades
- **C-006**: Docker Compose file must be compatible with Docker Engine 20.10+ and Docker Compose v2.0+
- **C-007**: Documentation must not assume specific cloud provider or infrastructure setup
