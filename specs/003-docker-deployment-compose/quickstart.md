# Quick Start: MrWhoOidc Docker Deployment

**Target Audience**: Operations engineers, DevOps teams, system administrators  
**Time to Complete**: 10-15 minutes  
**Prerequisites**: Docker 20.10+, Docker Compose v2.0+

## What You'll Deploy

- **MrWhoOidc OIDC Server**: OpenID Connect Provider (authentication server)
- **PostgreSQL 16**: Database for OIDC entities (clients, users, sessions, keys)
- **Redis 7.2** (optional): Session cache for improved performance

## Quick Start (Minimal Configuration)

### 1. Create Deployment Directory

```bash
mkdir mrwhooidc && cd mrwhooidc
```

### 2. Download Docker Compose File

```bash
curl -O https://raw.githubusercontent.com/popicka70/MrWhoOidc/main/docker-compose.yml
```

### 3. Create Environment File

Create `.env` file with minimum required configuration:

```bash
cat > .env << 'EOF'
# Database Configuration
POSTGRES_PASSWORD=changeme_strong_password_here

# OIDC Configuration
OIDC_PUBLIC_BASE_URL=https://localhost:8443

# TLS Certificate Password
CERT_PASSWORD=changeit
EOF
```

### 4. Download Development Certificate

For testing/development only (use real certificates in production):

```bash
mkdir certs
curl -o certs/aspnetapp.pfx https://raw.githubusercontent.com/popicka70/MrWhoOidc/main/certs/aspnetapp.pfx
```

### 5. Start Services

```bash
docker compose up -d
```

### 6. Verify Deployment

Wait ~30 seconds for startup, then check discovery endpoint:

```bash
curl -k https://localhost:8443/.well-known/openid-configuration
```

Expected output: JSON with OIDC metadata including `issuer`, `authorization_endpoint`, `token_endpoint`, etc.

### 7. Access Admin UI

Open browser to: `https://localhost:8443/admin`

Default credentials will be created on first startup (check container logs for initial admin credentials).

## Configuration Options

### Enable Redis (Recommended for Production)

Edit `docker-compose.yml` to uncomment Redis service and connection string, or use override:

```bash
docker compose -f docker-compose.yml -f docker-compose.redis.yml up -d
```

### Enable Multi-Tenancy

Add to `.env`:

```bash
MULTITENANT_ENABLED=true
MULTITENANT_DEFAULT_TENANT_SLUG=default
```

### Configure SMTP (Email Notifications)

Add to `.env`:

```bash
MAIL_ENABLED=true
MAIL_SMTP_HOST=smtp.example.com
MAIL_SMTP_PORT=587
MAIL_FROM_ADDRESS=no-reply@example.com
MAIL_FROM_NAME=MrWhoOidc
```

### Production TLS Certificates

Replace development certificate with your own:

1. Obtain certificate from Let's Encrypt, commercial CA, or internal PKI
2. Convert to PFX format if necessary
3. Place in `./certs/` directory
4. Update `CERT_PASSWORD` in `.env`

Example Let's Encrypt conversion:

```bash
openssl pkcs12 -export -out certs/production.pfx \
  -inkey privkey.pem -in cert.pem -certfile chain.pem
```

Update compose file to mount new certificate:

```yaml
volumes:
  - ./certs/production.pfx:/https/production.pfx:ro
environment:
  ASPNETCORE_Kestrel__Certificates__Default__Path: /https/production.pfx
```

## Upgrading to New Version

### 1. Backup Database

```bash
docker exec mrwhooidc-postgres pg_dump -U oidc authdb > backup-$(date +%Y%m%d).sql
```

### 2. Update Image Tag

Edit `docker-compose.yml`:

```yaml
services:
  webauth:
    image: ghcr.io/popicka70/mrwhooidc:v1.2.3  # Update version here
```

### 3. Pull New Image and Restart

```bash
docker compose pull
docker compose up -d
```

Database migrations run automatically on startup.

### 4. Verify Upgrade

```bash
docker compose logs webauth | grep -i migration
docker compose ps
```

## Troubleshooting

### Container Won't Start

Check logs:

```bash
docker compose logs webauth
```

