# Key & License Management Service - Deployment Guide

**Service**: Key & License Generator  
**Version**: 1.0  
**Last Updated**: October 28, 2025  
**Environment**: Docker Containerized Deployment

## Overview

This guide covers deploying the Key & License Management Service in production using Docker containers. The service generates cryptographic key pairs and license tokens for OIDC clients.

## Architecture

```
┌──────────────────────┐
│  Reverse Proxy       │  HTTPS termination
│  (nginx/Traefik)     │  Authentication
└──────────┬───────────┘
           │
┌──────────▼───────────┐
│  KeyGen Container    │  Port 8080
│  (ASP.NET Core 9.0)  │  Non-root (app:1654)
└──────────┬───────────┘
           │
   ┌───────┴────────┬──────────────┐
   │                │              │
┌──▼─────────┐  ┌──▼──────────┐  ┌▼─────────────┐
│ SQLite DB  │  │  Secrets    │  │  Data        │
│ Volume     │  │  Volume     │  │  Protection  │
│ (keygen-   │  │  (licensing │  │  Keys        │
│  data)     │  │   key)      │  │  (optional)  │
└────────────┘  └─────────────┘  └──────────────┘
```

## Prerequisites

### System Requirements

- **OS**: Linux (Ubuntu 20.04+, Debian 11+, RHEL 8+) or Windows Server 2019+
- **Docker**: 20.10+ or Docker Desktop
- **CPU**: 1 vCPU minimum, 2 vCPU recommended
- **RAM**: 512MB minimum, 1GB recommended
- **Disk**: 10GB for OS + 5GB for Docker images + storage for database growth

### Network Requirements

- **Inbound**: Port 8080 (or custom port) from reverse proxy
- **Outbound**: None required (no external API calls)
- **DNS**: Not required (can run on IP address)

### Security Requirements

- HTTPS termination (via reverse proxy)
- Firewall rules limiting access to admin users
- Secure storage for licensing private key
- Backup solution for database volume

## Pre-Deployment Checklist

- [ ] Docker installed and running
- [ ] Licensing ECDSA P-256 private key generated
- [ ] Secrets directory created with appropriate permissions
- [ ] Reverse proxy configured for HTTPS
- [ ] Firewall rules configured
- [ ] Backup strategy defined
- [ ] Monitoring/alerting configured
- [ ] Health check endpoint tested

## Step 1: Generate Licensing Private Key

The service requires an ECDSA P-256 private key for signing license tokens.

### Option A: Using .NET Tool

```bash
# Clone repository (if not already)
git clone https://github.com/your-org/MrWhoOidc.git
cd MrWhoOidc

# Generate key
dotnet run --project tools/KeyGenerator/KeyGenerator.csproj
```

This creates `secrets/licensing-private-key.pem`.

### Option B: Using OpenSSL

```bash
# Create secrets directory
mkdir -p secrets

# Generate ECDSA P-256 key
openssl ecparam -genkey -name prime256v1 -noout -out secrets/licensing-private-key.pem

# Set restrictive permissions (Linux)
chmod 600 secrets/licensing-private-key.pem
chown root:root secrets/licensing-private-key.pem
```

### Verify Key Format

```bash
# Should show: ASN1 OID: prime256v1 and NIST CURVE: P-256
openssl ec -in secrets/licensing-private-key.pem -text -noout
```

## Step 2: Build Docker Image

### Using Dockerfile

```bash
# From repository root
docker build -t mrwhooidc-keygen:1.0 -f MrWhoOidc.KeyGen/Dockerfile .
```

**Build verification:**
- Build time: ~20-30 seconds (first build)
- Image size: ~404MB (optimized)
- Base image: `mcr.microsoft.com/dotnet/aspnet:9.0`

### Using Docker Compose

```bash
docker-compose -f docker-compose-keygen.yml build
```

## Step 3: Configure Environment

### Environment Variables

Create an `.env` file for Docker Compose:

