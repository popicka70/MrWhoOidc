# Docker Security Best Practices for MrWhoOidc

**Version**: 1.0
**Last Updated**: 2025-11-02
**Target Audience**: Security engineers, operations teams

This document provides comprehensive security hardening guidelines for deploying MrWhoOidc in production environments using Docker.

## Table of Contents

1. [Security Overview](#security-overview)
2. [Container Security](#container-security)
3. [Network Security](#network-security)
4. [Secrets Management](#secrets-management)
5. [TLS/Certificate Security](#tlscertificate-security)
6. [Database Security](#database-security)
7. [Redis Security](#redis-security)
8. [Access Control](#access-control)
9. [Monitoring and Auditing](#monitoring-and-auditing)
10. [Compliance Considerations](#compliance-considerations)
11. [Security Checklist](#security-checklist)

## Security Overview

### Defense in Depth Strategy

MrWhoOidc implements multiple layers of security:

1. **Container Isolation**: Non-root user, read-only filesystems, minimal attack surface
2. **Network Segmentation**: Internal networks for database/cache, edge network for public access
3. **Secrets Protection**: Environment variables, volume mounts, external secret stores
4. **TLS Everywhere**: Encrypted communication, certificate validation
5. **Audit Logging**: Comprehensive logging of security events
6. **Regular Updates**: Automated image builds with security patches

### Threat Model

**Threats Addressed**:

- Container escape attempts
- Network-based attacks (MITM, eavesdropping)
- Credential theft (database passwords, TLS keys, API secrets)
- Unauthorized access to admin interfaces
- Data exfiltration from database/cache
- Denial of Service (DoS) attacks

**Out of Scope** (requires additional infrastructure):

- DDoS mitigation (use CloudFlare, AWS Shield, etc.)
- WAF (Web Application Firewall) - deploy nginx/Traefik with ModSecurity
- Rate limiting at edge (use reverse proxy or API gateway)
- Advanced threat detection (use SIEM like Splunk, ELK)

## Container Security

### 1. Run as Non-Root User

MrWhoOidc containers run as non-root by default:

```dockerfile
# Already configured in Dockerfile
USER 1654
```

**Verification**:

```bash
# Check user in running container
docker compose exec webauth whoami
# Should output: "appuser" or numeric UID 1654, NOT root
```

**Why Important**: Prevents container escape exploits from gaining root access on host.

### 2. Use Minimal Base Images

```dockerfile
# Dockerfile uses chiseled Ubuntu base
FROM mcr.microsoft.com/dotnet/aspnet:9.0-jammy-chiseled
```

**Benefits**:

- Smaller attack surface (no shell, no package manager)
- Reduced image size (fewer vulnerabilities to patch)
- Faster security scanning

### 3. Read-Only Root Filesystem

For webauth container, consider read-only root filesystem:

```yaml
services:
  webauth:
    image: ghcr.io/popicka70/mrwhooidc:latest
    read_only: true
    tmpfs:
      - /tmp
      - /var/tmp
```

**Important**: Test thoroughly - some features may require write access to specific paths.

### 4. Drop Unnecessary Capabilities

```yaml
services:
  webauth:
    cap_drop:
      - ALL
    cap_add:
      - NET_BIND_SERVICE  # Only if binding to ports <1024
```

### 5. Limit Resources

Prevent DoS from resource exhaustion:

```yaml
services:
  webauth:
    deploy:
      resources:
        limits:
          cpus: '2'
          memory: 1G
        reservations:
          cpus: '0.5'
          memory: 512M
```

### 6. Security Scanning

Scan images for vulnerabilities:

```bash
# Using Docker Scout
docker scout cves ghcr.io/popicka70/mrwhooidc:latest

# Using Trivy
trivy image ghcr.io/popicka70/mrwhooidc:latest

# Using Grype
grype ghcr.io/popicka70/mrwhooidc:latest
```

**Best Practice**: Scan on every build in CI/CD pipeline.

### 7. Image Signing and Verification

Sign images with Docker Content Trust:

```bash
# Enable Docker Content Trust
export DOCKER_CONTENT_TRUST=1

# Pull only signed images
docker pull ghcr.io/popicka70/mrwhooidc:latest
```

**For Publishers**: Sign images in CI/CD:

```yaml
# .github/workflows/docker-publish.yml
- name: Sign image
  run: |
    docker trust sign ghcr.io/popicka70/mrwhooidc:${{ github.ref_name }}
```

## Network Security

### 1. Network Segmentation

**Architecture**:

- **Internal Network**: Database and Redis (isolated, no external access)
- **Edge Network**: Webauth container (public access on 8443/443)

```yaml
networks:
  internal:
    driver: bridge
    internal: true  # CRITICAL: No external access
  edge:
    driver: bridge

services:
  webauth:
    networks:
      - internal  # Access to database/redis
      - edge      # Public access
  
  postgres:
    networks:
      - internal  # Isolated, no public access
  
  redis:
    networks:
      - internal  # Isolated, no public access
```

**Verification**:

```bash
# Postgres should NOT have external connectivity
docker compose exec postgres ping -c 1 google.com
# Should fail: "ping: bad address 'google.com'"
```

### 2. Firewall Rules

**Host Firewall** (iptables/firewalld):

```bash
# Allow only HTTPS traffic to webauth
iptables -A INPUT -p tcp --dport 8443 -j ACCEPT
iptables -A INPUT -p tcp --dport 443 -j ACCEPT

# Block direct access to PostgreSQL/Redis ports
iptables -A INPUT -p tcp --dport 5432 -j DROP
iptables -A INPUT -p tcp --dport 6379 -j DROP

# Allow established connections
iptables -A INPUT -m state --state ESTABLISHED,RELATED -j ACCEPT
```

**Cloud Security Groups** (AWS/Azure/GCP):

- Inbound: HTTPS (443) from 0.0.0.0/0
- Outbound: All traffic (for pulling images, SMTP, etc.)
- No public access to 5432 (PostgreSQL), 6379 (Redis)

### 3. Reverse Proxy with TLS Termination

**Recommended Architecture**:

```
Internet → Reverse Proxy (nginx/Traefik) → Webauth Container
                ↓ TLS termination
```

**nginx Configuration**:

```nginx
upstream mrwhooidc {
    server localhost:8443;
}

server {
    listen 443 ssl http2;
    server_name auth.company.com;

    # TLS Configuration
    ssl_certificate /etc/nginx/ssl/cert.pem;
    ssl_certificate_key /etc/nginx/ssl/key.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;

    # Security Headers
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;
    add_header X-Frame-Options "DENY" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-XSS-Protection "1; mode=block" always;

    # Rate Limiting
    limit_req_zone $binary_remote_addr zone=auth:10m rate=10r/s;
    limit_req zone=auth burst=20 nodelay;

    location / {
        proxy_pass https://mrwhooidc;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

**Traefik Configuration** (docker-compose.yml):

```yaml
services:
  traefik:
    image: traefik:v2.10
    command:
      - --providers.docker=true
      - --entrypoints.websecure.address=:443
      - --certificatesresolvers.letsencrypt.acme.email=admin@company.com
      - --certificatesresolvers.letsencrypt.acme.storage=/acme.json
      - --certificatesresolvers.letsencrypt.acme.httpchallenge.entrypoint=web
    ports:
      - "443:443"
      - "80:80"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
      - ./acme.json:/acme.json
    networks:
      - edge

  webauth:
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.mrwhooidc.rule=Host(`auth.company.com`)"
      - "traefik.http.routers.mrwhooidc.entrypoints=websecure"
      - "traefik.http.routers.mrwhooidc.tls.certresolver=letsencrypt"
      - "traefik.http.middlewares.ratelimit.ratelimit.average=100"
      - "traefik.http.middlewares.ratelimit.ratelimit.burst=50"
```

### 4. Disable Unnecessary Ports

Only expose required ports:

```yaml
services:
  webauth:
    ports:
      - "8443:8443"  # HTTPS only
      # DO NOT expose 8080 (HTTP) in production
```

### 5. Network Encryption

**PostgreSQL TLS** (production recommendation):

```yaml
postgres:
  command: >
    postgres
    -c ssl=on
    -c ssl_cert_file=/etc/ssl/certs/server.crt
    -c ssl_key_file=/etc/ssl/private/server.key
  volumes:
    - ./certs/postgres-server.crt:/etc/ssl/certs/server.crt:ro
    - ./certs/postgres-server.key:/etc/ssl/private/server.key:ro
```

Update connection string:

```yaml
webauth:
  environment:
    ConnectionStrings__authdb: "Host=postgres;Database=authdb;Username=oidc;Password=${POSTGRES_PASSWORD};SSL Mode=Require"
```

**Redis TLS** (if using Redis Cloud or requiring encryption):

```yaml
redis:
  command: >
    redis-server
    --tls-port 6379
    --port 0
    --tls-cert-file /etc/redis/certs/redis.crt
    --tls-key-file /etc/redis/certs/redis.key
    --tls-ca-cert-file /etc/redis/certs/ca.crt
```

## Secrets Management

### 1. Never Commit Secrets

**Critical Files** (add to `.gitignore`):

```gitignore
.env
certs/*.pfx
certs/*.key
secrets/
```

**Verification**:

```bash
# Check repository for leaked secrets
git log -p | grep -i "password\|secret\|key" --color

# Use tools
trufflehog git file://. --only-verified
gitleaks detect --source . -v
```

### 2. Environment Variable Security

**Development** (`.env` file):

```bash
# Set restrictive permissions
chmod 600 .env

# Verify
ls -la .env
# Should show: -rw------- (owner read/write only)
```

**Production Options**:

#### Option A: Docker Secrets (Swarm Mode)

```yaml
services:
  webauth:
    secrets:
      - postgres_password
      - cert_password
    environment:
      POSTGRES_PASSWORD_FILE: /run/secrets/postgres_password
      CERT_PASSWORD_FILE: /run/secrets/cert_password

secrets:
  postgres_password:
    external: true
  cert_password:
    external: true
```

Create secrets:

```bash
echo "strong-password" | docker secret create postgres_password -
echo "cert-password" | docker secret create cert_password -
```

#### Option B: HashiCorp Vault

```bash
# Fetch secrets from Vault at runtime
export POSTGRES_PASSWORD=$(vault kv get -field=password secret/mrwhooidc/postgres)
export CERT_PASSWORD=$(vault kv get -field=password secret/mrwhooidc/cert)

docker compose up -d
```

#### Option C: Cloud Provider Secrets

**AWS Secrets Manager**:

```bash
# Fetch from AWS Secrets Manager
export POSTGRES_PASSWORD=$(aws secretsmanager get-secret-value \
  --secret-id mrwhooidc/postgres-password \
  --query SecretString \
  --output text)
```

**Azure Key Vault**:

```bash
# Fetch from Azure Key Vault
export POSTGRES_PASSWORD=$(az keyvault secret show \
  --vault-name mrwhooidc-vault \
  --name postgres-password \
  --query value \
  --output tsv)
```

**Google Secret Manager**:

```bash
# Fetch from GCP Secret Manager
export POSTGRES_PASSWORD=$(gcloud secrets versions access latest \
  --secret="mrwhooidc-postgres-password")
```

### 3. Certificate Security

**Storage**:

```bash
# Store certificates with restrictive permissions
chmod 600 certs/aspnetapp.pfx
chmod 600 certs/*.key
chmod 644 certs/*.crt

# Verify
ls -la certs/
```

**Rotation**:

```bash
# Rotate certificates annually (Let's Encrypt: every 90 days)
# 1. Generate new certificate
# 2. Update certs/ directory
# 3. Restart webauth: docker compose restart webauth
```

**Backup**:

```bash
# Backup certificates to secure location (encrypted)
tar czf certs-backup-$(date +%Y%m%d).tar.gz certs/
gpg --encrypt --recipient admin@company.com certs-backup-*.tar.gz
# Store encrypted backup offsite
```

### 4. Database Password Security

**Requirements**:

- Minimum 32 characters
- Alphanumeric + special characters
- Unique per environment
- Rotated regularly (quarterly)

**Generation**:

```bash
# Generate strong password
openssl rand -base64 32

# Or using pwgen
pwgen -s 32 1
```

**Rotation Procedure**:

```bash
# 1. Update password in secret store (Vault/AWS/Azure)
# 2. Update PostgreSQL password
docker compose exec postgres psql -U postgres -c "ALTER USER oidc WITH PASSWORD 'new-password';"

# 3. Update .env or secret
POSTGRES_PASSWORD=new-password

# 4. Restart webauth
docker compose restart webauth

# 5. Verify
docker compose logs webauth | grep "database connection"
```

## TLS/Certificate Security

### 1. Certificate Requirements

**Production Certificates**:

- Issued by trusted Certificate Authority (Let's Encrypt, DigiCert, etc.)
- Valid for domain name (not localhost)
- Key size: RSA 2048-bit minimum (4096-bit recommended) or ECDSA P-256
- Validity: 90 days (Let's Encrypt) or 1 year maximum
- Include intermediate certificates (full chain)

### 2. Let's Encrypt Automation

**Using Certbot**:

```bash
# Install certbot
apt-get install certbot

# Obtain certificate (standalone mode - stops on port 80)
certbot certonly --standalone -d auth.company.com

# Certificates saved to:
# /etc/letsencrypt/live/auth.company.com/fullchain.pem
# /etc/letsencrypt/live/auth.company.com/privkey.pem

# Convert to PFX for ASP.NET Core
openssl pkcs12 -export \
  -out ./certs/aspnetapp.pfx \
  -inkey /etc/letsencrypt/live/auth.company.com/privkey.pem \
  -in /etc/letsencrypt/live/auth.company.com/fullchain.pem \
  -password pass:YourCertPassword

# Auto-renewal (cron)
0 0 * * * certbot renew --post-hook "docker compose restart webauth"
```

### 3. TLS Configuration

**Enforce TLS 1.2+ only**:

```yaml
webauth:
  environment:
    Kestrel__Endpoints__Https__Protocols: Http1AndHttp2
    Kestrel__Endpoints__Https__SslProtocols: Tls12,Tls13
```

### 4. HTTP Strict Transport Security (HSTS)

Enable HSTS in reverse proxy (see nginx config above) or application:

```yaml
webauth:
  environment:
    HSTS_ENABLED: true
    HSTS_MAX_AGE: 31536000  # 1 year
```

## Database Security

### 1. PostgreSQL Hardening

**Connection Limits**:

```yaml
postgres:
  command: >
    postgres
    -c max_connections=100
    -c shared_buffers=256MB
    -c log_connections=on
    -c log_disconnections=on
```

**Authentication**:

```bash
# Edit pg_hba.conf (inside container)
docker compose exec postgres bash
echo "host all all 0.0.0.0/0 scram-sha-256" >> /var/lib/postgresql/data/pg_hba.conf
```

**Best Practice**: Use `scram-sha-256` instead of `md5` or `password`.

### 2. Database Encryption at Rest

**Volume Encryption**:

- **AWS**: Use EBS encrypted volumes
- **Azure**: Use Azure Disk Encryption
- **GCP**: Use encrypted persistent disks
- **On-Prem**: Use LUKS (Linux Unified Key Setup)

**Example (LUKS)**:

```bash
# Create encrypted volume
cryptsetup luksFormat /dev/sdb
cryptsetup open /dev/sdb postgres-encrypted

# Format and mount
mkfs.ext4 /dev/mapper/postgres-encrypted
mount /dev/mapper/postgres-encrypted /var/lib/docker/volumes/mrwhooidc_postgres-data/_data
```

### 3. Backup Encryption

```bash
# Encrypt backups with GPG
docker compose exec postgres pg_dump -U oidc authdb | \
  gzip | \
  gpg --encrypt --recipient admin@company.com \
  > backup-encrypted-$(date +%Y%m%d).sql.gz.gpg

# Decrypt when restoring
gpg --decrypt backup-encrypted-YYYYMMDD.sql.gz.gpg | \
  gunzip | \
  docker compose exec -T postgres psql -U oidc authdb
```

### 4. Audit Logging

Enable PostgreSQL audit logging:

```yaml
postgres:
  command: >
    postgres
    -c log_statement=all
    -c log_duration=on
    -c log_line_prefix='%t [%p]: user=%u,db=%d,app=%a,client=%h '
```

**Warning**: `log_statement=all` logs everything (verbose). For production, use `log_statement=ddl` or `mod`.

## Redis Security

### 1. Authentication

**Require password**:

```yaml
redis:
  command: redis-server --requirepass ${REDIS_PASSWORD} --save 60 1 --loglevel warning
```

Update connection string:

```yaml
webauth:
  environment:
    Redis__ConnectionString: "redis:6379,password=${REDIS_PASSWORD},abortConnect=false"
```

### 2. Disable Dangerous Commands

```yaml
redis:
  command: >
    redis-server
    --requirepass ${REDIS_PASSWORD}
    --rename-command FLUSHDB ""
    --rename-command FLUSHALL ""
    --rename-command CONFIG ""
    --save 60 1
```

### 3. Network Isolation

Redis should NEVER be exposed publicly:

```yaml
redis:
  networks:
    - internal  # Isolated network only
  # NO ports section - do not expose to host
```

## Access Control

### 1. Admin UI Access

**Restrict Admin Access**:

- Deploy admin UI on separate subdomain: `admin.auth.company.com`
- Use IP whitelisting in reverse proxy
- Require VPN or bastion host access
- Enable multi-factor authentication (MFA)

**nginx IP Whitelist**:

```nginx
location /admin {
    allow 10.0.0.0/8;      # Internal network
    allow 203.0.113.0/24;  # Office IP range
    deny all;

    proxy_pass https://mrwhooidc;
}
```

### 2. Container Shell Access

**Disable Shell in Production**:

```yaml
services:
  webauth:
    security_opt:
      - no-new-privileges:true
```

**Audit Shell Access**:

```bash
# Log all docker exec commands
auditctl -w /usr/bin/docker -p x -k docker_exec
```

### 3. Role-Based Access Control (RBAC)

**Docker Host Access**:

- Limit `docker` group membership
- Use separate accounts for deployment vs. maintenance
- Enable Docker authorization plugins (authz)

## Monitoring and Auditing

### 1. Security Event Logging

**Enable Audit Logs**:

```yaml
webauth:
  environment:
    Logging__LogLevel__MrWhoOidc.Auth: Information
    Logging__LogLevel__MrWhoOidc.WebAuth.Handlers: Information
```

**Critical Events to Log**:

- Failed authentication attempts
- Admin UI access (successful/failed)
- Client secret verification failures
- Token generation/validation
- Database connection failures
- Certificate loading errors

### 2. Log Aggregation

**Centralized Logging** (ELK Stack):

```yaml
services:
  webauth:
    logging:
      driver: "fluentd"
      options:
        fluentd-address: "fluentd:24224"
        tag: "mrwhooidc.webauth"
```

**Syslog**:

```yaml
webauth:
  logging:
    driver: "syslog"
    options:
      syslog-address: "udp://logserver:514"
      tag: "mrwhooidc"
```

### 3. Security Monitoring

**Metrics to Monitor**:

- Failed login attempts (rate/count)
- Database connection errors
- Certificate expiry dates
- Unusual traffic patterns
- Resource exhaustion (CPU/memory)

**Alerting Rules**:

```yaml
# Prometheus alert example
groups:
  - name: mrwhooidc_security
    rules:
      - alert: HighFailedLogins
        expr: rate(failed_logins_total[5m]) > 10
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "High rate of failed logins detected"
```

### 4. Intrusion Detection

**Host-Based IDS** (OSSEC/Wazuh):

```bash
# Monitor Docker logs for suspicious activity
<localfile>
  <log_format>json</log_format>
  <location>/var/lib/docker/containers/*/*-json.log</location>
</localfile>
```

**Container Runtime Security** (Falco):

```yaml
# Falco rules for MrWhoOidc
- rule: Unauthorized Process in Container
  condition: spawned_process and container.name = "mrwhooidc-webauth" and not proc.name in (dotnet)
  output: "Unexpected process in MrWhoOidc container (proc=%proc.name)"
  priority: WARNING
```

## Compliance Considerations

### 1. GDPR Compliance

**Data Protection**:

- Encrypt personal data at rest (database encryption)
- Encrypt data in transit (TLS everywhere)
- Implement data retention policies (backup retention)
- Enable audit logging for access to personal data
- Support data export (user data portability)
- Support data deletion (right to be forgotten)

### 2. PCI-DSS (if handling payment data)

**Requirements**:

- TLS 1.2+ only
- Strong cryptography (AES-256)
- Restrict network access (firewall rules)
- Audit all access to cardholder data
- Regular security scanning
- Implement access control (RBAC)

### 3. HIPAA (if handling health data)

**Requirements**:

- Encrypt data at rest and in transit
- Implement access controls and audit logging
- Regular security risk assessments
- Business Associate Agreements (BAAs)
- Breach notification procedures

### 4. SOC 2 Type II

**Requirements**:

- Document security policies and procedures
- Implement continuous monitoring
- Regular penetration testing
- Incident response plan
- Change management process
- Vendor risk management

## Security Checklist

Use this checklist for production deployments:

### Container Security

- [ ] **Non-root user**: Verified container runs as UID 1654
- [ ] **Minimal base image**: Using chiseled Ubuntu image
- [ ] **Read-only filesystem**: Enabled (if applicable)
- [ ] **Dropped capabilities**: Only necessary capabilities enabled
- [ ] **Resource limits**: CPU and memory limits configured
- [ ] **Image scanning**: No critical/high vulnerabilities in image
- [ ] **Image signing**: Docker Content Trust enabled (if applicable)

### Network Security

- [ ] **Network segmentation**: Internal network isolated (no external access)
- [ ] **Firewall rules**: Only 443/8443 accessible externally
- [ ] **Reverse proxy**: Deployed with TLS termination and rate limiting
- [ ] **Unnecessary ports closed**: HTTP port 8080 not exposed in production
- [ ] **Database TLS**: PostgreSQL configured with SSL (if required)
- [ ] **Redis isolation**: Redis not exposed to public network

### Secrets Management

- [ ] **No committed secrets**: `.env` in `.gitignore`, no secrets in git history
- [ ] **Strong passwords**: 32+ character passwords for database/Redis
- [ ] **File permissions**: `.env` and certificates have 600/644 permissions
- [ ] **Secret rotation**: Documented procedure for rotating secrets
- [ ] **External secret store**: Using Vault/AWS/Azure for production (if applicable)

### TLS/Certificates

- [ ] **Valid certificates**: Production certificates from trusted CA
- [ ] **TLS 1.2+ only**: Older protocols disabled
- [ ] **Strong ciphers**: Weak ciphers disabled
- [ ] **HSTS enabled**: Strict-Transport-Security header set
- [ ] **Certificate expiry monitoring**: Alerts configured for expiring certificates
- [ ] **Auto-renewal**: Certbot or similar configured (if using Let's Encrypt)

### Database Security

- [ ] **Strong password**: PostgreSQL password meets requirements
- [ ] **Connection limits**: max_connections configured
- [ ] **Encryption at rest**: Volume encryption enabled (if required)
- [ ] **Backup encryption**: Backups encrypted with GPG or similar
- [ ] **Audit logging**: PostgreSQL logging configured
- [ ] **Network isolation**: Database only accessible from internal network

### Access Control

- [ ] **Admin UI restricted**: IP whitelist or VPN required for /admin
- [ ] **Shell access disabled**: no-new-privileges enabled
- [ ] **RBAC implemented**: Docker host access restricted to authorized users
- [ ] **MFA enabled**: Multi-factor authentication for admin accounts (if applicable)

### Monitoring and Auditing

- [ ] **Security logging enabled**: Audit logs for authentication/authorization events
- [ ] **Log aggregation**: Logs sent to centralized logging system
- [ ] **Alerting configured**: Alerts for security events (failed logins, errors)
- [ ] **Metrics monitored**: Prometheus/Grafana monitoring security metrics
- [ ] **IDS deployed**: Falco/OSSEC monitoring container activity (if applicable)

### Compliance

- [ ] **Data encryption**: At rest and in transit encryption enabled
- [ ] **Audit trail**: Comprehensive audit logging configured
- [ ] **Retention policy**: Backup retention meets compliance requirements
- [ ] **Incident response**: Documented incident response plan
- [ ] **Regular scanning**: Scheduled vulnerability scans configured
- [ ] **Penetration testing**: Annual penetration tests scheduled

### Operational Security

- [ ] **Regular updates**: Automated image rebuilds for security patches
- [ ] **Backup tested**: Restore procedure tested successfully
- [ ] **Rollback plan**: Documented rollback procedure
- [ ] **Change management**: Change approval process documented
- [ ] **Documentation current**: Security documentation up to date
- [ ] **Team trained**: Operations team trained on security procedures

## Support and Resources

- **Security Issues**: Report to `security@mrwhooidc.com` (or create private issue)
- **Security Advisories**: Subscribe to GitHub security advisories
- **Best Practices**: OWASP Docker Security Cheat Sheet
- **Compliance**: PCI-DSS, HIPAA, GDPR compliance guides

**Document Version**: 1.0
**Last Updated**: 2025-11-02
**Maintained By**: MrWhoOidc Security Team
