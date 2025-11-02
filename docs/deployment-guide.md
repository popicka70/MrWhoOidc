# MrWhoOidc Deployment Guide

**Version**: 1.0
**Last Updated**: 2025-11-01
**Target Audience**: Operations engineers, DevOps teams, System administrators

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Quick Start](#quick-start)
3. [System Requirements](#system-requirements)
4. [Configuration](#configuration)
5. [PostgreSQL Configuration](#postgresql-configuration)
6. [TLS Certificates](#tls-certificates)
7. [Environment Variables](#environment-variables)
8. [Deployment Scenarios](#deployment-scenarios)
9. [Troubleshooting](#troubleshooting)
10. [Security Best Practices](#security-best-practices)
11. [Monitoring and Logging](#monitoring-and-logging)
12. [Backup and Recovery](#backup-and-recovery)

---

## Prerequisites

Before deploying MrWhoOidc, ensure you have the following installed:

- **Docker Engine**: Version 20.10 or later
- **Docker Compose**: V2.0 or later (compose plugin)
- **Operating System**: Linux (recommended), Windows, macOS
- **Network Access**: Outbound internet access to pull images from GitHub Container Registry
- **TLS Certificate**: Valid certificate for production deployments

### Verify Prerequisites

```bash
# Check Docker version
docker --version
# Required: Docker version 20.10.0 or higher

# Check Docker Compose version
docker compose version
# Required: Docker Compose version v2.0.0 or higher

# Test Docker daemon
docker ps
# Should list running containers (or empty if none running)
```

---

## Quick Start

For a rapid deployment to test MrWhoOidc:

1. **Create deployment directory**:

```bash
mkdir mrwhooidc-deployment && cd mrwhooidc-deployment
```

2. **Download compose file**:

```bash
curl -O https://raw.githubusercontent.com/popicka70/MrWhoOidc/main/docker-compose.yml
```

3. **Download .env example**:

```bash
curl -O https://raw.githubusercontent.com/popicka70/MrWhoOidc/main/.env.example
```

4. **Configure environment**:

```bash
cp .env.example .env
# Edit .env and set:
# - POSTGRES_PASSWORD (use a strong password)
# - OIDC_PUBLIC_BASE_URL (your deployment URL)
# - CERT_PASSWORD (certificate password)
```

5. **Download development certificate** (for testing only):

```bash
mkdir certs
curl -o certs/aspnetapp.pfx https://raw.githubusercontent.com/popicka70/MrWhoOidc/main/certs/aspnetapp.pfx
```

6. **Start services**:

```bash
docker compose up -d
```

7. **Verify deployment**:

```bash
# Wait 30 seconds for startup
sleep 30

# Check discovery endpoint
curl -k https://localhost:8443/.well-known/openid-configuration

# Expected: JSON response with OIDC metadata
```

8. **Access admin UI**:

Open browser to `https://localhost:8443/admin`

---

## System Requirements

### Minimum Requirements

| Component | Requirement |
|-----------|-------------|
| **CPU** | 2 cores (x64 or ARM64) |
| **RAM** | 2GB (4GB recommended with Redis) |
| **Disk Space** | 10GB for application + database growth |
| **Network** | 1 Gbps (for high-traffic deployments) |

### Resource Allocation Per Service

| Service | CPU | Memory | Storage |
|---------|-----|--------|---------|
| **MrWhoOidc.WebAuth** | 1 core | 512MB | N/A (stateless) |
| **PostgreSQL** | 1 core | 512MB | 5GB+ (grows with data) |
| **Redis** (optional) | 0.5 core | 256MB | 100MB (cache data) |

### Performance Targets

- **Concurrent Users**: 1,000+ with Redis enabled
- **Authentication Requests**: 100-200 req/sec (single instance)
- **Startup Time**: <30 seconds (including migrations)
- **Discovery Endpoint**: <50ms response time

---

## Configuration

MrWhoOidc is configured via environment variables defined in `.env` file or directly in `docker-compose.yml`.

### Configuration File Structure

```bash
mrwhooidc-deployment/
├── docker-compose.yml      # Service definitions
├── .env                    # Environment variables (DO NOT commit to git)
├── .env.example            # Template with all options documented
└── certs/                  # TLS certificates
    └── aspnetapp.pfx       # Certificate file
```

### Essential Configuration Steps

1. **Copy environment template**:

```bash
cp .env.example .env
```

2. **Edit .env file**:

```bash
nano .env  # or your preferred editor
```

3. **Set required variables** (minimum):

```bash
POSTGRES_PASSWORD=<strong-random-password>
OIDC_PUBLIC_BASE_URL=https://your-domain.com
CERT_PASSWORD=<certificate-password>
```

4. **Generate strong password**:

```bash
# On Linux/macOS:
openssl rand -base64 32

# On Windows PowerShell:
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Min 0 -Max 256 }))
```

---

## PostgreSQL Configuration

MrWhoOidc uses PostgreSQL 16 for persistent storage of OIDC entities.

### Database Connection

The connection string is automatically constructed from environment variables:

```yaml
ConnectionStrings__authdb: Host=postgres;Port=5432;Database=authdb;Username=oidc;Password=${POSTGRES_PASSWORD}
```

### Database Schema

- **Automatic Migrations**: Schema is automatically created/updated on application startup
- **Migration Safety**: Migrations are idempotent (safe to run multiple times)
- **Zero Downtime**: No manual migration steps required

### Database Persistence

Data is stored in a Docker named volume:

```yaml
volumes:
  postgres-data:
    driver: local
```

### Connection String Customization

To use external PostgreSQL (not Docker container):

```bash
# In .env file:
CONNECTION_STRING_AUTHDB=Host=external-db.example.com;Port=5432;Database=authdb;Username=oidc;Password=<password>;SSL Mode=Require
```

Update `docker-compose.yml` to remove postgres service if using external database.

### Database Backup

```bash
# Backup database to SQL file
docker exec mrwhooidc-postgres pg_dump -U oidc authdb > backup-$(date +%Y%m%d-%H%M%S).sql

# Restore database from SQL file
cat backup-YYYYMMDD-HHMMSS.sql | docker exec -i mrwhooidc-postgres psql -U oidc authdb
```

---

## Redis Configuration (Optional)

Redis provides distributed caching and session management for improved performance in production deployments.

### Why Redis?

**Performance Benefits**:

- **Session Caching**: Reduces database queries for frequently accessed session data
- **Distributed Cache**: Shares cache across multiple OIDC server instances
- **Token Caching**: Speeds up token validation and introspection
- **Rate Limiting**: Efficient distributed rate limiting across instances

**Typical Performance Gains**:

- 30-50% reduction in response times for authenticated requests
- 60-80% reduction in database load for read-heavy workloads
- Support for 1000+ concurrent users per instance (vs 300-500 without Redis)

### Enabling Redis

Redis is **optional** and disabled by default. Enable it in `.env`:

```bash
# Enable Redis caching
REDIS_ENABLED=true
REDIS_CONNECTION_STRING=redis:6379,abortConnect=false
```

The Redis service is already configured in `docker-compose.yml` and will start automatically:

```bash
# Start services (includes Redis if uncommented)
docker compose up -d

# Verify Redis is running
docker compose ps redis
# Should show: Up (healthy)
```

### Graceful Degradation

**Important**: The OIDC server is designed to function with or without Redis.

- **Redis Available**: Full caching benefits, optimal performance
- **Redis Unavailable**: Automatic fallback to in-memory cache, degraded performance but no outage
- **Connection Setting**: `abortConnect=false` ensures Redis failures don't crash the application

**Behavior During Redis Failure**:

```bash
# Simulate Redis failure
docker compose stop redis

# OIDC server continues functioning
curl -k https://localhost:8443/.well-known/openid-configuration
# Expected: HTTP 200 (success, using fallback cache)

# Check logs
docker compose logs webauth | grep -i redis
# Will show: "Redis connection failed, using in-memory cache"
```

### Redis Persistence Options

The default configuration uses RDB (Redis Database) snapshots for persistence.

#### Option 1: RDB Snapshots (Default - Recommended)

```yaml
redis:
  command: redis-server --save 60 1 --loglevel warning
  # Saves snapshot if 1+ keys changed in 60 seconds
  # Good balance between performance and durability
```

**Pros**: Fast, small disk footprint  
**Cons**: Potential data loss (up to 60 seconds) on crash  
**Use Case**: Production deployments where cache can be rebuilt

#### Option 2: AOF (Append-Only File)

```yaml
redis:
  command: redis-server --appendonly yes --loglevel warning
  # Logs every write operation
  # More durable but slower
```

**Pros**: Minimal data loss (1 second or less)  
**Cons**: Slower writes, larger disk usage  
**Use Case**: When cache data is critical and rebuild is expensive

#### Option 3: No Persistence (Cache Only)

```yaml
redis:
  command: redis-server --loglevel warning
  # No persistence, pure in-memory cache
```

**Pros**: Maximum performance  
**Cons**: All cache lost on restart  
**Use Case**: Development, testing, or when cache warmup is fast

### Redis Monitoring

#### Check Redis Health

```bash
# Test Redis connection
docker compose exec redis redis-cli ping
# Expected: PONG

# Check Redis info
docker compose exec redis redis-cli INFO server
# Shows version, uptime, OS

# View connected clients
docker compose exec redis redis-cli CLIENT LIST
# Shows active connections from webauth
```

#### Monitor Performance

```bash
# Real-time monitoring
docker compose exec redis redis-cli --stat
# Shows: commands/sec, hits, misses, keyspace

# Check memory usage
docker compose exec redis redis-cli INFO memory | grep used_memory_human
# Example: used_memory_human:45.23M

# View cache hit rate
docker compose exec redis redis-cli INFO stats | grep keyspace
# Higher hits/misses ratio = better performance
```

#### Monitor Operations

```bash
# Watch all Redis commands in real-time
docker compose exec redis redis-cli monitor
# Useful for debugging cache behavior

# View slow operations (>10ms)
docker compose exec redis redis-cli SLOWLOG GET 10
```

### Redis Troubleshooting

#### Redis Won't Start

**Symptom**: `docker compose ps redis` shows "Exited" or "Restarting"

**Check logs**:

```bash
docker compose logs redis
```

**Common Issues**:

1. **Port conflict**:
   - Another Redis instance using port 6379
   - Solution: Change port in docker-compose.yml: `command: redis-server --port 6380`

2. **Permission denied on volume**:
   - Redis can't write to `/data`
   - Solution: `docker compose down -v` then `docker compose up -d` (recreates volume)

3. **Memory limit exceeded**:
   - Redis using too much memory
   - Solution: Add memory limit in docker-compose.yml or tune Redis maxmemory

#### Webauth Can't Connect to Redis

**Symptom**: Application logs show "Redis connection timeout"

**Checks**:

```bash
# Verify Redis is on internal network
docker compose exec webauth ping redis
# Should resolve and respond

# Check Redis health
docker compose ps redis
# Status should be "Up (healthy)"

# Test connection from webauth container
docker compose exec webauth sh -c "nc -zv redis 6379"
# Should show: Connection to redis 6379 port [tcp/*] succeeded!
```

**Solutions**:

- Ensure `REDIS_ENABLED=true` in `.env`
- Verify `REDIS_CONNECTION_STRING=redis:6379,abortConnect=false`
- Check both services are on `internal` network

#### Cache Not Working (Low Hit Rate)

**Symptom**: Redis connected but cache hit rate is low

**Diagnosis**:

```bash
# Check cache statistics
docker compose exec redis redis-cli INFO stats

# Look for:
# keyspace_hits:1000
# keyspace_misses:5000
# Hit rate = hits / (hits + misses) = 16.7% (low)
```

**Common Causes**:

1. **Cache warming**: Just started, cache not yet populated (normal for first few minutes)
2. **TTL too short**: Keys expiring too quickly
3. **Memory pressure**: Redis evicting keys due to memory limits
4. **No cache benefit**: Workload is mostly writes (cache helps reads)

**Check for evictions**:

```bash
docker compose exec redis redis-cli INFO stats | grep evicted
# evicted_keys:0 is good
# evicted_keys:>0 means memory pressure
```

#### High Memory Usage

**Symptom**: Redis using excessive memory

**Check current usage**:

```bash
docker compose exec redis redis-cli INFO memory | grep used_memory_human
```

**Solutions**:

1. **Set max memory limit**:

```yaml
# docker-compose.yml
redis:
  command: redis-server --maxmemory 256mb --maxmemory-policy allkeys-lru --save 60 1 --loglevel warning
```

2. **Tune eviction policy**:
   - `allkeys-lru`: Evict least recently used keys (recommended)
   - `allkeys-lfu`: Evict least frequently used keys
   - `volatile-lru`: Evict only keys with TTL

3. **Clear cache if needed**:

```bash
docker compose exec redis redis-cli FLUSHALL
# WARNING: Clears all cache data
```

### Redis Performance Tuning

#### For High-Traffic Production

```yaml
redis:
  command: redis-server --maxmemory 512mb --maxmemory-policy allkeys-lru --save 300 10 --loglevel warning
  deploy:
    resources:
      limits:
        cpus: '1'
        memory: 1G
      reservations:
        cpus: '0.5'
        memory: 512M
```

**Settings explained**:

- `--maxmemory 512mb`: Limit Redis to 512MB RAM
- `--maxmemory-policy allkeys-lru`: Evict LRU keys when full
- `--save 300 10`: Save snapshot if 10+ keys changed in 5 minutes (less frequent saves = better performance)

#### For Development/Testing

```yaml
redis:
  command: redis-server --loglevel debug
  # No persistence, verbose logging
```

### Disabling Redis

To disable Redis after enabling:

1. **Update .env**:

```bash
REDIS_ENABLED=false
```

2. **Restart webauth** (no need to stop Redis service):

```bash
docker compose restart webauth
```

3. **Optional: Stop Redis service**:

```bash
docker compose stop redis
```

The OIDC server will automatically fall back to in-memory caching.

### Redis Best Practices

- ✅ **Always use `abortConnect=false`** for graceful degradation
- ✅ **Enable persistence** (RDB snapshots) for production
- ✅ **Monitor hit rate** - aim for >70% for cache-friendly workloads
- ✅ **Set memory limits** to prevent OOM issues
- ✅ **Use LRU eviction** for predictable cache behavior
- ✅ **Regular backups** of Redis RDB file (if critical data)
- ✅ **Health checks enabled** (already configured in docker-compose.yml)
- ⚠️ **Don't rely on Redis** for critical data - it's a cache, not a database
- ⚠️ **Don't disable persistence** unless you understand the tradeoff

---

## TLS Certificates

MrWhoOidc requires HTTPS for production deployments per OIDC specification.

### Certificate Requirements

- **Format**: PFX (PKCS#12) containing certificate + private key
- **Location**: Mounted as volume at `/https` in container
- **Password**: Provided via `CERT_PASSWORD` environment variable

### Development Certificate

A self-signed certificate is included for **development/testing only**:

```bash
# Download development certificate
curl -o certs/aspnetapp.pfx https://raw.githubusercontent.com/popicka70/MrWhoOidc/main/certs/aspnetapp.pfx

# Password: changeit
```

**WARNING**: Never use self-signed certificates in production!

### Production Certificate Options

#### Option 1: Let's Encrypt (Recommended for Internet-Facing Deployments)

```bash
# Install Certbot
sudo apt-get install certbot  # Ubuntu/Debian

# Obtain certificate
sudo certbot certonly --standalone -d auth.example.com

# Convert to PFX format
sudo openssl pkcs12 -export \
  -out certs/production.pfx \
  -inkey /etc/letsencrypt/live/auth.example.com/privkey.pem \
  -in /etc/letsencrypt/live/auth.example.com/cert.pem \
  -certfile /etc/letsencrypt/live/auth.example.com/chain.pem \
  -passout pass:your-password-here
```

Update `docker-compose.yml`:

```yaml
volumes:
  - ./certs/production.pfx:/https/production.pfx:ro
environment:
  ASPNETCORE_Kestrel__Certificates__Default__Path: /https/production.pfx
  ASPNETCORE_Kestrel__Certificates__Default__Password: ${CERT_PASSWORD}
```

#### Option 2: Commercial Certificate Authority

1. Purchase certificate from CA (DigiCert, GlobalSign, etc.)
2. Download certificate files (certificate + private key)
3. Convert to PFX if needed
4. Place in `./certs/` directory
5. Update `CERT_PASSWORD` in `.env`

#### Option 3: Internal PKI (Corporate Environments)

1. Request certificate from internal CA
2. Ensure certificate includes Subject Alternative Name (SAN) matching deployment URL
3. Export as PFX with private key
4. Distribute root CA certificate to clients

#### Option 4: Reverse Proxy TLS Termination (Recommended for Production)

Deploy MrWhoOidc behind nginx/Traefik/HAProxy:

- Reverse proxy handles TLS termination
- MrWhoOidc can use HTTP internally (within internal network)
- Simplifies certificate renewal

See "Reverse Proxy Setup" section for configuration examples.

---

## Environment Variables

Complete reference of all environment variables.

### Required Variables

| Variable | Description | Example |
|----------|-------------|---------|
| `POSTGRES_PASSWORD` | PostgreSQL database password | `mySecureP@ssw0rd123!` |
| `OIDC_PUBLIC_BASE_URL` | Public URL for OIDC server (issuer URL) | `https://auth.example.com` |
| `CERT_PASSWORD` | Password for TLS certificate PFX file | `certP@ssw0rd` |

### Optional Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_ENVIRONMENT` | `Production` | ASP.NET environment (Development, Staging, Production) |
| `MULTITENANT_ENABLED` | `false` | Enable multi-tenant mode |
| `MULTITENANT_DEFAULT_TENANT_SLUG` | `default` | Default tenant identifier |
| `REDIS_ENABLED` | `false` | Enable Redis caching (requires Redis service) |
| `MAIL_ENABLED` | `false` | Enable email notifications |
| `MAIL_SMTP_HOST` | - | SMTP server hostname |
| `MAIL_SMTP_PORT` | `587` | SMTP server port |
| `MAIL_FROM_ADDRESS` | - | Sender email address |
| `MAIL_FROM_NAME` | `MrWhoOidc` | Sender display name |
| `LOGGING_LEVEL` | `Information` | Minimum log level (Trace, Debug, Information, Warning, Error, Critical) |

### Variable Substitution in docker-compose.yml

Docker Compose automatically loads variables from `.env` file:

```yaml
environment:
  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
  OIDC_PUBLIC_BASE_URL: ${OIDC_PUBLIC_BASE_URL:-https://localhost:8443}
```

Syntax: `${VARIABLE:-default}` uses default if variable not set.

---

## Deployment Scenarios

### Scenario 1: Development/Testing

```bash
# Use development docker-compose.dev.yml with hot reload
docker compose -f docker-compose.dev.yml up

# Includes:
# - MailHog for email testing
# - Development certificate
# - Detailed logging
```

### Scenario 2: Production Single-Tenant

```bash
# .env configuration:
POSTGRES_PASSWORD=<strong-password>
OIDC_PUBLIC_BASE_URL=https://auth.company.com
CERT_PASSWORD=<cert-password>
MULTITENANT_ENABLED=false

# Deploy:
docker compose up -d
```

### Scenario 3: Production Multi-Tenant

```bash
# .env configuration:
MULTITENANT_ENABLED=true
MULTITENANT_DEFAULT_TENANT_SLUG=default

# Deploy:
docker compose up -d

# Each tenant gets isolated data within same database
```

### Scenario 4: High-Performance with Redis

See [docker-compose-examples.md](./docker-compose-examples.md) for Redis integration.

---

## Troubleshooting

### Container Won't Start

**Symptom**: `docker compose up` fails or webauth container exits immediately

**Check logs**:

```bash
docker compose logs webauth
```

**Common Issues**:

1. **Database connection failed**:
   - Verify `POSTGRES_PASSWORD` matches in .env and PostgreSQL service
   - Check PostgreSQL is healthy: `docker compose ps postgres`
   - Solution: Ensure password is correctly set in .env

2. **Certificate not found**:
   - Error: `Unable to load certificate`
   - Solution: Verify certificate exists at `./certs/aspnetapp.pfx` and path is correct

3. **Port already in use**:
   - Error: `Bind for 0.0.0.0:8443 failed: port is already allocated`
   - Solution: Change port mapping in docker-compose.yml: `"8444:8443"`

4. **Permission denied (Linux)**:
   - Error: `Permission denied` when accessing certificate
   - Solution: `chmod 644 certs/aspnetapp.pfx`

### Discovery Endpoint Returns 404

**Symptom**: Curl to `/.well-known/openid-configuration` returns 404

**Checks**:

1. Verify `OIDC_PUBLIC_BASE_URL` matches your deployment URL
2. Check container is running: `docker compose ps`
3. Check logs for startup errors: `docker compose logs webauth`
4. Verify port mapping: Should see `0.0.0.0:8443->8443/tcp`

**Solution**:

```bash
# Check if service is listening
docker compose exec webauth netstat -tlnp | grep 8443

# Should show: tcp 0 0 :::8443 :::* LISTEN
```

### Database Connection Timeout

**Symptom**: Application logs show `timeout expired` errors

**Checks**:

1. Verify PostgreSQL is healthy:

```bash
docker compose ps postgres
# Status should be "Up (healthy)"
```

2. Check PostgreSQL logs:

```bash
docker compose logs postgres
```

3. Test connection manually:

```bash
docker compose exec postgres psql -U oidc -d authdb -c "SELECT version();"
```

**Solutions**:

- If PostgreSQL not healthy: Restart service: `docker compose restart postgres`
- If password mismatch: Fix `POSTGRES_PASSWORD` in .env and recreate: `docker compose up -d --force-recreate`

### Migrations Fail on Startup

**Symptom**: Container starts but migrations error in logs

**Check**:

```bash
docker compose logs webauth | grep -i migration
```

**Common Issues**:

1. **Concurrent migration attempts**:
   - Multiple instances trying to migrate simultaneously
   - Solution: Scale to 1 instance during startup, then scale up

2. **Corrupted migration state**:
   - Rare: Migration partially applied
   - Solution: Restore database from backup, restart deployment

### SSL Certificate Errors in Browser

**Symptom**: Browser shows certificate warning

**For Development**:

- Expected with self-signed certificate
- Click "Advanced" → "Proceed" (browsers vary)
- Or: Import cert into browser/OS trust store

**For Production**:

- Verify certificate CN/SAN matches deployment URL
- Check certificate not expired: `openssl pkcs12 -in certs/production.pfx -nokeys -passin pass:password | openssl x509 -noout -dates`
- Ensure certificate chain is complete (intermediate certificates included)

### High Memory Usage

**Symptom**: Container using excessive memory

**Check current usage**:

```bash
docker stats --no-stream

# Should see memory usage per container
```

**Solutions**:

1. **Limit memory per service** (docker-compose.yml):

```yaml
services:
  webauth:
    deploy:
      resources:
        limits:
          memory: 1GB
```

2. **Enable Redis** to offload session data from memory

3. **Tune PostgreSQL** shared_buffers for your workload

---

## Security Best Practices

### 1. Strong Passwords

- Use 32+ character random passwords
- Rotate passwords every 90 days
- Never commit `.env` to version control

### 2. TLS Configuration

- Use valid certificates from trusted CA
- Enable TLS 1.2+ only (disable older versions)
- Consider HTTP Strict Transport Security (HSTS)

### 3. Network Isolation

- Database on internal network only (no external access)
- Use firewall rules to restrict access:

```bash
# Allow HTTPS only
sudo ufw allow 8443/tcp
sudo ufw deny 5432/tcp  # Block PostgreSQL from external access
```

### 4. Regular Updates

- Monitor for security updates
- Subscribe to MrWhoOidc security advisories
- Update Docker images regularly:

```bash
docker compose pull
docker compose up -d
```

### 5. Principle of Least Privilege

- Run containers as non-root (already configured in Dockerfile)
- Limit container capabilities
- Use read-only volumes where possible

### 6. Secrets Management

**Development**: `.env` file (add to `.gitignore`)

**Production Options**:

- **Docker Secrets** (Swarm mode):

```yaml
secrets:
  postgres_password:
    external: true
```

- **HashiCorp Vault**: Inject secrets at runtime
- **Cloud Provider Secrets** (AWS Secrets Manager, Azure Key Vault, GCP Secret Manager)

### 7. Audit Logging

Enable comprehensive logging for security events:

```yaml
environment:
  LOGGING_LEVEL: Information
```

Ship logs to centralized system (ELK, Splunk, CloudWatch).

### 8. Production Configuration Checklist

Use this checklist before deploying to production:

#### Pre-Deployment Configuration

- [ ] **Strong Passwords**: Generated 32+ character random passwords for PostgreSQL
- [ ] **TLS Certificates**: Valid production certificates from trusted CA installed in `./certs/`
- [ ] **Base URL**: `OIDC_PUBLIC_BASE_URL` matches actual production domain
- [ ] **Certificate Password**: `CERT_PASSWORD` set correctly for production certificate
- [ ] **Environment File**: `.env` file permissions set to 600 (owner read/write only)
- [ ] **Git Ignore**: Confirmed `.env` is in `.gitignore` (never commit secrets)

#### Feature Configuration

- [ ] **Multi-Tenancy**: `MULTITENANT_ENABLED` configured per requirements
- [ ] **Redis**: `REDIS_ENABLED=true` for production performance (recommended)
- [ ] **Email/SMTP**: `MAIL_ENABLED=true` and SMTP credentials configured
- [ ] **Logging Level**: `LOGGING_LEVEL=Warning` or `Error` for production (reduce noise)

#### Security Hardening

- [ ] **Network Isolation**: Database on internal network only (no external access)
- [ ] **Firewall Rules**: Only ports 8443/443 accessible externally
- [ ] **TLS Version**: TLS 1.2+ enforced (disable TLS 1.0/1.1)
- [ ] **Container Security**: Running as non-root user (verified in Dockerfile)
- [ ] **Read-Only Volumes**: Certificate volumes mounted as `:ro` (read-only)

#### Resource Management

- [ ] **Resource Limits**: CPU and memory limits configured (see docker-compose-examples.md)
- [ ] **Health Checks**: Enabled for all services (webauth, postgres, redis)
- [ ] **Log Rotation**: Configured to prevent disk space exhaustion
- [ ] **Restart Policies**: Set to `unless-stopped` for all services

#### Operational Readiness

- [ ] **Backup Procedures**: Database backup script created and scheduled (daily recommended)
- [ ] **Monitoring**: External monitoring/alerting configured (Prometheus, Grafana, CloudWatch, etc.)
- [ ] **Log Aggregation**: Logs shipped to centralized system (ELK, Splunk, Datadog, etc.)
- [ ] **Secrets Management**: Production secrets stored in secure vault (not just .env file)
- [ ] **Documentation**: Team trained on deployment, backup, and recovery procedures
- [ ] **Rollback Plan**: Documented rollback procedure for failed deployments

#### Verification

- [ ] **Discovery Endpoint**: `curl https://yourdomain/.well-known/openid-configuration` returns valid JSON
- [ ] **SSL/TLS**: No browser certificate warnings (valid chain of trust)
- [ ] **Database Connection**: Application logs show successful database connection
- [ ] **Redis Connection**: (if enabled) Application logs show successful Redis connection
- [ ] **Email Sending**: (if enabled) Test email delivery works
- [ ] **Health Endpoint**: `curl https://yourdomain/health` returns HTTP 200
- [ ] **Admin UI Access**: Admin interface accessible and functional

#### Post-Deployment

- [ ] **Security Scan**: Container image scanned for vulnerabilities
- [ ] **Penetration Testing**: Security assessment completed (if required)
- [ ] **Performance Testing**: Load testing confirms performance targets met
- [ ] **Update Schedule**: Regular security update schedule established

**Tip**: Save this checklist and review it for each deployment or upgrade.

---

## Monitoring and Logging

### Health Checks

MrWhoOidc includes built-in health checks:

```bash
# Check health endpoint
curl -k https://localhost:8443/health

# Expected: HTTP 200 with JSON status
```

Health checks verify:

- Database connectivity
- Redis connectivity (if enabled)
- Key material loaded

### Log Access

**View logs**:

```bash
# All services
docker compose logs

# Specific service
docker compose logs webauth

# Follow logs in real-time
docker compose logs -f webauth

# Last 100 lines
docker compose logs --tail=100 webauth
```

**Log Levels**:

- `Trace`: Very detailed (development only)
- `Debug`: Detailed debugging info
- `Information`: General informational messages (default)
- `Warning`: Warning messages (potential issues)
- `Error`: Error messages (failures)
- `Critical`: Critical failures requiring immediate attention

**Set log level**:

```bash
# In .env:
LOGGING_LEVEL=Warning
```

### Metrics and Observability

For production deployments, integrate with monitoring tools:

- **Prometheus**: Metrics collection
- **Grafana**: Visualization dashboards
- **ELK Stack**: Log aggregation
- **Application Insights**: Azure monitoring
- **CloudWatch**: AWS monitoring

Example Prometheus configuration:

```yaml
# Add to docker-compose.yml
services:
  prometheus:
    image: prom/prometheus
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml
    ports:
      - "9090:9090"
```

---

## Backup and Recovery

### Database Backup Strategy

**Daily Backup Script**:

```bash
#!/bin/bash
# backup-db.sh

BACKUP_DIR="/backups"
TIMESTAMP=$(date +%Y%m%d-%H%M%S)
BACKUP_FILE="$BACKUP_DIR/mrwhooidc-backup-$TIMESTAMP.sql.gz"

# Create backup
docker exec mrwhooidc-postgres pg_dump -U oidc authdb | gzip > "$BACKUP_FILE"

# Verify backup
if [ -f "$BACKUP_FILE" ]; then
    echo "Backup created: $BACKUP_FILE"
    
    # Delete backups older than 30 days
    find "$BACKUP_DIR" -name "mrwhooidc-backup-*.sql.gz" -mtime +30 -delete
else
    echo "Backup failed!"
    exit 1
fi
```

**Schedule with Cron**:

```bash
# Edit crontab
crontab -e

# Add daily backup at 2 AM
0 2 * * * /path/to/backup-db.sh >> /var/log/mrwhooidc-backup.log 2>&1
```

### Database Restore Procedure

#### Quick Restore (Services Running)

Use this procedure when PostgreSQL is running:

```bash
# 1. Stop application (keeps database running)
docker compose stop webauth

# 2. Restore from compressed backup
gunzip < backups/mrwhooidc-backup-YYYYMMDD-HHMMSS.sql.gz | \
  docker compose exec -T postgres psql -U oidc authdb

# 3. Restart application
docker compose start webauth

# 4. Verify
curl -k https://localhost:8443/.well-known/openid-configuration
```

#### Full Restore (Services Stopped)

Use this procedure after `docker compose down`:

```bash
# 1. Start PostgreSQL only
docker compose up -d postgres

# 2. Wait for PostgreSQL healthy
docker compose ps postgres
# Wait until status shows "(healthy)"

# 3. Restore from compressed backup
gunzip < backups/mrwhooidc-backup-YYYYMMDD-HHMMSS.sql.gz | \
  docker compose run --rm -T postgres psql -h postgres -U oidc authdb

# 4. Start application
docker compose up -d webauth

# 5. Verify
curl -k https://localhost:8443/.well-known/openid-configuration
```

#### Restore from Uncompressed Backup

```bash
# If backup is not compressed (.sql file)
docker compose exec -T postgres psql -U oidc authdb < backups/backup-file.sql
```

#### Restore with Database Drop/Recreate

Use if you need to clear all data first:

```bash
# 1. Stop application
docker compose stop webauth

# 2. Drop and recreate database
docker compose exec postgres psql -U oidc -c "DROP DATABASE IF EXISTS authdb;"
docker compose exec postgres psql -U oidc -c "CREATE DATABASE authdb;"

# 3. Restore backup
gunzip < backups/mrwhooidc-backup-YYYYMMDD-HHMMSS.sql.gz | \
  docker compose exec -T postgres psql -U oidc authdb

# 4. Restart application
docker compose start webauth
```

#### Restore Verification

After restore, verify data integrity:

```bash
# 1. Check table counts
docker compose exec postgres psql -U oidc authdb -c "\
  SELECT 'clients' AS table, COUNT(*) FROM clients \
  UNION ALL \
  SELECT 'users', COUNT(*) FROM users \
  UNION ALL \
  SELECT 'consent_grants', COUNT(*) FROM consent_grants;"

# 2. Test authentication
# - Open admin UI: https://localhost:8443/admin
# - Verify can login
# - Check client list displays

# 3. Check logs for errors
docker compose logs --tail=50 webauth | grep -i error
# Should show no errors related to database
```

#### Restore Troubleshooting

**Problem**: "database authdb already exists"

```bash
# Solution: Drop existing database first
docker compose exec postgres psql -U oidc -c "DROP DATABASE authdb;"
docker compose exec postgres psql -U oidc -c "CREATE DATABASE authdb;"
# Then restore
```

**Problem**: "permission denied"

```bash
# Solution: Use -T flag to disable pseudo-TTY
gunzip < backup.sql.gz | docker compose exec -T postgres psql -U oidc authdb
```

**Problem**: Restore hangs or times out

```bash
# Check PostgreSQL health
docker compose ps postgres

# Check PostgreSQL logs
docker compose logs postgres

# Restart PostgreSQL if needed
docker compose restart postgres
```

**See Also**: [upgrade-guide.md](./upgrade-guide.md) for upgrade-specific restore procedures

### Disaster Recovery

**Full Recovery Steps**:

1. **Reinstall Docker** (if needed)
2. **Restore docker-compose.yml and .env** from backup
3. **Restore certificates** to `./certs/`
4. **Pull latest image**:

```bash
docker compose pull
```

5. **Create volumes and start PostgreSQL**:

```bash
docker compose up -d postgres
```

6. **Restore database backup** (see above)

7. **Start application**:

```bash
docker compose up -d webauth
```

8. **Verify deployment**

**Recovery Time Objective (RTO)**: ~15-30 minutes  
**Recovery Point Objective (RPO)**: Daily backups = 24 hours data loss maximum

---

## Next Steps

- **Configuration Examples**: See [docker-compose-examples.md](./docker-compose-examples.md)
- **Upgrade Guide**: See [upgrade-guide.md](./upgrade-guide.md)
- **Admin Guide**: See [admin-guide.md](./admin-guide.md)
- **Developer Guide**: See [developer-guide.md](./developer-guide.md)

---

## Support

- **GitHub Issues**: [https://github.com/popicka70/MrWhoOidc/issues](https://github.com/popicka70/MrWhoOidc/issues)
- **Documentation**: [https://github.com/popicka70/MrWhoOidc](https://github.com/popicka70/MrWhoOidc)

---

**Document Version**: 1.0  
**Last Updated**: 2025-11-01  
**Maintained By**: MrWhoOidc Project