```env
# Application
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:8080

# Database
CONNECTION_STRING=Data Source=/data/keygen.db

# Licensing
LICENSING_KEY_PATH=/secrets/licensing-private-key.pem

# Optional: Logging
LOGGING__LOGLEVEL__DEFAULT=Information
LOGGING__LOGLEVEL__MICROSOFT_ASPNETCORE=Warning
```

### appsettings.Production.json

For custom configuration, mount an override file:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "MrWhoOidc.KeyGen": "Information"
    }
  },
  "ConnectionStrings": {
    "KeyGenDb": "Data Source=/data/keygen.db"
  },
  "KeyGen": {
    "LicensingPrivateKeyPath": "/secrets/licensing-private-key.pem"
  }
}
```

## Step 4: Deploy Container

### Using Docker Run

```bash
docker run -d \
  --name keygen \
  --restart unless-stopped \
  -p 8080:8080 \
  -v keygen-data:/data \
  -v /path/to/secrets:/secrets:ro \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ConnectionStrings__KeyGenDb="Data Source=/data/keygen.db" \
  -e KeyGen__LicensingPrivateKeyPath="/secrets/licensing-private-key.pem" \
  --health-cmd="curl -f http://localhost:8080/health || exit 1" \
  --health-interval=30s \
  --health-timeout=3s \
  --health-retries=3 \
  mrwhooidc-keygen:1.0
```

### Using Docker Compose

```yaml
version: '3.8'

services:
  keygen:
    image: mrwhooidc-keygen:1.0
    container_name: keygen
    restart: unless-stopped
    ports:
      - "8080:8080"
    volumes:
      - keygen-data:/data
      - /path/to/secrets:/secrets:ro
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__KeyGenDb=Data Source=/data/keygen.db
      - KeyGen__LicensingPrivateKeyPath=/secrets/licensing-private-key.pem
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/health"]
      interval: 30s
      timeout: 3s
      retries: 3
    networks:
      - keygen-network
    logging:
      driver: "json-file"
      options:
        max-size: "10m"
        max-file: "3"

volumes:
  keygen-data:
    driver: local

networks:
  keygen-network:
    driver: bridge
```

Deploy:

```bash
docker-compose -f docker-compose-keygen.yml up -d
```

## Step 5: Verify Deployment

### Health Check

```bash
# Check container health
docker inspect --format='{{.State.Health.Status}}' keygen
# Expected: healthy

# Test health endpoint
curl http://localhost:8080/health
# Expected: 200 OK, body: "Healthy"
```

### Application Logs

```bash
# Follow logs
docker logs -f keygen

# Check for startup success
docker logs keygen | grep "Application started"

# Verify migrations applied
docker logs keygen | grep "Applying migration"
```

### Database Verification

```bash
# Check database file exists
docker exec keygen ls -lh /data/keygen.db

# Verify database is accessible
docker exec keygen sh -c "echo 'SELECT COUNT(*) FROM KeyPairMetadata;' | sqlite3 /data/keygen.db"
```

### Access Web UI

Navigate to `http://your-server-ip:8080` (or through reverse proxy with HTTPS).

**Expected:**
- Home page loads
- Navigation menu visible
- "Key Generation" and "License Generation" links work

## Step 6: Configure Reverse Proxy

### Nginx Configuration

```nginx
upstream keygen_backend {
    server localhost:8080;
}

server {
    listen 443 ssl http2;
    server_name keygen.yourdomain.com;

    ssl_certificate /etc/ssl/certs/keygen.crt;
    ssl_certificate_key /etc/ssl/private/keygen.key;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;

    access_log /var/log/nginx/keygen-access.log;
    error_log /var/log/nginx/keygen-error.log;

    # Security headers (additional to app's headers)
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;

    # Proxy to container
    location / {
        proxy_pass http://keygen_backend;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection keep-alive;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_cache_bypass $http_upgrade;
        
        # Timeouts
        proxy_connect_timeout 60s;
        proxy_send_timeout 60s;
        proxy_read_timeout 60s;
    }

    # Health check (bypass auth if needed)
    location /health {
        proxy_pass http://keygen_backend/health;
        access_log off;
    }
}

# HTTP redirect
server {
    listen 80;
    server_name keygen.yourdomain.com;
    return 301 https://$host$request_uri;
}
```

