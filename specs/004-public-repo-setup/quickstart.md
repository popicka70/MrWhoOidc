# Quick Start Guide: 10-Minute MrWhoOidc Deployment

**Feature**: Public Repository Setup for MrWhoOidc Distribution  
**Date**: November 2, 2025  
**Purpose**: Validate that README Quick Start enables deployment in 10 minutes or less

## Test Scenario

A developer with Docker installed following the README Quick Start section should be able to:

1. Clone the repository
2. Configure environment variables
3. Start services with docker-compose
4. Verify the deployment is working
5. Access the admin UI

**Target Time**: 10 minutes  
**Prerequisites**: Docker Engine 20.10+, Docker Compose V2+, 4GB RAM available

## Quick Start Content (for README)

### Prerequisites

Before you begin, ensure you have:

- Docker Engine 20.10 or later
- Docker Compose V2 or later
- 4GB available RAM
- Port 8443 available (or customize in docker-compose.yml)

**Verify prerequisites:**

```bash
docker --version  # Should show 20.10+
docker compose version  # Should show v2.0+
```

### Quick Start (4 Steps)

#### Step 1: Clone Repository (30 seconds)

```bash
git clone https://github.com/popicka70/MrWho.git
cd MrWho
```

#### Step 2: Configure Environment (2 minutes)

```bash
# Copy environment template
cp .env.example .env

# Edit .env and set these required values:
# - POSTGRES_PASSWORD: Strong database password
# - OIDC_PUBLIC_BASE_URL: Public URL where IdP will be accessible
```

**Minimum .env configuration:**

```bash
# Database (REQUIRED)
POSTGRES_PASSWORD=your_secure_password_here

# OIDC Configuration (REQUIRED)
OIDC_PUBLIC_BASE_URL=https://localhost:8443

# TLS Certificate (Optional - defaults to development cert)
# CERT_PASSWORD=changeit
```

**Security Note**: Change `POSTGRES_PASSWORD` to a strong password (min 16 characters, mixed case, numbers, symbols) before deployment.

#### Step 3: Start Services (5 minutes)

```bash
# Start all services in background
docker compose up -d

# Monitor startup logs
docker compose logs -f

# Wait for "Application started" message
# PostgreSQL migrations run automatically on first startup
```

**Expected output:**

```text
✓ Network mrwho_edge      Created
✓ Network mrwho_internal  Created
✓ Volume mrwho_postgres-data  Created
✓ Container mrwho-postgres-1  Healthy
✓ Container mrwho-webauth-1   Started
```

#### Step 4: Verify Deployment (2 minutes)

**Check OpenID Discovery Endpoint:**

```bash
curl -k https://localhost:8443/.well-known/openid-configuration
```

**Expected response:** JSON with `issuer`, `authorization_endpoint`, `token_endpoint`, etc.

**Access Admin UI:**

Open browser to: `https://localhost:8443/admin`

- Accept self-signed certificate warning (development only)
- Default credentials created on first startup (check logs or docs)
- You should see the MrWhoOidc admin dashboard

**Health Check:**

```bash
# Run health check script
./scripts/health-check.sh

# Or manually check health endpoint
curl -k https://localhost:8443/health
```

### What You Get

✅ **Fully functional OIDC Provider** supporting:

- OpenID Connect Core 1.0
- OAuth 2.0 Authorization Code Flow with PKCE
- Token Exchange (RFC 8693)
- Automatic database migrations
- Admin UI for client/scope/user management

✅ **Production-ready architecture**:

- PostgreSQL 16 for data persistence
- Automatic TLS/HTTPS (self-signed dev cert included)
- Health checks and graceful shutdown
- Structured logging

### Next Steps

1. **Create your first OIDC client** in admin UI (`/admin/clients`)
2. **Try demo applications** in `/demos` directory
3. **Read deployment guide** for production configuration (`/docs/deployment-guide.md`)
4. **Enable Redis caching** for better performance (`docker-compose.redis.yml`)
5. **Configure multi-tenancy** if needed (`/docs/multitenancy-quick-reference.md`)

### Common Issues

**Port 8443 already in use:**

```bash
# Edit docker-compose.yml and change port mapping:
ports:
  - "9443:8443"  # Use 9443 on host instead

# Update OIDC_PUBLIC_BASE_URL in .env:
OIDC_PUBLIC_BASE_URL=https://localhost:9443
```

**Database connection failed:**

```bash
# Check PostgreSQL is healthy
docker compose ps

# View PostgreSQL logs
docker compose logs postgres

# Common fix: wait 30 seconds for PostgreSQL to fully start, then:
docker compose restart webauth
```

**Certificate errors in browser:**

- Expected for development with self-signed certificate
- Click "Advanced" → "Proceed to localhost (unsafe)"
- For production: provide your own certificate (see deployment guide)

