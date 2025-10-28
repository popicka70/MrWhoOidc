# Docker Deployment Guide

This guide explains how to deploy the Key & License Management Service using Docker.

## Prerequisites

- Docker Engine 20.10+ or Docker Desktop
- Linux container support
- At least 512MB RAM available for the container

## Quick Start

### 1. Prepare Secrets Directory

```bash
mkdir -p secrets
```

Generate the ECDSA P-256 private key for license signing:

```bash
# Using OpenSSL (Linux/Mac)
openssl ecparam -genkey -name prime256v1 -noout -out secrets/licensing-private-key.pem

# Using .NET (Windows PowerShell - from repo root)
dotnet run --project tools/KeyGenerator/KeyGenerator.csproj
```

### 2. Build the Docker Image

```bash
docker build -t mrwhooidc-keygen:latest -f MrWhoOidc.KeyGen/Dockerfile .
```

Build output:
- **Image size**: ~404MB (optimized multi-stage build)
- **Build time**: ~20-25s (first build), ~1-3s (cached rebuilds)
- **Base images**: 
  - Build: `mcr.microsoft.com/dotnet/sdk:9.0`
  - Runtime: `mcr.microsoft.com/dotnet/aspnet:9.0`

### 3. Run the Container

```bash
docker run -d \
  --name keygen \
  -p 8080:8080 \
  -v keygen-data:/data \
  -v ./secrets:/secrets:ro \
  -e ASPNETCORE_ENVIRONMENT=Production \
  mrwhooidc-keygen:latest
```

Parameters:
- `-d`: Run in detached mode (background)
- `--name keygen`: Container name
- `-p 8080:8080`: Map host port 8080 to container port 8080
- `-v keygen-data:/data`: Named volume for SQLite database persistence
- `-v ./secrets:/secrets:ro`: Bind mount secrets directory (read-only)
- `-e ASPNETCORE_ENVIRONMENT=Production`: Set environment

### 4. Verify Deployment

```bash
# Check health endpoint
curl http://localhost:8080/health
# Expected: "Healthy"

# View logs
docker logs keygen

# Check container status
docker ps
```

## Using Docker Compose

### docker-compose-keygen.yml

```yaml
version: '3.8'

services:
  keygen:
    build:
      context: .
      dockerfile: MrWhoOidc.KeyGen/Dockerfile
    image: mrwhooidc-keygen:latest
    container_name: keygen
    ports:
      - "8080:8080"
    volumes:
      # Database persistence
      - keygen-data:/data
      # License signing key (read-only)
      - ./secrets:/secrets:ro
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - ConnectionStrings__KeyGenDb=Data Source=/data/keygen.db
      - KeyGen__LicensingPrivateKeyPath=/secrets/licensing-private-key.pem
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/health"]
      interval: 30s
      timeout: 3s
      retries: 3
    restart: unless-stopped
    networks:
      - keygen-network

volumes:
  keygen-data:
    driver: local

networks:
  keygen-network:
    driver: bridge
```

### Commands

```bash
# Start services
docker-compose -f docker-compose-keygen.yml up -d

# View logs
docker-compose -f docker-compose-keygen.yml logs -f

# Stop services
docker-compose -f docker-compose-keygen.yml down

# Stop and remove volumes (⚠️ DELETES ALL DATA)
docker-compose -f docker-compose-keygen.yml down -v
```

## Volume Management

### Database Volume

The `keygen-data` volume stores the SQLite database at `/data/keygen.db`.

```bash
# Inspect volume
docker volume inspect keygen-data

# Backup database
docker run --rm \
  -v keygen-data:/data \
  -v $(pwd):/backup \
  alpine sh -c "cp /data/keygen.db /backup/keygen-backup.db"

# Restore database
docker run --rm \
  -v keygen-data:/data \
  -v $(pwd):/backup \
  alpine sh -c "cp /backup/keygen-backup.db /data/keygen.db && chown 1654:1654 /data/keygen.db"
```

### Secrets Volume

The `./secrets` directory (bind mount) contains the ECDSA private key for license signing.

**Security notes:**
- Mounted as **read-only** (`:ro` flag)
- Key file: `/secrets/licensing-private-key.pem`
- Must be ECDSA P-256 curve
- Keep this key secure and backed up separately

## Configuration

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_ENVIRONMENT` | Production | Environment (Development/Staging/Production) |
| `ASPNETCORE_URLS` | `http://+:8080` | Listening URLs |
| `ConnectionStrings__KeyGenDb` | `Data Source=/data/keygen.db` | SQLite connection string |
| `KeyGen__LicensingPrivateKeyPath` | `/secrets/licensing-private-key.pem` | License signing key path |