Reload nginx:

```bash
nginx -t && systemctl reload nginx
```

### Traefik Configuration

```yaml
# docker-compose-keygen.yml with Traefik labels
services:
  keygen:
    # ... existing config ...
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.keygen.rule=Host(`keygen.yourdomain.com`)"
      - "traefik.http.routers.keygen.entrypoints=websecure"
      - "traefik.http.routers.keygen.tls=true"
      - "traefik.http.routers.keygen.tls.certresolver=letsencrypt"
      - "traefik.http.services.keygen.loadbalancer.server.port=8080"
      - "traefik.http.middlewares.keygen-ratelimit.ratelimit.average=100"
      - "traefik.http.middlewares.keygen-ratelimit.ratelimit.burst=50"
      - "traefik.http.routers.keygen.middlewares=keygen-ratelimit"
```

## Step 7: Backup Strategy

### Database Backup

**Automated backup script:**

```bash
#!/bin/bash
# backup-keygen-db.sh

BACKUP_DIR="/backups/keygen"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
BACKUP_FILE="$BACKUP_DIR/keygen-$TIMESTAMP.db"

# Create backup directory
mkdir -p "$BACKUP_DIR"

# Backup database
docker run --rm \
  -v keygen-data:/data:ro \
  -v "$BACKUP_DIR:/backup" \
  alpine sh -c "cp /data/keygen.db /backup/keygen-$TIMESTAMP.db"

# Compress
gzip "$BACKUP_FILE"

# Keep only last 30 days
find "$BACKUP_DIR" -name "keygen-*.db.gz" -mtime +30 -delete

echo "Backup completed: ${BACKUP_FILE}.gz"
```

**Schedule with cron:**

```bash
# Daily backup at 2 AM
0 2 * * * /usr/local/bin/backup-keygen-db.sh >> /var/log/keygen-backup.log 2>&1
```

### Secrets Backup

```bash
# Backup licensing key (encrypted)
gpg --encrypt --recipient admin@yourdomain.com secrets/licensing-private-key.pem
mv secrets/licensing-private-key.pem.gpg /secure/backup/location/
```

## Step 8: Monitoring & Alerting

### Docker Health Checks

```bash
# Check health status
docker inspect --format='{{json .State.Health}}' keygen | jq
```

### Log Monitoring

Use a log aggregator (ELK, Splunk, Loki):

```yaml
# docker-compose-keygen.yml logging driver
logging:
  driver: "syslog"
  options:
    syslog-address: "tcp://logstash:5000"
    tag: "keygen"
```

### Prometheus Metrics (Future Enhancement)

Planned metrics endpoints:
- Key generation rate
- License generation rate
- Error rates
- Request latency

### Uptime Monitoring

Configure external monitoring (e.g., UptimeRobot, Pingdom):
- **URL**: `https://keygen.yourdomain.com/health`
- **Interval**: 5 minutes
- **Alert threshold**: 2 consecutive failures

## Maintenance

### Updates

```bash
# Pull new image
docker pull mrwhooidc-keygen:1.1

# Stop current container
docker stop keygen

# Remove old container (data persists in volume)
docker rm keygen

# Start new container with same volumes
docker run -d ... mrwhooidc-keygen:1.1
```

Or with Docker Compose:

```bash
docker-compose -f docker-compose-keygen.yml pull
docker-compose -f docker-compose-keygen.yml up -d
```

### Database Migrations

Migrations apply automatically on startup. To verify:

```bash
docker logs keygen | grep -i migration
```

### Scaling (Future)

For high availability:
1. Use PostgreSQL instead of SQLite
2. Deploy multiple container instances
3. Use shared volume or external database
4. Configure load balancer