**Migrations failed:**

```bash
# Check webauth logs
docker compose logs webauth

# Restart to retry migrations
docker compose restart webauth
```

## Testing Checklist

Use this to validate the Quick Start works end-to-end:

### Pre-Test Setup

- [ ] Fresh system or VM with Docker installed
- [ ] No previous MrWhoOidc containers/volumes
- [ ] Port 8443 available
- [ ] Timer ready to track time

### Step-by-Step Validation

- [ ] **Step 1 (Clone)**: Repository clones successfully, MrWho directory created
- [ ] **Step 2 (Configure)**: `.env.example` copied to `.env`, required variables set
- [ ] **Step 3 (Start)**: `docker compose up -d` completes without errors
- [ ] **Step 3 (Wait)**: PostgreSQL reports healthy within 30 seconds
- [ ] **Step 3 (Migrations)**: Webauth logs show "Database migration completed"
- [ ] **Step 3 (Ready)**: Webauth logs show "Application started" or similar
- [ ] **Step 4 (Discovery)**: curl command returns valid JSON
- [ ] **Step 4 (Admin UI)**: Browser loads admin UI at https://localhost:8443/admin
- [ ] **Step 4 (Health)**: Health check returns 200 OK

### Success Criteria

- [ ] Total time from clone to working admin UI: ≤ 10 minutes
- [ ] All docker containers running and healthy
- [ ] OpenID discovery endpoint returns valid configuration
- [ ] Admin UI accessible and functional
- [ ] No errors in docker compose logs (warnings acceptable)

### Failure Scenarios to Test

- [ ] Missing POSTGRES_PASSWORD: Should fail with clear error message
- [ ] Invalid OIDC_PUBLIC_BASE_URL: Should start but discovery endpoint shows wrong issuer
- [ ] Port conflict: Should fail with clear "port already in use" error
- [ ] Insufficient memory: Should fail to start PostgreSQL or webauth

## README Integration

The Quick Start content above should be integrated into the public repository README.md as follows:

**Position**: After project description and feature list, before detailed documentation links

**Length**: ~150 lines including code blocks and expected outputs

**Style**:

- Imperative commands (not "you can" or "you should")
- Clear separation of required vs optional steps
- Expected outputs shown for every command
- Troubleshooting inline for common issues
- Visual indicators (✅ ✓ ❌ etc.) for status

**Links from Quick Start**:

- "Read deployment guide" → `/docs/deployment-guide.md`
- "Try demo applications" → `/demos/README.md`
- "Enable Redis" → link to docker-compose.redis.yml section
- "Configure multi-tenancy" → `/docs/multitenancy-quick-reference.md`

## Validation Script

Create `/MrWho/scripts/validate-quickstart.sh` to automate testing:

```bash
#!/bin/bash
set -e

echo "=== MrWhoOidc Quick Start Validation ==="
echo ""

# Check prerequisites
echo "Checking prerequisites..."
docker --version || { echo "ERROR: Docker not installed"; exit 1; }
docker compose version || { echo "ERROR: Docker Compose V2 not installed"; exit 1; }

# Check .env exists
if [ ! -f .env ]; then
    echo "ERROR: .env file not found. Run: cp .env.example .env"
    exit 1
fi

# Start services
echo "Starting services..."
START_TIME=$(date +%s)
docker compose up -d

# Wait for health
echo "Waiting for services to be healthy..."
for i in {1..60}; do
    if docker compose ps | grep -q "healthy"; then
        break
    fi
    sleep 2
    echo -n "."
done
echo ""

# Test discovery endpoint
echo "Testing OpenID Discovery..."
if curl -k -s https://localhost:8443/.well-known/openid-configuration | grep -q "issuer"; then
    echo "✓ Discovery endpoint working"
else
    echo "✗ Discovery endpoint failed"
    exit 1
fi

# Test health endpoint
echo "Testing health endpoint..."
if curl -k -s https://localhost:8443/health | grep -q "Healthy"; then
    echo "✓ Health check passed"
else
    echo "✗ Health check failed"
    exit 1
fi

END_TIME=$(date +%s)
DURATION=$((END_TIME - START_TIME))

echo ""
echo "=== Validation Complete ==="
echo "Total time: ${DURATION} seconds"
if [ $DURATION -le 600 ]; then
    echo "✓ Deployment completed within 10 minutes"
else
    echo "⚠ Deployment took longer than 10 minutes"
fi
```

## Documentation Dependencies

The Quick Start references these documents that must exist:

1. `/docs/deployment-guide.md` - Comprehensive deployment instructions
2. `/docs/multitenancy-quick-reference.md` - Multi-tenant setup
3. `/demos/README.md` - Demo applications overview
4. `/scripts/health-check.sh` - Health verification script
5. `.env.example` - Environment variable template

All must be created as part of this feature implementation.
