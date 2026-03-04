# Backup Verification Testing Procedures

This document outlines procedures for verifying backup integrity and testing restore operations for MrWhoOidc deployments.

## Overview

Regular backup verification is critical to ensure data can be recovered in case of disaster. This guide provides:
- Scheduled restore testing procedures
- RTO/RPO targets
- Backup integrity verification steps
- Disaster recovery runbook outline

## RTO/RPO Targets

### Recovery Time Objective (RTO)

| Scenario | Target | Maximum Acceptable |
|----------|--------|--------------------|
| Single instance failure | 5 minutes | 15 minutes |
| Database failure | 15 minutes | 30 minutes |
| Complete site failure | 1 hour | 4 hours |
| Regional disaster | 4 hours | 24 hours |

### Recovery Point Objective (RPO)

| Data Type | Target | Maximum Acceptable |
|-----------|--------|--------------------|
| User accounts | 0 (synchronous) | 5 minutes |
| Sessions | 5 minutes | 15 minutes |
| Audit logs | 15 minutes | 1 hour |
| Configuration | 1 hour | 24 hours |

## Backup Types

### 1. Database Backups

**Frequency:** Daily full, hourly incremental (WAL archiving)

**What's included:**
- All tenant data
- User accounts and credentials
- Client configurations
- Identity provider configurations
- Audit logs
- Session data (if stored in database)

**Retention:**
- Daily backups: 30 days
- Weekly backups: 12 weeks
- Monthly backups: 12 months
- Yearly backups: 7 years (compliance)

### 2. Configuration Backups

**Frequency:** On every change + daily snapshot

**What's included:**
- `appsettings.Production.json`
- Environment variables
- TLS certificates
- Signing keys
- Docker Compose files
- Kubernetes manifests

**Retention:** 90 days

### 3. Certificate and Key Backups

**Frequency:** On generation/rotation

**What's included:**
- TLS certificates (public and private)
- JWT signing keys
- Client secrets (encrypted)
- DPoP keys

**Retention:** Until superseded + 1 year (for audit)

**Storage:** Encrypted vault (e.g., HashiCorp Vault, AWS Secrets Manager)

## Scheduled Restore Testing

### Weekly Tests (Automated)

**Scope:** Database restore to isolated environment

**Procedure:**

1. **Setup test environment**
   ```bash
   # Create isolated test namespace/environment
   kubectl create namespace backup-test-$(date +%Y%m%d)
   
   # Deploy minimal test infrastructure
   helm install postgres-test postgresql \
     --namespace backup-test-$(date +%Y%m%d) \
     --set persistence.size=10Gi
   ```

2. **Restore latest backup**
   ```bash
   # Download latest backup from storage
   aws s3 cp s3://backups/mrwhooidc/db/daily/latest.sql.gz ./latest.sql.gz
   
   # Restore to test database
   gunzip -c latest.sql.gz | \
     psql -h postgres-test.backup-test.svc -U postgres -d authdb
   ```

3. **Verify restore integrity**
   ```sql
   -- Check row counts
   SELECT 'Tenants' as table_name, count(*) FROM "Tenants";
   SELECT 'Users' as table_name, count(*) FROM "Users";
   SELECT 'Clients' as table_name, count(*) FROM "Clients";
   SELECT 'IdentityProviders' as table_name, count(*) FROM "IdentityProviders";
   
   -- Check data freshness
   SELECT MAX("Timestamp") as latest_audit FROM "AuditLog";
   SELECT MAX("ModifiedAt") as latest_user FROM "Users";
   ```

4. **Run validation queries**
   ```sql
   -- Verify referential integrity
   SELECT COUNT(*) as orphaned_clients
   FROM "Clients" c
   LEFT JOIN "Tenants" t ON c."TenantId" = t."Id"
   WHERE t."Id" IS NULL;
   
   -- Verify critical data exists
   SELECT COUNT(*) as platform_admins
   FROM "Users" u
   JOIN "UserRoles" ur ON u."Id" = ur."UserId"
   JOIN "Roles" r ON ur."RoleId" = r."Id"
   WHERE r."Name" = 'PlatformAdmin';
   ```

5. **Cleanup**
   ```bash
   kubectl delete namespace backup-test-$(date +%Y%m%d)
   ```

**Success Criteria:**
- All tables restored with expected row counts
- No referential integrity violations
- Critical admin accounts present
- Data within RPO target

### Monthly Tests (Manual)

**Scope:** Full environment restore

**Procedure:**

1. **Provision test environment**
   ```bash
   # Deploy complete MrWhoOidc stack in isolated environment
   cd test-environments/monthly-restore-test
   terraform init
   terraform apply -var="environment=restore-test"
   ```

2. **Restore all components**
   ```bash
   # Restore database
   ./restore-database.sh --from daily --date $(date +%Y-%m-%d -d "7 days ago")
   
   # Restore configuration
   ./restore-configuration.sh --from vault
   
   # Restore certificates
   ./restore-certificates.sh --from vault
   ```

