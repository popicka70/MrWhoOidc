# MrWhoOidc Upgrade Guide

**Version**: 1.0
**Last Updated**: 2025-11-02
**Target Audience**: Operations engineers, DevOps teams

This guide covers upgrading your MrWhoOidc deployment to newer versions safely with minimal downtime.

## Table of Contents

1. [Pre-Upgrade Checklist](#pre-upgrade-checklist)
2. [Backup Procedures](#backup-procedures)
3. [Upgrade Steps](#upgrade-steps)
4. [Automatic Database Migrations](#automatic-database-migrations)
5. [Version Pinning Strategy](#version-pinning-strategy)
6. [Rollback Procedure](#rollback-procedure)
7. [Verification Steps](#verification-steps)
8. [Troubleshooting Failed Upgrades](#troubleshooting-failed-upgrades)
9. [Upgrade Testing Checklist](#upgrade-testing-checklist)
10. [Backup Retention Policy](#backup-retention-policy)

## Pre-Upgrade Checklist

Complete this checklist BEFORE starting any upgrade:

### Review Phase

- [ ] **Read Release Notes**: Review changelog for breaking changes, new features, deprecations
- [ ] **Check Compatibility**: Verify target version compatible with your PostgreSQL/Redis versions
- [ ] **Review Migration Impact**: Check if database schema changes are included
- [ ] **Plan Maintenance Window**: Schedule appropriate downtime (typically 5-15 minutes)
- [ ] **Notify Users**: Inform users of scheduled maintenance

### Backup Phase

- [ ] **Backup Database**: Complete PostgreSQL dump created and verified
- [ ] **Backup Configuration**: Copy `.env` file and `docker-compose.yml` to safe location
- [ ] **Backup TLS Certificates**: Ensure certificates backed up (if not in version control)
- [ ] **Document Current Version**: Note current image tag for rollback reference
- [ ] **Test Backup Restore**: Verify backup can be restored (if critical deployment)

### Environment Phase

- [ ] **Disk Space Check**: Ensure sufficient disk space (images, volumes, logs)
- [ ] **Resource Availability**: Confirm CPU/memory resources available
- [ ] **Network Connectivity**: Verify outbound access to GitHub Container Registry
- [ ] **Health Status**: All services healthy before upgrade (postgres, redis, webauth)

### Testing Phase (Production Upgrades)

- [ ] **Test in Staging**: Upgrade staging environment first if available
- [ ] **Verify Staging**: Confirm staging deployment successful
- [ ] **Load Test Staging**: Run smoke tests on upgraded staging environment

## Backup Procedures

### Database Backup (Required)

Create a full database backup before every upgrade:

```bash
# Create backup directory
mkdir -p backups

# Set backup filename with timestamp
BACKUP_FILE="backups/mrwhooidc-backup-$(date +%Y%m%d-%H%M%S).sql.gz"

# Create compressed backup
docker compose exec -T postgres pg_dump -U oidc authdb | gzip > "$BACKUP_FILE"

# Verify backup created
ls -lh "$BACKUP_FILE"
# Should show file size (e.g., 5.2M)

# Test backup integrity (optional but recommended)
gunzip -t "$BACKUP_FILE"
echo $?
# Should output: 0 (success)
```

**Backup Script** (`backups/backup-db.sh`):

```bash
#!/bin/bash
set -e

BACKUP_DIR="./backups"
TIMESTAMP=$(date +%Y%m%d-%H%M%S)
BACKUP_FILE="$BACKUP_DIR/mrwhooidc-backup-$TIMESTAMP.sql.gz"

echo "Creating backup: $BACKUP_FILE"

# Create backup
docker compose exec -T postgres pg_dump -U oidc authdb | gzip > "$BACKUP_FILE"

# Verify
if [ -f "$BACKUP_FILE" ]; then
    SIZE=$(du -h "$BACKUP_FILE" | cut -f1)
    echo "✓ Backup complete: $BACKUP_FILE ($SIZE)"
else
    echo "✗ Backup failed!"
    exit 1
fi
```

Make executable: `chmod +x backups/backup-db.sh`

### Configuration Backup

```bash
# Backup configuration files
cp .env backups/.env.backup-$(date +%Y%m%d-%H%M%S)
cp docker-compose.yml backups/docker-compose.yml.backup-$(date +%Y%m%d-%H%M%S)

# Verify
ls -lh backups/*.backup-*
```

### Full System Backup (Optional)

For critical deployments, backup entire Docker volumes:

```bash
# Stop services (optional - for consistent backup)
docker compose stop webauth

# Backup PostgreSQL volume
docker run --rm \
  -v mrwhooidc_postgres-data:/source:ro \
  -v $(pwd)/backups:/backup \
  alpine tar czf /backup/postgres-volume-$(date +%Y%m%d-%H%M%S).tar.gz -C /source .

# Backup Redis volume (if using Redis)
docker run --rm \
  -v mrwhooidc_redis-data:/source:ro \
  -v $(pwd)/backups:/backup \
  alpine tar czf /backup/redis-volume-$(date +%Y%m%d-%H%M%S).tar.gz -C /source .

# Restart services
docker compose start webauth
```

## Upgrade Steps

### Standard Upgrade Process

Follow these steps for zero-downtime or minimal-downtime upgrades:

#### Step 1: Backup (Required)

```bash
# Create database backup
./backups/backup-db.sh

# Backup configuration
cp .env backups/.env.backup-$(date +%Y%m%d-%H%M%S)
```

#### Step 2: Update Image Tag

Edit `docker-compose.yml` to specify new version:

```yaml
services:
  webauth:
    image: ghcr.io/popicka70/mrwhooidc:v1.2.0  # Update from v1.1.0
    # ... rest of configuration
```

**Alternative**: Use `.env` file for version management:

```bash
# Add to .env
MRWHOOIDC_VERSION=v1.2.0
```

Then in `docker-compose.yml`:

```yaml
services:
  webauth:
    image: ghcr.io/popicka70/mrwhooidc:${MRWHOOIDC_VERSION:-latest}
```

#### Step 3: Pull New Image

```bash
# Pull new image
docker compose pull webauth

# Verify new image downloaded
docker images | grep mrwhooidc
# Should show new version tag
```

#### Step 4: Stop Current Services

```bash
# Stop webauth service (keeps postgres/redis running)
docker compose stop webauth

# Or stop all services for major upgrades
docker compose down
# NOTE: 'down' stops all services but preserves volumes
```

#### Step 5: Start with New Image

```bash
# Start services with new image
docker compose up -d

# For major upgrades, use --force-recreate
docker compose up -d --force-recreate webauth
```

#### Step 6: Monitor Startup

```bash
# Watch logs for successful startup
docker compose logs -f webauth

# Look for:
# - "Applying database migration X" (if migrations present)
# - "Application started"
# - No error messages

# Press Ctrl+C to exit log view
```

#### Step 7: Verify Upgrade

See [Verification Steps](#verification-steps) section below.

### Quick Upgrade (One Command)

For non-critical environments:

```bash
# Backup, pull, and upgrade in one command
./backups/backup-db.sh && docker compose pull && docker compose up -d --force-recreate
```

### Blue-Green Deployment (Zero Downtime)

For critical deployments behind a load balancer:

1. Deploy new version on separate instance (green)
2. Verify green instance healthy
3. Switch load balancer to green instance
4. Decommission old instance (blue)

**Note**: Requires shared PostgreSQL and Redis between instances.

## Automatic Database Migrations

MrWhoOidc automatically applies database migrations on startup.

### How Migrations Work

- **Automatic**: No manual intervention required
- **Idempotent**: Safe to run multiple times (won't duplicate changes)
- **Transaction-Based**: Migrations run in transactions (roll back on failure)
- **Startup Sequence**: Migrations run before application accepts traffic

### Migration Behavior

```bash
# During upgrade, logs will show:
# 2025-11-02 10:00:00 [INF] Checking for pending migrations...
# 2025-11-02 10:00:01 [INF] Applying migration: 20251102_AddClientMetadata
# 2025-11-02 10:00:02 [INF] Migration applied successfully
# 2025-11-02 10:00:03 [INF] Database schema up to date
# 2025-11-02 10:00:04 [INF] Application starting...
```

### Zero-Downtime Migrations

**Backward-Compatible Changes** (safe during rolling updates):
- Adding nullable columns
- Adding new tables
- Adding indexes
- Adding non-unique constraints

**Non-Backward-Compatible Changes** (require downtime):
- Removing columns
- Renaming columns
- Changing column types
- Adding non-nullable columns without defaults

**Release notes will indicate if downtime is required.**

### Migration Failures

If migration fails:

1. **Container will NOT start** (safe - old version still serving if using blue-green)
2. **Check logs**: `docker compose logs webauth | grep -i migration`
3. **Database unchanged**: Partial migration rolled back
4. **Rollback required**: Restore previous image version

## Version Pinning Strategy

Choose your update strategy based on risk tolerance:

### Strategy 1: Pin to Specific Version (Recommended for Production)

```yaml
# docker-compose.yml
services:
  webauth:
    image: ghcr.io/popicka70/mrwhooidc:v1.2.3
```

**Pros**:
- Predictable: Exact version deployed
- Controlled: Manual approval for upgrades
- Auditable: Clear version in git history

**Cons**:
- Manual updates required
- May miss security patches

**Use Case**: Production deployments, compliance requirements

### Strategy 2: Pin to Minor Version

```yaml
services:
  webauth:
    image: ghcr.io/popicka70/mrwhooidc:v1.2
```

**Pros**:
- Auto-receive patch fixes (v1.2.x)
- No breaking changes (semantic versioning)

**Cons**:
- Unplanned upgrades on container restart

**Use Case**: Staging, pre-production environments

### Strategy 3: Pin to Major Version

```yaml
services:
  webauth:
    image: ghcr.io/popicka70/mrwhooidc:v1
```

**Pros**:
- Auto-receive minor and patch updates

**Cons**:
- Potential behavior changes

**Use Case**: Development, testing

### Strategy 4: Latest (Not Recommended for Production)

```yaml
services:
  webauth:
    image: ghcr.io/popicka70/mrwhooidc:latest
```

**Pros**:
- Always newest features

**Cons**:
- Unpredictable upgrades
- Potential breaking changes
- Hard to rollback (which "latest" was deployed?)

**Use Case**: Local development only

### Recommended Production Pattern

Use environment variable for flexibility:

```yaml
# docker-compose.yml
services:
  webauth:
    image: ghcr.io/popicka70/mrwhooidc:${MRWHOOIDC_VERSION:-v1.2.3}
```

```bash
# .env
MRWHOOIDC_VERSION=v1.2.3
```

**Benefits**:
- Version in `.env` (not committed to git)
- Easy to change without editing compose file
- Default fallback specified

## Rollback Procedure

If upgrade fails or introduces issues, rollback to previous version:

### Quick Rollback (Recommended)

Assumes database backup exists and database schema is backward-compatible.

```bash
# Step 1: Stop current services
docker compose stop webauth

# Step 2: Restore previous image version
# Edit docker-compose.yml or .env to previous version
# Example: v1.2.0 (previous) instead of v1.2.1 (failed)

# Step 3: Pull previous image (if not cached)
docker compose pull webauth

# Step 4: Start with previous version
docker compose up -d webauth

# Step 5: Verify
curl -k https://localhost:8443/.well-known/openid-configuration
```

### Full Rollback (With Database Restore)

Use if database migration is not backward-compatible:

```bash
# Step 1: Stop all services
docker compose down

# Step 2: Restore database backup
# List available backups
ls -lh backups/*.sql.gz

# Restore specific backup
gunzip < backups/mrwhooidc-backup-YYYYMMDD-HHMMSS.sql.gz | \
  docker compose run --rm -T postgres psql -h postgres -U oidc authdb

# Or if services are running:
gunzip < backups/mrwhooidc-backup-YYYYMMDD-HHMMSS.sql.gz | \
  docker compose exec -T postgres psql -U oidc authdb

# Step 3: Restore previous image version
# Edit docker-compose.yml to previous version

# Step 4: Start services
docker compose up -d

# Step 5: Verify
curl -k https://localhost:8443/.well-known/openid-configuration
```

### Emergency Rollback (Full System)

If database restore fails or complete rollback needed:

```bash
# Step 1: Stop and remove everything (DESTRUCTIVE)
docker compose down -v
# WARNING: -v removes volumes (data loss)

# Step 2: Restore volume backups (if available)
# Restore postgres volume
docker run --rm \
  -v mrwhooidc_postgres-data:/target \
  -v $(pwd)/backups:/backup \
  alpine sh -c "cd /target && tar xzf /backup/postgres-volume-YYYYMMDD-HHMMSS.tar.gz"

# Step 3: Restore configuration
cp backups/.env.backup-YYYYMMDD-HHMMSS .env
cp backups/docker-compose.yml.backup-YYYYMMDD-HHMMSS docker-compose.yml

# Step 4: Start services
docker compose up -d
```

## Verification Steps

After upgrade, verify system health:

### 1. Check Container Status

```bash
# All services should be "Up (healthy)"
docker compose ps

# Expected output:
# NAME                STATUS
# webauth            Up (healthy)
# postgres           Up (healthy)
# redis              Up (healthy)
```

### 2. Check Application Logs

```bash
# View recent logs
docker compose logs --tail=50 webauth

# Look for:
# - "Application started"
# - "Listening on https://[::]:8443"
# - No error messages
# - Migration success messages (if applicable)
```

### 3. Test Discovery Endpoint

```bash
# Test OIDC discovery endpoint
curl -k https://localhost:8443/.well-known/openid-configuration

# Should return JSON with OIDC metadata
# Check "issuer" field matches your OIDC_PUBLIC_BASE_URL
```

### 4. Test JWKS Endpoint

```bash
# Test JSON Web Key Set endpoint
curl -k https://localhost:8443/.well-known/jwks

# Should return JSON with signing keys
```

### 5. Test Admin UI

```bash
# Access admin interface
# https://localhost:8443/admin

# Verify:
# - Login page loads
# - Can authenticate
# - Client list loads
# - No JavaScript errors in browser console
```

### 6. Test Database Connectivity

```bash
# Check database connection
docker compose exec postgres psql -U oidc authdb -c "SELECT COUNT(*) FROM clients;"

# Should return count of clients (not an error)
```

### 7. Test Redis Connectivity (If Enabled)

```bash
# Test Redis connection
docker compose exec redis redis-cli ping

# Expected: PONG

# Check webauth connected to Redis
docker compose logs webauth | grep -i redis
# Should show: "Redis connection established"
```

### 8. Smoke Test Authentication Flow

Perform a basic authentication test:

1. Create test client in admin UI (or use existing)
2. Attempt authorization request
3. Verify token generation works
4. Verify token introspection works

### 9. Check Resource Usage

```bash
# Monitor resource usage
docker stats --no-stream

# Verify:
# - Memory usage within expected limits
# - CPU not pegged at 100%
```

### 10. Check Version

```bash
# Verify deployed version matches target
docker compose images webauth

# Or check application logs for version info
docker compose logs webauth | grep -i version
```

## Troubleshooting Failed Upgrades

### Problem: Container Won't Start After Upgrade

**Symptoms**:
- `docker compose ps` shows webauth as "Exited" or "Restarting"
- No "Application started" message in logs

**Diagnosis**:

```bash
# Check logs for errors
docker compose logs webauth

# Look for:
# - Migration errors
# - Configuration errors
# - Missing environment variables
# - Database connection failures
```

**Solutions**:

1. **Migration Failed**: Rollback to previous version (see Rollback Procedure)
2. **Configuration Error**: Check `.env` file for missing/incorrect values
3. **Database Connection**: Verify PostgreSQL healthy: `docker compose ps postgres`

### Problem: Migration Hangs or Times Out

**Symptoms**:
- Container starting but never reaches "Application started"
- Logs show "Applying migration X" but no completion

**Diagnosis**:

```bash
# Check if database locked
docker compose exec postgres psql -U oidc authdb -c "SELECT * FROM pg_locks WHERE NOT granted;"

# Check long-running queries
docker compose exec postgres psql -U oidc authdb -c "SELECT pid, now() - pg_stat_activity.query_start AS duration, query FROM pg_stat_activity WHERE state = 'active';"
```

**Solutions**:

1. **Cancel stuck migration**: Restart PostgreSQL: `docker compose restart postgres`
2. **Rollback**: Restore database backup and deploy previous version

### Problem: Application Starts But Errors on Requests

**Symptoms**:
- Container shows "healthy"
- Discovery endpoint returns errors
- Admin UI not accessible

**Diagnosis**:

```bash
# Test discovery endpoint with verbose output
curl -v -k https://localhost:8443/.well-known/openid-configuration

# Check for:
# - HTTP 500 errors
# - Connection refused
# - Timeout
```

**Solutions**:

1. **Check database schema**: Migration may have partially applied
2. **Verify configuration**: Ensure `OIDC_PUBLIC_BASE_URL` correct
3. **Check logs**: Look for exceptions: `docker compose logs webauth | grep -i exception`

### Problem: Performance Degraded After Upgrade

**Symptoms**:
- Slow response times
- High CPU/memory usage
- Timeouts

**Diagnosis**:

```bash
# Check resource usage
docker stats

# Check Redis connection (if enabled)
docker compose exec redis redis-cli INFO stats

# Check database connections
docker compose exec postgres psql -U oidc authdb -c "SELECT count(*) FROM pg_stat_activity;"
```

**Solutions**:

1. **Redis not connected**: Check `REDIS_ENABLED=true` and Redis healthy
2. **Database connection pool**: Restart services: `docker compose restart webauth`
3. **Insufficient resources**: Increase memory limits in docker-compose.yml

### Problem: Data Inconsistency After Upgrade

**Symptoms**:
- Missing data
- Unexpected behavior
- Foreign key violations

**Solution**:

**CRITICAL**: Rollback immediately and restore database backup.

```bash
# Full rollback with database restore
docker compose down
gunzip < backups/mrwhooidc-backup-YYYYMMDD-HHMMSS.sql.gz | \
  docker compose run --rm -T postgres psql -h postgres -U oidc authdb
# Restore previous image version and restart
```

## Upgrade Testing Checklist

Use this checklist to test upgrades in staging before production:

### Pre-Upgrade Testing

- [ ] Backup staging database
- [ ] Note current version
- [ ] Document current configuration

### Upgrade Execution

- [ ] Pull new image successfully
- [ ] Container starts without errors
- [ ] Migrations apply successfully (if any)
- [ ] All services reach "healthy" status

### Functional Testing

- [ ] Discovery endpoint returns valid JSON
- [ ] JWKS endpoint returns signing keys
- [ ] Admin UI loads and is functional
- [ ] Can create new client
- [ ] Can update existing client
- [ ] Can delete test client
- [ ] Can authenticate with existing client
- [ ] Token generation works
- [ ] Token introspection works
- [ ] Token revocation works (if supported)
- [ ] User login flow works
- [ ] Consent page displays correctly
- [ ] Logout works

### Performance Testing

- [ ] Response times acceptable (<200ms for discovery)
- [ ] Can handle expected concurrent load
- [ ] Memory usage stable (no leaks)
- [ ] CPU usage reasonable (<50% average)
- [ ] Database connection pool healthy

### Integration Testing

- [ ] Existing integrated applications still work
- [ ] Tokens issued pre-upgrade still valid
- [ ] Session continuity maintained (if possible)
- [ ] Refresh tokens still work

### Rollback Testing (Critical)

- [ ] Can rollback to previous version
- [ ] Database restore works
- [ ] Previous version starts successfully
- [ ] All functionality restored after rollback

## Backup Retention Policy

Establish a backup retention strategy based on your requirements:

### Recommended Policy

**Development**:
- Retain last 3 backups
- No automated retention needed

**Staging**:
- Retain last 7 days of backups
- One backup per day

**Production**:
- **Daily Backups**: Retain last 30 days
- **Weekly Backups**: Retain last 12 weeks (3 months)
- **Monthly Backups**: Retain last 12 months (1 year)
- **Pre-Upgrade Backups**: Retain until next successful upgrade (minimum 30 days)

### Automated Retention Script

Add to `backups/backup-db.sh`:

```bash
#!/bin/bash
set -e

BACKUP_DIR="./backups"
TIMESTAMP=$(date +%Y%m%d-%H%M%S)
BACKUP_FILE="$BACKUP_DIR/mrwhooidc-backup-$TIMESTAMP.sql.gz"

echo "Creating backup: $BACKUP_FILE"
docker compose exec -T postgres pg_dump -U oidc authdb | gzip > "$BACKUP_FILE"

if [ -f "$BACKUP_FILE" ]; then
    SIZE=$(du -h "$BACKUP_FILE" | cut -f1)
    echo "✓ Backup complete: $BACKUP_FILE ($SIZE)"
    
    # Retention: Delete backups older than 30 days
    find "$BACKUP_DIR" -name "mrwhooidc-backup-*.sql.gz" -mtime +30 -delete
    echo "✓ Old backups cleaned (>30 days)"
else
    echo "✗ Backup failed!"
    exit 1
fi
```

### Offsite Backup Storage

For production deployments, store backups offsite:

**AWS S3**:

```bash
# Upload to S3 after backup
aws s3 cp "$BACKUP_FILE" s3://your-bucket/mrwhooidc-backups/
```

**Azure Blob Storage**:

```bash
# Upload to Azure
az storage blob upload \
  --account-name youraccount \
  --container-name backups \
  --file "$BACKUP_FILE" \
  --name "mrwhooidc/$(basename $BACKUP_FILE)"
```

**Google Cloud Storage**:

```bash
# Upload to GCS
gsutil cp "$BACKUP_FILE" gs://your-bucket/mrwhooidc-backups/
```

## Best Practices

### Before Every Upgrade

1. ✅ **Always backup** - Never skip database backup
2. ✅ **Read release notes** - Understand what's changing
3. ✅ **Test in staging** - Catch issues before production
4. ✅ **Plan maintenance window** - Don't upgrade during peak hours
5. ✅ **Monitor after upgrade** - Watch logs for 30+ minutes

### Version Management

1. ✅ **Pin to specific versions** in production
2. ✅ **Use semantic versioning** - Understand major.minor.patch
3. ✅ **Track versions in git** - Document what's deployed
4. ✅ **Subscribe to releases** - Get notified of security updates

### Rollback Strategy

1. ✅ **Have rollback plan** before starting
2. ✅ **Test rollback in staging** - Ensure it works
3. ✅ **Document previous version** - Know what to rollback to
4. ✅ **Keep previous image** - Don't prune until upgrade validated

## Support

- **GitHub Issues**: [https://github.com/popicka70/MrWhoOidc/issues](https://github.com/popicka70/MrWhoOidc/issues)
- **Release Notes**: [https://github.com/popicka70/MrWhoOidc/releases](https://github.com/popicka70/MrWhoOidc/releases)
- **Documentation**: [https://github.com/popicka70/MrWhoOidc](https://github.com/popicka70/MrWhoOidc)

**Document Version**: 1.0
**Last Updated**: 2025-11-02
**Maintained By**: MrWhoOidc Project