### appsettings.json Override

Mount a custom `appsettings.Production.json`:

```bash
docker run -d \
  -v ./appsettings.Production.json:/app/appsettings.Production.json:ro \
  mrwhooidc-keygen:latest
```

## Security

### Container Security

- **Non-root user**: Runs as user `app` (UID 1654)
- **Minimal base image**: ASP.NET Core 9.0 runtime only (no SDK)
- **Read-only secrets**: Licensing key mounted read-only
- **No shell access**: Production image doesn't include shell tools

### Network Security

- **Internal network**: Use Docker networks to isolate services
- **Reverse proxy**: Put behind nginx/Traefik for TLS/authentication
- **Firewall rules**: Restrict port 8080 to trusted sources

### Data Protection

The application uses ASP.NET Core Data Protection for:
- Antiforgery tokens
- Session cookies

**Warning**: Data Protection keys are stored in `/home/app/.aspnet/DataProtection-Keys` inside the container and will be lost when the container is recreated.

For production:
1. Use a persistent volume for Data Protection keys:
   ```bash
   -v dataprotection-keys:/home/app/.aspnet/DataProtection-Keys
   ```
2. Or configure Azure Key Vault / Redis for key storage

## Monitoring

### Health Checks

```bash
# Docker health status
docker inspect --format='{{.State.Health.Status}}' keygen

# Manual health check
curl http://localhost:8080/health
```

Health check includes:
- Database connectivity (EF Core DbContext check)
- HTTP 200 response = healthy
- HTTP 503 response = unhealthy

### Logs

```bash
# Follow logs
docker logs -f keygen

# Last 100 lines
docker logs --tail 100 keygen

# Since timestamp
docker logs --since 2024-01-01T00:00:00 keygen
```

Log levels (controlled by `appsettings.json`):
- **Information**: Startup, configuration, key/license generation events
- **Warning**: Data Protection warnings, security concerns
- **Error**: Exceptions, failed operations
- **Debug**: (Development only) EF Core SQL queries

## Troubleshooting

### Container Won't Start

```bash
# Check logs
docker logs keygen

# Common issues:
# 1. Port 8080 already in use
#    Solution: Use different port -p 8090:8080

# 2. Missing licensing key
#    Solution: Ensure secrets/licensing-private-key.pem exists

# 3. Volume permissions
#    Solution: Check volume ownership (should be app:app / 1654:1654)
```

### Database Errors

```bash
# Check database file
docker exec keygen ls -lah /data/

# Apply migrations manually (if needed)
docker exec keygen dotnet ef database update --project /app/MrWhoOidc.KeyGen.dll
```

### 500 Internal Server Error

Check logs for:
- SQLite locking issues (ensure WAL mode is enabled)
- Missing configuration (appsettings.json)
- Invalid licensing key format

### High Memory Usage

Typical memory usage:
- **Idle**: ~50-80MB
- **Under load**: ~100-150MB

If higher:
- Check for memory leaks in logs
- Restart container: `docker restart keygen`
- Limit memory: `docker run -m 512m ...`

## Production Recommendations

1. **Use HTTPS**: Put behind a reverse proxy (nginx, Traefik) with TLS
2. **Backup database**: Schedule regular backups of `keygen-data` volume
3. **Secure secrets**: Use Docker secrets or external secret management (Vault, Azure Key Vault)
4. **Resource limits**: Set memory/CPU limits (`-m 512m --cpus=1`)
5. **Logging**: Configure structured logging to external log aggregator (ELK, Splunk)
6. **Monitoring**: Integrate with Prometheus/Grafana for metrics
7. **Auto-restart**: Use `restart: unless-stopped` or orchestrator (Kubernetes)
8. **Vulnerability scanning**: Run `docker scout quickview` regularly

## Kubernetes Deployment (Future)

For Kubernetes deployment, see `k8s/` directory (to be added in Phase 7).

Key considerations:
- Use ConfigMaps for appsettings
- Use Secrets for licensing key
- Use PersistentVolumeClaims for database
- Configure liveness/readiness probes pointing to `/health`
- Set resource requests/limits
- Use Ingress for TLS termination

## Support

For issues, see:
- **Documentation**: `docs/` directory
- **Logs**: `docker logs keygen`
- **Health**: `curl http://localhost:8080/health`