3. **Deploy application**
   ```bash
   # Deploy MrWhoOidc
   helm install mrwhooidc ./charts/mrwhooidc \
     --namespace restore-test \
     --values values-restore-test.yaml
   ```

4. **Functional testing**
   ```bash
   # Run smoke tests
   ./run-smoke-tests.sh --environment restore-test
   
   # Verify authentication flow
   ./test-auth-flow.sh --environment restore-test
   
   # Verify admin functions
   ./test-admin-functions.sh --environment restore-test
   ```

5. **Document results**
   - Time to complete restore
   - Issues encountered
   - Data integrity verification
   - Functional test results

**Success Criteria:**
- Full environment restored within RTO
- All smoke tests pass
- Authentication flows work correctly
- Admin functions operational

### Quarterly Tests (Disaster Recovery)

**Scope:** Complete DR failover test

**Procedure:**

1. **Activate DR site**
   ```bash
   # Failover to DR region
   ./dr-failover.sh --region us-west-2
   ```

2. **Restore from cross-region backups**
   ```bash
   # Restore from DR backup storage
   aws s3 cp s3://dr-backups/mrwhooidc/ ./restore/ --recursive
   ./restore-all.sh --from-dr-backup
   ```

3. **Update DNS/routing**
   ```bash
   # Update DNS to point to DR site
   aws route53 change-resource-record-sets \
     --hosted-zone-id Z123456 \
     --change-batch file://dns-failover.json
   ```

4. **Full functional testing**
   - All authentication flows
   - All admin functions
   - API integrations
   - Monitoring and alerting

5. **Failback to primary**
   ```bash
   # Sync data back to primary
   ./dr-sync-back.sh --to primary
   
   # Restore primary DNS
   aws route53 change-resource-record-sets \
     --hosted-zone-id Z123456 \
     --change-batch file://dns-failback.json
   ```

**Success Criteria:**
- DR site fully operational within 4 hours
- All functionality verified
- Data consistency confirmed
- Failback completed successfully

## Backup Integrity Verification

### Automated Checks (Daily)

**1. Checksum Verification**
```bash
# Verify backup checksum
sha256sum -c backup-2025-01-15.sql.gz.sha256

# Alert if checksum fails
if [ $? -ne 0 ]; then
  echo "BACKUP CHECKSUM FAILED" | mail -s "Alert: Backup Integrity" ops@example.com
fi
```

**2. File Size Validation**
```bash
# Check backup size is within expected range
MIN_SIZE=1000000  # 1MB
MAX_SIZE=10000000000  # 10GB

SIZE=$(stat -c%s backup-2025-01-15.sql.gz)

if [ $SIZE -lt $MIN_SIZE ] || [ $SIZE -gt $MAX_SIZE ]; then
  echo "BACKUP SIZE ANOMALY: $SIZE bytes" | mail -s "Alert: Backup Size" ops@example.com
fi
```

**3. Backup Completeness**
```bash
# Verify all expected tables are in backup
grep -q "CREATE TABLE \"Tenants\"" backup.sql || echo "MISSING: Tenants table"
grep -q "CREATE TABLE \"Users\"" backup.sql || echo "MISSING: Users table"
grep -q "CREATE TABLE \"Clients\"" backup.sql || echo "MISSING: Clients table"
grep -q "CREATE TABLE \"AuditLog\"" backup.sql || echo "MISSING: AuditLog table"
```

### Manual Verification (Weekly)

**1. Sample Data Verification**
```sql
-- Connect to restored backup and verify sample records
SELECT "Id", "Name", "CreatedAt" 
FROM "Tenants" 
ORDER BY "CreatedAt" DESC 
LIMIT 5;

-- Verify user count matches expectations
SELECT COUNT(*) as user_count FROM "Users";

-- Verify recent audit entries exist
SELECT COUNT(*) as recent_audits 
FROM "AuditLog" 
WHERE "Timestamp" > NOW() - INTERVAL '24 hours';
```

**2. Configuration Verification**
```bash
# Verify configuration file integrity
jq '.Auth.IssuerUri' appsettings.Production.json
jq '.ConnectionStrings.Postgres' appsettings.Production.json

# Verify certificate validity
openssl x509 -in certs/tls.crt -text -noout | grep -E "Not Before|Not After"
```

## Disaster Recovery Runbook Outline

### DR Activation Checklist

**Phase 1: Assessment (0-15 minutes)**

- [ ] Confirm primary site failure
- [ ] Assess scope of outage
- [ ] Notify incident commander
- [ ] Open incident communication channel
- [ ] Begin incident timeline documentation

**Phase 2: Decision (15-30 minutes)**

- [ ] Evaluate recovery options
- [ ] Confirm DR site availability
- [ ] Get authorization for failover
- [ ] Notify stakeholders of potential failover

