# Docker Compose Configuration Examples

**Version**: 1.0
**Last Updated**: 2025-11-02
**Target Audience**: Operations engineers, DevOps teams

This document provides example Docker Compose configurations for common MrWhoOidc deployment scenarios.

## Table of Contents

1. [Single-Tenant Configuration](#single-tenant-configuration)
2. [Multi-Tenant Configuration](#multi-tenant-configuration)
3. [Custom Certificate Configuration](#custom-certificate-configuration)
4. [SMTP Email Configuration](#smtp-email-configuration)
5. [Redis Caching Configuration](#redis-caching-configuration)
6. [Complete Production Configuration](#complete-production-configuration)

## Single-Tenant Configuration

The default configuration for deployments serving a single organization or identity domain.

### .env Configuration

```bash
# Required Settings
POSTGRES_PASSWORD=your_secure_postgres_password_here
OIDC_PUBLIC_BASE_URL=https://auth.example.com
CERT_PASSWORD=your_certificate_password

# Single-Tenant Settings (defaults)
MULTITENANT_ENABLED=false
ASPNETCORE_ENVIRONMENT=Production
```

### docker-compose.yml

Use the base `docker-compose.yml` without modifications. Single-tenant mode is the default.

### Verification

```bash
# Start services
docker compose up -d

# Check discovery endpoint
curl -k https://localhost:8443/.well-known/openid-configuration

# Verify tenant configuration
docker compose logs webauth | grep -i tenant
# Should show: Multi-tenant mode: Disabled
```

## Multi-Tenant Configuration

Enable multi-tenancy to serve multiple isolated identity domains from one deployment.

### .env Configuration

```bash
# Required Settings
POSTGRES_PASSWORD=your_secure_postgres_password_here
OIDC_PUBLIC_BASE_URL=https://auth.example.com
CERT_PASSWORD=your_certificate_password

# Multi-Tenant Settings
MULTITENANT_ENABLED=true
MULTITENANT_DEFAULT_TENANT_SLUG=default

# Optional: Configure tenant resolution
# Tenant resolution typically uses subdomain or path-based routing
```

### docker-compose.yml

Use the base `docker-compose.yml` - multi-tenancy is configured via environment variables only.

### Tenant URL Patterns

With multi-tenancy enabled, URLs follow this pattern:

```text
# Subdomain-based (recommended)
https://tenant1.auth.example.com/.well-known/openid-configuration
https://tenant2.auth.example.com/.well-known/openid-configuration

# Path-based
https://auth.example.com/tenant1/.well-known/openid-configuration
https://auth.example.com/tenant2/.well-known/openid-configuration
```

### Verification

```bash
# Start services
docker compose up -d

# Check multi-tenant mode enabled
docker compose logs webauth | grep -i "multi-tenant"
# Should show: Multi-tenant mode: Enabled

# Verify default tenant
curl -k https://localhost:8443/default/.well-known/openid-configuration
```

### Creating Tenants

Tenants are created via the admin UI or admin API:

```bash
# Access admin UI
https://auth.example.com/admin/tenants

# Or via API
curl -X POST https://auth.example.com/admin/api/tenants \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_ADMIN_TOKEN" \
  -d '{
    "slug": "acme-corp",
    "name": "Acme Corporation",
    "issuerUrl": "https://acme-corp.auth.example.com"
  }'
```

## Custom Certificate Configuration

Use production certificates from a certificate authority instead of the development certificate.

### Directory Structure

```text
deployment/
├── docker-compose.yml
├── .env
└── certs/
    ├── production.pfx          # Your production certificate
    └── intermediate-ca.crt     # Optional: intermediate CA bundle
```

### .env Configuration

```bash
# Required Settings
POSTGRES_PASSWORD=your_secure_postgres_password_here
OIDC_PUBLIC_BASE_URL=https://auth.example.com
CERT_PASSWORD=your_production_certificate_password

# Certificate Path (optional override)
ASPNETCORE_Kestrel__Certificates__Default__Path=/https/production.pfx
```

### docker-compose.yml Override

If using a different certificate filename, update the volume mount:

```yaml
services:
  webauth:
    # ... other configuration ...
    volumes:
      - ./certs/production.pfx:/https/production.pfx:ro
    environment:
      ASPNETCORE_Kestrel__Certificates__Default__Path: /https/production.pfx
      ASPNETCORE_Kestrel__Certificates__Default__Password: ${CERT_PASSWORD}
```

### Using Let's Encrypt Certificates

Convert Let's Encrypt certificates to PFX format:

```bash
# After obtaining certificate with certbot
sudo openssl pkcs12 -export \
  -out certs/production.pfx \
  -inkey /etc/letsencrypt/live/auth.example.com/privkey.pem \
  -in /etc/letsencrypt/live/auth.example.com/cert.pem \
  -certfile /etc/letsencrypt/live/auth.example.com/chain.pem \
  -password pass:your_certificate_password
```

### Verification

```bash
# Start services
docker compose up -d

# Verify certificate (from external client)
openssl s_client -connect auth.example.com:8443 -showcerts

# Check certificate details
curl -v https://auth.example.com/.well-known/openid-configuration
```

## SMTP Email Configuration

Enable email notifications for password resets, email verification, and notifications.

### .env Configuration

```bash
# Required Settings
POSTGRES_PASSWORD=your_secure_postgres_password_here
OIDC_PUBLIC_BASE_URL=https://auth.example.com
CERT_PASSWORD=your_certificate_password

# SMTP Configuration
MAIL_ENABLED=true
MAIL_SMTP_HOST=smtp.sendgrid.net
MAIL_SMTP_PORT=587
MAIL_SMTP_USE_SSL=true
MAIL_FROM_ADDRESS=no-reply@example.com
MAIL_FROM_NAME=Your Organization OIDC
MAIL_SMTP_USERNAME=apikey
MAIL_SMTP_PASSWORD=your_sendgrid_api_key
```

### Common SMTP Providers

#### SendGrid

```bash
MAIL_SMTP_HOST=smtp.sendgrid.net
MAIL_SMTP_PORT=587
MAIL_SMTP_USE_SSL=true
MAIL_SMTP_USERNAME=apikey
MAIL_SMTP_PASSWORD=your_sendgrid_api_key
```

#### AWS SES

```bash
MAIL_SMTP_HOST=email-smtp.us-east-1.amazonaws.com
MAIL_SMTP_PORT=587
MAIL_SMTP_USE_SSL=true
MAIL_SMTP_USERNAME=your_ses_smtp_username
MAIL_SMTP_PASSWORD=your_ses_smtp_password
```

#### Gmail (for testing only)

```bash
MAIL_SMTP_HOST=smtp.gmail.com
MAIL_SMTP_PORT=587
MAIL_SMTP_USE_SSL=true
MAIL_SMTP_USERNAME=your_email@gmail.com
MAIL_SMTP_PASSWORD=your_app_specific_password
```

**Note**: Gmail requires app-specific passwords and is not recommended for production.

#### Microsoft 365

```bash
MAIL_SMTP_HOST=smtp.office365.com
MAIL_SMTP_PORT=587
MAIL_SMTP_USE_SSL=true
MAIL_SMTP_USERNAME=your_email@yourdomain.com
MAIL_SMTP_PASSWORD=your_password
```

### Verification

```bash
# Start services
docker compose up -d

# Check email configuration
docker compose logs webauth | grep -i mail
# Should show: Email enabled: True

# Test email sending (trigger password reset or registration)
# Check application logs for SMTP connection attempts
docker compose logs -f webauth
```

### Troubleshooting Email

```bash
# Check SMTP connection
docker compose exec webauth sh -c "nc -zv $MAIL_SMTP_HOST $MAIL_SMTP_PORT"

# View detailed email logs
docker compose logs webauth | grep -i "smtp\|email\|mail"

# Common issues:
# - Authentication failed: Check username/password
# - Connection timeout: Check firewall allows outbound port 587/465
# - TLS/SSL errors: Verify MAIL_SMTP_USE_SSL setting matches provider requirements
```

## Redis Caching Configuration

Add Redis for improved performance with session caching and distributed cache.

### docker-compose.yml with Redis

```yaml
services:
  webauth:
    image: ghcr.io/popicka70/mrwhooidc:latest
    depends_on:
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy
    environment:
      # ... other environment variables ...
      Redis__Enabled: "true"
      Redis__ConnectionString: redis:6379,abortConnect=false
    # ... rest of webauth config ...

  postgres:
    # ... postgres configuration (unchanged) ...

  redis:
    image: redis:7.2-alpine
    command: redis-server --save 60 1 --loglevel warning
    volumes:
      - redis-data:/data
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 5s
    restart: unless-stopped
    networks:
      - internal

volumes:
  postgres-data:
    driver: local
  redis-data:
    driver: local

networks:
  internal:
    driver: bridge
    internal: true
  edge:
    driver: bridge
```

### .env Configuration

```bash
# Required Settings
POSTGRES_PASSWORD=your_secure_postgres_password_here
OIDC_PUBLIC_BASE_URL=https://auth.example.com
CERT_PASSWORD=your_certificate_password

# Redis Configuration
REDIS_ENABLED=true
REDIS_CONNECTION_STRING=redis:6379,abortConnect=false
```

### Redis Persistence Options

#### Option 1: RDB Snapshots (Default)

```yaml
redis:
  command: redis-server --save 60 1 --loglevel warning
  # Saves snapshot if 1+ keys changed in 60 seconds
```

#### Option 2: AOF (Append-Only File)

```yaml
redis:
  command: redis-server --appendonly yes --loglevel warning
  # More durable but slower
```

#### Option 3: No Persistence (Cache Only)

```yaml
redis:
  command: redis-server --loglevel warning
  # Fast but data lost on restart
```

### Verification

```bash
# Start services with Redis
docker compose up -d

# Verify Redis connection
docker compose exec redis redis-cli ping
# Expected: PONG

# Check webauth connected to Redis
docker compose logs webauth | grep -i redis
# Should show: Redis enabled: True

# Monitor Redis operations
docker compose exec redis redis-cli monitor

# Check cache statistics
docker compose exec redis redis-cli INFO stats
```

### Performance Monitoring

```bash
# Check hit rate
docker compose exec redis redis-cli INFO stats | grep keyspace

# Monitor memory usage
docker compose exec redis redis-cli INFO memory

# View active connections
docker compose exec redis redis-cli CLIENT LIST
```

## Complete Production Configuration

A comprehensive example combining all production features.

### Directory Structure

```text
production-deployment/
├── docker-compose.yml          # Base configuration
├── docker-compose.prod.yml     # Production overrides
├── .env                        # Environment variables (DO NOT COMMIT)
├── .env.example                # Template for .env
├── certs/
│   ├── production.pfx          # Production TLS certificate
│   └── ca-bundle.crt          # CA certificate chain
├── backups/                    # Database backup scripts
│   └── backup-db.sh
└── logs/                       # Application logs (if using volume mount)
```

### .env Configuration

```bash
# ==============================================================================
# Production Environment Configuration
# ==============================================================================

# Core Settings
POSTGRES_PASSWORD=randomly_generated_32_char_password_here
OIDC_PUBLIC_BASE_URL=https://auth.company.com
CERT_PASSWORD=certificate_password_from_ca

# Environment
ASPNETCORE_ENVIRONMENT=Production

# Multi-Tenancy
MULTITENANT_ENABLED=true
MULTITENANT_DEFAULT_TENANT_SLUG=default

# Redis (Performance)
REDIS_ENABLED=true
REDIS_CONNECTION_STRING=redis:6379,abortConnect=false

# Email (Notifications)
MAIL_ENABLED=true
MAIL_SMTP_HOST=smtp.sendgrid.net
MAIL_SMTP_PORT=587
MAIL_SMTP_USE_SSL=true
MAIL_FROM_ADDRESS=no-reply@company.com
MAIL_FROM_NAME=Company OIDC Server
MAIL_SMTP_USERNAME=apikey
MAIL_SMTP_PASSWORD=sendgrid_api_key_here

# Logging
LOGGING_LEVEL=Warning

# Advanced Settings
RATE_LIMIT_REQUESTS_PER_MINUTE=100
SESSION_TIMEOUT_MINUTES=30
```

### docker-compose.yml (Base)

Use the standard docker-compose.yml from the repository.

### docker-compose.prod.yml (Production Overrides)

```yaml
# Production-specific overrides
version: '3.8'

services:
  webauth:
    # Resource limits
    deploy:
      resources:
        limits:
          cpus: '2'
          memory: 1G
        reservations:
          cpus: '1'
          memory: 512M
    
    # Health check
    healthcheck:
      test: ["CMD", "curl", "-f", "https://localhost:8443/health"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 60s
    
    # Logging
    logging:
      driver: "json-file"
      options:
        max-size: "10m"
        max-file: "3"

  postgres:
    # Resource limits
    deploy:
      resources:
        limits:
          cpus: '1'
          memory: 1G
        reservations:
          cpus: '0.5'
          memory: 512M
    
    # PostgreSQL tuning
    command: postgres -c shared_buffers=256MB -c max_connections=200
    
    # Logging
    logging:
      driver: "json-file"
      options:
        max-size: "10m"
        max-file: "3"

  redis:
    # Resource limits
    deploy:
      resources:
        limits:
          cpus: '0.5'
          memory: 512M
        reservations:
          cpus: '0.25'
          memory: 256M
    
    # Logging
    logging:
      driver: "json-file"
      options:
        max-size: "5m"
        max-file: "3"
```

### Deployment Commands

```bash
# Deploy with production overrides
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d

# Check status
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps

# View logs
docker compose -f docker-compose.yml -f docker-compose.prod.yml logs -f

# Scale webauth (if load balancing)
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --scale webauth=3
```

### Production Checklist

- [ ] Strong passwords generated for PostgreSQL (32+ characters)
- [ ] Valid production TLS certificates installed
- [ ] OIDC_PUBLIC_BASE_URL matches actual production URL
- [ ] Multi-tenancy configured if needed
- [ ] Redis enabled for performance
- [ ] SMTP configured for email notifications
- [ ] Logging level set to Warning or Error
- [ ] Resource limits configured
- [ ] Health checks enabled
- [ ] Log rotation configured
- [ ] Backup procedures established
- [ ] Monitoring/alerting configured (external)
- [ ] Firewall rules applied (allow 8443/443 only)
- [ ] SSL/TLS verification working
- [ ] .env file permissions set to 600 (owner read/write only)
- [ ] Regular security updates scheduled

### Backup Script

Create `backups/backup-db.sh`:

```bash
#!/bin/bash
set -e

BACKUP_DIR="./backups"
TIMESTAMP=$(date +%Y%m%d-%H%M%S)
BACKUP_FILE="$BACKUP_DIR/mrwhooidc-$TIMESTAMP.sql.gz"

echo "Starting backup: $BACKUP_FILE"

docker compose exec -T postgres pg_dump -U oidc authdb | gzip > "$BACKUP_FILE"

echo "Backup complete: $BACKUP_FILE"

# Delete backups older than 30 days
find "$BACKUP_DIR" -name "mrwhooidc-*.sql.gz" -mtime +30 -delete

echo "Old backups cleaned up"
```

Make executable:

```bash
chmod +x backups/backup-db.sh
```

Schedule with cron:

```bash
# Daily backup at 2 AM
0 2 * * * /path/to/production-deployment/backups/backup-db.sh >> /var/log/mrwhooidc-backup.log 2>&1
```

## Next Steps

- **Security Hardening**: See [docker-security-best-practices.md](./docker-security-best-practices.md)
- **Monitoring Setup**: See [monitoring-and-observability.md](./monitoring-and-observability.md)
- **Upgrade Procedures**: See [upgrade-guide.md](./upgrade-guide.md)
- **Troubleshooting**: See [deployment-guide.md](./deployment-guide.md#troubleshooting)

## Support

- **GitHub Issues**: [https://github.com/popicka70/MrWhoOidc/issues](https://github.com/popicka70/MrWhoOidc/issues)
- **Documentation**: [https://github.com/popicka70/MrWhoOidc](https://github.com/popicka70/MrWhoOidc)

**Document Version**: 1.0
**Last Updated**: 2025-11-02
**Maintained By**: MrWhoOidc Project