Common issues:

- **Database connection failed**: Check `POSTGRES_PASSWORD` matches in `.env` and compose file
- **Certificate not found**: Verify certificate path and permissions
- **Port already in use**: Change `ports` mapping in compose file

### Database Connection Timeout

Ensure PostgreSQL is healthy:

```bash
docker compose ps postgres
# Status should show "healthy"
```

If not healthy, check PostgreSQL logs:

```bash
docker compose logs postgres
```

### Redis Connection Warnings

Redis failures are non-fatal (graceful degradation). To disable Redis warnings:

```bash
# In .env
REDIS_ENABLED=false
```

Or fix Redis connectivity:

```bash
docker compose ps redis
docker compose logs redis
```

### Discovery Endpoint Returns 404

- Verify `OIDC_PUBLIC_BASE_URL` matches your deployment URL
- Check that container is listening on correct port:

```bash
docker compose exec webauth netstat -tlnp
```

## Next Steps

### 1. Configure OAuth Clients

Use admin UI at `https://localhost:8443/admin/clients` to:

- Create OAuth/OIDC clients for your applications
- Configure redirect URIs, scopes, grant types
- Generate client secrets

### 2. Set Up Users

- Use admin UI at `https://localhost:8443/admin/users` to create users
- Or configure external identity provider integration

### 3. Review Security

For production deployments:

- Replace development certificate with valid TLS certificate
- Use strong, randomly generated passwords (32+ characters)
- Deploy behind reverse proxy (nginx, Traefik) for additional security
- Enable rate limiting on reverse proxy
- Configure firewall rules to restrict database access
- Set up monitoring and log aggregation

### 4. Backup Strategy

Implement regular database backups:

```bash
# Example cron job (daily backup at 2 AM)
0 2 * * * docker exec mrwhooidc-postgres pg_dump -U oidc authdb | gzip > /backups/mrwhooidc-$(date +\%Y\%m\%d).sql.gz
```

Retain backups according to your retention policy (e.g., 30 days).

## Architecture Overview

```text
┌─────────────────┐
│   Internet      │
└────────┬────────┘
         │ HTTPS (8443)
         │
┌────────▼────────────────────────┐
│  MrWhoOidc.WebAuth              │
│  (OIDC Provider)                │
│  - Discovery endpoint           │
│  - Authorization/Token/UserInfo │
│  - Admin UI                     │
└──────┬─────────────┬────────────┘
       │             │
       │ authdb     │ cache
       │             │
┌──────▼─────┐  ┌───▼─────┐
│ PostgreSQL │  │  Redis  │
│  (persist) │  │ (cache) │
└────────────┘  └─────────┘
```

## Environment Variables Reference

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `POSTGRES_PASSWORD` | Yes | (none) | PostgreSQL superuser password |
| `OIDC_PUBLIC_BASE_URL` | Yes | (none) | Public URL where OIDC server is accessible |
| `CERT_PASSWORD` | Yes | (none) | Password for TLS certificate PFX file |
| `REDIS_ENABLED` | No | `false` | Enable Redis caching |
| `MULTITENANT_ENABLED` | No | `false` | Enable multi-tenant mode |
| `MAIL_ENABLED` | No | `false` | Enable email notifications |
| `ASPNETCORE_ENVIRONMENT` | No | `Production` | ASP.NET Core environment |
| `LOGGING_LEVEL` | No | `Information` | Minimum log level |

For complete list, see [deployment-guide.md](../../docs/deployment-guide.md).

## Support & Documentation

- **Deployment Guide**: [docs/deployment-guide.md](../../docs/deployment-guide.md)
- **Configuration Examples**: [docs/docker-compose-examples.md](../../docs/docker-compose-examples.md)
- **Upgrade Guide**: [docs/upgrade-guide.md](../../docs/upgrade-guide.md)
- **Admin Guide**: [docs/admin-guide.md](../../docs/admin-guide.md)
- **GitHub Issues**: [https://github.com/popicka70/MrWhoOidc/issues](https://github.com/popicka70/MrWhoOidc/issues)

## License

MrWhoOidc is licensed under [LICENSE]. See repository for details.