**Phase 3: Activation (30-60 minutes)**

- [ ] Activate DR infrastructure
- [ ] Restore database from latest backup
- [ ] Restore configuration from vault
- [ ] Deploy application to DR site
- [ ] Verify application health

**Phase 4: Traffic Redirect (60-90 minutes)**

- [ ] Update DNS records
- [ ] Update load balancer configuration
- [ ] Verify traffic routing to DR
- [ ] Monitor for errors

**Phase 5: Validation (90-120 minutes)**

- [ ] Run smoke tests
- [ ] Verify authentication flows
- [ ] Test admin functions
- [ ] Confirm monitoring operational
- [ ] Validate backup processes at DR site

**Phase 6: Communication**

- [ ] Notify users of maintenance (if applicable)
- [ ] Update status page
- [ ] Notify stakeholders of recovery
- [ ] Document recovery time

### DR Failback Checklist

**Phase 1: Primary Site Recovery**

- [ ] Confirm primary site restored
- [ ] Verify primary infrastructure health
- [ ] Restore primary database
- [ ] Sync data from DR to primary

**Phase 2: Data Synchronization**

- [ ] Export data changes from DR
- [ ] Import changes to primary
- [ ] Verify data consistency
- [ ] Resolve any conflicts

**Phase 3: Traffic Migration**

- [ ] Gradually shift traffic to primary
- [ ] Monitor for errors
- [ ] Complete traffic migration
- [ ] Update DNS records

**Phase 4: DR Site Reset**

- [ ] Clear DR site data
- [ ] Restore DR site from primary backup
- [ ] Verify DR site health
- [ ] Confirm DR readiness

**Phase 5: Documentation**

- [ ] Document failback procedure
- [ ] Record any issues
- [ ] Update runbooks
- [ ] Schedule post-incident review

## Backup Schedule Summary

| Backup Type | Frequency | Retention | Storage Location | Verification |
|-------------|-----------|-----------|------------------|--------------|
| Database (Full) | Daily | 30 days | S3 + Cross-region | Weekly restore test |
| Database (WAL) | Continuous | 7 days | S3 + Cross-region | Daily integrity check |
| Configuration | On-change + Daily | 90 days | Git + Vault | Weekly validation |
| Certificates | On-rotation | 1 year after expiry | Vault | Monthly verification |
| Full Environment | Weekly | 4 weeks | DR Site | Monthly DR test |

## Monitoring Backup Health

### Backup Success Metrics

```yaml
# Prometheus recording rules
groups:
  - name: backup-metrics
    rules:
      - record: backup_success_rate
        expr: |
          sum(rate(backup_completed_total[24h])) 
          / sum(rate(backup_scheduled_total[24h]))
      
      - record: backup_age_seconds
        expr: |
          time() - max(backup_timestamp_seconds)
      
      - record: backup_size_bytes
        expr: max(backup_size_bytes)
```

### Backup Alerts

```yaml
# Alerting rules
groups:
  - name: backup-alerts
    rules:
      - alert: BackupFailed
        expr: backup_success_rate < 0.95
        for: 1h
        labels:
          severity: critical
        annotations:
          summary: "Backup success rate below 95%"
      
      - alert: BackupTooOld
        expr: backup_age_seconds > 86400
        for: 1h
        labels:
          severity: critical
        annotations:
          summary: "Latest backup is more than 24 hours old"
      
      - alert: BackupSizeAnomaly
        expr: |
          abs(backup_size_bytes - avg_over_time(backup_size_bytes[7d])) 
          / avg_over_time(backup_size_bytes[7d]) > 0.5
        for: 1h
        labels:
          severity: warning
        annotations:
          summary: "Backup size anomaly detected"
```

## Compliance and Audit

### Backup Audit Log

Maintain audit log of all backup operations:

```sql
CREATE TABLE "BackupAuditLog" (
    "Id" UUID PRIMARY KEY,
    "Timestamp" TIMESTAMPTZ NOT NULL,
    "BackupType" VARCHAR(50) NOT NULL,
    "Status" VARCHAR(20) NOT NULL,
    "SizeBytes" BIGINT,
    "DurationSeconds" INTEGER,
    "StorageLocation" VARCHAR(500),
    "ChecksumSha256" VARCHAR(64),
    "PerformedBy" VARCHAR(100),
    "Notes" TEXT
);
```

### Compliance Requirements

| Requirement | Implementation |
|-------------|----------------|
| Data retention | Automated retention policies |
| Encryption at rest | AES-256 encryption |
| Encryption in transit | TLS 1.3 |
| Access logging | All backup access logged |
| Regular testing | Weekly/Monthly/Quarterly tests |
| Documentation | This document + runbooks |

---

**Document Version:** 1.0  
**Last Updated:** 2025-01-XX  
**Review Schedule:** Quarterly  
**Owner:** Operations Team  
**Next Test Date:** [Schedule next quarterly DR test]