## Troubleshooting

### Container Won't Start

```bash
# Check logs
docker logs keygen

# Common issues:
# 1. Port already in use
docker ps | grep 8080
# Solution: Use different port or stop conflicting service

# 2. Missing licensing key
docker exec keygen ls -l /secrets/
# Solution: Verify volume mount and file exists

# 3. Volume permissions
docker exec keygen ls -la /data/
# Solution: Ensure app user (1654) can write to /data
```

### Health Check Failing

```bash
# Test health endpoint from inside container
docker exec keygen curl -f http://localhost:8080/health

# Check database connection
docker exec keygen sh -c "sqlite3 /data/keygen.db 'SELECT 1;'"
```

### High Memory Usage

```bash
# Check memory usage
docker stats keygen --no-stream

# Set memory limit
docker update --memory 512m --memory-swap 1g keygen
```

### Database Locked

SQLite WAL mode should prevent most locking issues, but if encountered:

```bash
# Check for WAL files
docker exec keygen ls -l /data/keygen.db*

# Force checkpoint
docker exec keygen sh -c "sqlite3 /data/keygen.db 'PRAGMA wal_checkpoint(FULL);'"
```

## Security Hardening

### Container Security

```bash
# Run with security options
docker run -d \
  --security-opt=no-new-privileges:true \
  --read-only \
  --tmpfs /tmp:noexec,nosuid,size=100m \
  -v keygen-data:/data \
  -v secrets:/secrets:ro \
  mrwhooidc-keygen:1.0
```

### Network Isolation

```bash
# Create isolated network
docker network create --internal keygen-internal

# Run container on isolated network
docker run -d --network keygen-internal ...
```

### Secrets Management

Use Docker secrets instead of environment variables:

```yaml
services:
  keygen:
    secrets:
      - licensing-key
    environment:
      - KeyGen__LicensingPrivateKeyPath=/run/secrets/licensing-key

secrets:
  licensing-key:
    file: ./secrets/licensing-private-key.pem
```

## Performance Tuning

### SQLite Optimization

The application already uses WAL mode. For additional optimization:

```bash
# Analyze database
docker exec keygen sh -c "sqlite3 /data/keygen.db 'ANALYZE;'"

# Vacuum (reclaim space)
docker exec keygen sh -c "sqlite3 /data/keygen.db 'VACUUM;'"
```

### Container Resources

```bash
# Set CPU and memory limits
docker run -d \
  --cpus=2 \
  --memory=1g \
  --memory-swap=2g \
  mrwhooidc-keygen:1.0
```

## Rollback Procedure

If an update fails:

```bash
# Stop new version
docker stop keygen

# Start previous version
docker run -d \
  --name keygen \
  -v keygen-data:/data \
  -v secrets:/secrets:ro \
  mrwhooidc-keygen:1.0  # Previous version tag
```

Database remains intact due to persistent volume.

## Compliance & Audit

### Audit Logging

All key downloads and license generations are logged:

```bash
# Query audit trail
docker exec keygen sh -c "sqlite3 /data/keygen.db 'SELECT * FROM KeyDownloadRecords ORDER BY DownloadedAt DESC LIMIT 10;'"
```

### Compliance Checklist

- [ ] HTTPS enforced
- [ ] Private keys never stored server-side
- [ ] Audit trail enabled and backed up
- [ ] Access logs retained per policy (30-90 days)
- [ ] Licensing key stored securely (encrypted at rest)
- [ ] Regular security updates applied
- [ ] Vulnerability scanning enabled
- [ ] Incident response plan documented

## Support

- **Documentation**: See [README.md](./README.md) and [DOCKER.md](./DOCKER.md)
- **Health Check**: `curl http://localhost:8080/health`
- **Logs**: `docker logs keygen`
- **Database**: SQLite at `/data/keygen.db` in container

---

**Version History**:
- 1.0 (2025-10-28): Initial deployment guide
