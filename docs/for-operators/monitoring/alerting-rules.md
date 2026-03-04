# Alerting Rules Documentation

This document describes all monitoring alerts configured for MrWhoOidc, including thresholds, escalation procedures, and runbook references.

## Alert Categories

### 1. Application Alerts

#### High Error Rate
**Alert:** `MrWhoOidcHighErrorRate`

| Attribute | Value |
|-----------|-------|
| **Severity** | Critical |
| **Threshold** | > 5% error rate |
| **Duration** | 5 minutes |
| **Metric** | `http_requests_total{status=~"5.."}` |

**Description:** Indicates a significant increase in HTTP 5xx errors, suggesting application-level issues.

**Possible Causes:**
- Application bugs or exceptions
- Database connection failures
- External service dependencies failing
- Resource exhaustion (memory, CPU)

**Response:**
1. Check application logs for exceptions
2. Verify database connectivity
3. Review recent deployments
4. Check dependent service health

**Escalation:** Page on-call engineer immediately

**Runbook:** [Application Error Troubleshooting](#application-error-troubleshooting)

---

#### Elevated Error Rate (Warning)
**Alert:** `MrWhoOidcElevatedErrorRate`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | > 2% error rate |
| **Duration** | 10 minutes |
| **Metric** | `http_requests_total{status=~"5.."}` |

**Description:** Early warning for increasing error rates before they reach critical levels.

**Response:**
1. Monitor trend
2. Check for correlated events
3. Prepare for potential escalation

**Escalation:** Notify on-call engineer via chat

---

#### Slow Response Times
**Alert:** `MrWhoOidcSlowResponseP95`, `MrWhoOidcSlowResponseP99`

| Attribute | P95 Warning | P99 Critical |
|-----------|-------------|---------------|
| **Severity** | Warning | Critical |
| **Threshold** | > 2 seconds | > 5 seconds |
| **Duration** | 5 minutes | 5 minutes |
| **Metric** | `http_request_duration_seconds_bucket` |

**Description:** Response times exceeding acceptable thresholds, affecting user experience.

**Possible Causes:**
- Database query performance issues
- Network latency
- Resource contention
- Cache misses

**Response:**
1. Check database query performance
2. Review cache hit rates
3. Analyze slow request traces
4. Check resource utilization

**Escalation:** 
- P95: Notify team via chat
- P99: Page on-call engineer

**Runbook:** [Performance Troubleshooting](#performance-troubleshooting)

---

#### High Authentication Failure Rate
**Alert:** `MrWhoOidcHighAuthFailureRate`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | > 30% failure rate |
| **Duration** | 5 minutes |
| **Metric** | `authentication_attempts_total{success="false"}` |

**Description:** Unusual number of failed authentication attempts, possibly indicating brute force attack.

**Possible Causes:**
- Brute force attack
- Credential stuffing
- Misconfigured client applications
- Expired user credentials

**Response:**
1. Check source IPs for patterns
2. Review failed authentication reasons
3. Consider temporary IP blocking
4. Verify legitimate client configurations

**Escalation:** Notify security team

**Runbook:** [Security Incident Response](../for-security-teams/incident-response.md)

---

#### Token Exchange Failures
**Alert:** `MrWhoOidcTokenExchangeFailures`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | > 0.5 failures/second |
| **Duration** | 5 minutes |
| **Metric** | `token_exchange_total{success="false"}` |

**Description:** Failures in token exchange operations, affecting OBO (On-Behalf-Of) flows.

**Possible Causes:**
- Invalid client credentials
- Expired subject tokens
- Policy violations
- DPoP proof validation failures

**Response:**
1. Check token exchange logs
2. Verify client configurations
3. Review policy settings
4. Check DPoP key validity

**Escalation:** Notify API team

---

### 2. Database Alerts

#### Database Connection Pool Exhausted
**Alert:** `MrWhoOidcDbConnectionPoolExhausted`

| Attribute | Value |
|-----------|-------|
| **Severity** | Critical |
| **Threshold** | > 90% pool usage |
| **Duration** | 2 minutes |
| **Metric** | `db_connection_pool_active / db_connection_pool_max` |

**Description:** Database connection pool nearly exhausted, risking service availability.

**Possible Causes:**
- Connection leaks
- Long-running queries
- Insufficient pool size
- Database performance issues

**Response:**
1. Check for connection leaks in application
2. Review long-running queries
3. Consider increasing pool size temporarily
4. Check database performance

**Escalation:** Page on-call engineer and DBA

**Runbook:** [Database Connection Issues](#database-connection-issues)

---

#### Database Connection Errors
**Alert:** `MrWhoOidcDbConnectionErrors`

| Attribute | Value |
|-----------|-------|
| **Severity** | Critical |
| **Threshold** | > 0.1 errors/second |
| **Duration** | 2 minutes |
| **Metric** | `db_connection_errors_total` |

**Description:** Active database connection failures.

**Possible Causes:**
- Database server down
- Network connectivity issues
- Authentication failures
- Maximum connections exceeded

**Response:**
1. Verify database server status
2. Check network connectivity
3. Review database logs
4. Check connection limits

**Escalation:** Page on-call engineer and DBA immediately

---

#### Slow Database Queries
**Alert:** `MrWhoOidcSlowDatabaseQueries`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | P95 > 1 second |
| **Duration** | 5 minutes |
| **Metric** | `db_query_duration_seconds_bucket` |

**Description:** Database queries taking longer than expected.

**Response:**
1. Identify slow queries from logs
2. Check for missing indexes
3. Review query execution plans
4. Check database resource utilization

**Escalation:** Notify DBA

---

#### Database Replication Lag
**Alert:** `MrWhoOidcDbReplicationLag`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | > 30 seconds |
| **Duration** | 5 minutes |
| **Metric** | `db_replication_lag_seconds` |

**Description:** Database replication falling behind, risking data consistency.

**Response:**
1. Check replica server health
2. Review replication logs
3. Check network between primary and replica
4. Consider read traffic reduction

**Escalation:** Notify DBA

---

#### Database Storage Low
**Alert:** `MrWhoOidcDbStorageLow`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | > 85% usage |
| **Duration** | 10 minutes |
| **Metric** | `db_storage_used_bytes / db_storage_total_bytes` |

**Description:** Database storage approaching capacity.

**Response:**
1. Check storage growth trends
2. Identify large tables
3. Plan storage expansion
4. Consider archival/deletion of old data

**Escalation:** Notify DBA and infrastructure team

---

### 3. Certificate Alerts

#### Certificate Expiration
**Alert:** `MrWhoOidcCertExpiringCritical`, `MrWhoOidcCertExpiringWarning`

| Attribute | Warning | Critical |
|-----------|---------|----------|
| **Severity** | Warning | Critical |
| **Threshold** | < 30 days | < 7 days |
| **Duration** | 1 hour | 1 hour |
| **Metric** | `probe_ssl_earliest_cert_expiry - time()` |

**Description:** SSL/TLS certificates approaching expiration.

**Response:**
1. Identify expiring certificate
2. Generate/renew certificate
3. Deploy new certificate
4. Verify deployment

**Escalation:**
- Warning: Notify team via chat
- Critical: Page on-call engineer

**Runbook:** [Certificate Renewal](#certificate-renewal)

---

#### Signing Key Expiration
**Alert:** `MrWhoOidcSigningKeyExpiring`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | < 14 days |
| **Duration** | 1 hour |
| **Metric** | `signing_key_expiry_seconds - time()` |

**Description:** JWT signing key approaching expiration.

**Response:**
1. Generate new signing key
2. Add to JWKS with new `kid`
3. Coordinate key rotation
4. Update all relying parties

**Escalation:** Notify security team

---

### 4. Infrastructure Alerts

#### Disk Space Warnings
**Alert:** `MrWhoOidcDiskSpaceWarning`, `MrWhoOidcDiskSpaceCritical`

| Attribute | Warning | Critical |
|-----------|---------|----------|
| **Severity** | Warning | Critical |
| **Threshold** | < 20% free | < 10% free |
| **Duration** | 10 minutes | 5 minutes |
| **Metric** | `node_filesystem_avail_bytes / node_filesystem_size_bytes` |

**Description:** Disk space running low on server.

**Response:**
1. Identify large files/directories
2. Clean up logs and temp files
3. Expand storage if needed
4. Check for runaway processes

**Escalation:**
- Warning: Notify infrastructure team
- Critical: Page on-call engineer

**Runbook:** [Disk Space Cleanup](#disk-space-cleanup)

---

#### Memory Usage High
**Alert:** `MrWhoOidcMemoryHigh`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | > 85% of limit |
| **Duration** | 5 minutes |
| **Metric** | `container_memory_usage_bytes / container_spec_memory_limit_bytes` |

**Description:** Container memory approaching limit.

**Response:**
1. Check for memory leaks
2. Review GC metrics
3. Consider increasing memory limit
4. Analyze heap dumps if available

**Escalation:** Notify on-call engineer

---

#### CPU Usage High
**Alert:** `MrWhoOidcCPUHigh`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | > 80% of quota |
| **Duration** | 10 minutes |
| **Metric** | `container_cpu_usage_seconds_total / (cpu_quota/cpu_period)` |

**Description:** Container CPU usage approaching allocated quota.

**Response:**
1. Identify CPU-intensive operations
2. Check for runaway processes
3. Consider scaling horizontally
4. Review recent code changes

**Escalation:** Notify on-call engineer

---

#### Container Restart Loop
**Alert:** `MrWhoOidcContainerRestarts`

| Attribute | Value |
|-----------|-------|
| **Severity** | Critical |
| **Threshold** | > 5 restarts in 5 minutes |
| **Duration** | 5 minutes |
| **Metric** | `increase(container_last_seen[5m])` |

**Description:** Container repeatedly restarting, indicating crash loop.

**Response:**
1. Check container logs for errors
2. Review recent deployments
3. Check resource limits
4. Verify configuration

**Escalation:** Page on-call engineer immediately

---

### 5. Availability Alerts

#### Service Down
**Alert:** `MrWhoOidcServiceDown`

| Attribute | Value |
|-----------|-------|
| **Severity** | Critical |
| **Threshold** | `up == 0` |
| **Duration** | 1 minute |
| **Metric** | `up{job="mrwhooidc"}` |

**Description:** MrWhoOidc service instance is not responding to Prometheus scraping.

**Response:**
1. Check container/pod status
2. Review application logs
3. Check host health
4. Attempt restart if appropriate

**Escalation:** Page on-call engineer immediately

**Runbook:** [Service Recovery](#service-recovery)

---

#### Health Check Failed
**Alert:** `MrWhoOidcHealthCheckFailed`

| Attribute | Value |
|-----------|-------|
| **Severity** | Critical |
| **Threshold** | Probe fails |
| **Duration** | 2 minutes |
| **Metric** | `probe_success{job="mrwhooidc-health"}` |

**Description:** Application health endpoint returning unhealthy status.

**Response:**
1. Check health endpoint response
2. Review dependent service health
3. Check database connectivity
4. Review application logs

**Escalation:** Page on-call engineer

---

#### Not Ready
**Alert:** `MrWhoOidcNotReady`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | Probe fails |
| **Duration** | 5 minutes |
| **Metric** | `probe_success{job="mrwhooidc-ready"}` |

**Description:** Application readiness check failing, traffic may be routed away.

**Response:**
1. Check readiness endpoint response
2. Review startup logs
3. Check database migrations status
4. Verify configuration

**Escalation:** Notify on-call engineer

---

#### High Latency (External)
**Alert:** `MrWhoOidcHighLatency`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | > 3 seconds |
| **Duration** | 5 minutes |
| **Metric** | `probe_http_duration_seconds` |

**Description:** External blackbox probe detecting high latency.

**Response:**
1. Check network path
2. Review application performance
3. Check for geographic issues
4. Verify load balancer health

**Escalation:** Notify on-call engineer

---

### 6. Redis Alerts

#### Redis Connection Failed
**Alert:** `MrWhoOidcRedisConnectionFailed`

| Attribute | Value |
|-----------|-------|
| **Severity** | Critical |
| **Threshold** | `redis_up == 0` |
| **Duration** | 1 minute |
| **Metric** | `redis_up` |

**Description:** Cannot connect to Redis cache instance.

**Response:**
1. Check Redis server status
2. Verify network connectivity
3. Check authentication
4. Review Redis logs

**Escalation:** Page on-call engineer

**Note:** MrWhoOidc has graceful degradation - service continues without Redis but with reduced performance.

---

#### Redis Memory High
**Alert:** `MrWhoOidcRedisMemoryHigh`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | > 85% of max |
| **Duration** | 5 minutes |
| **Metric** | `redis_memory_used_bytes / redis_memory_max_bytes` |

**Description:** Redis memory usage approaching limit.

**Response:**
1. Review key expiration policies
2. Check for memory-intensive operations
3. Consider increasing memory limit
4. Analyze memory usage patterns

**Escalation:** Notify infrastructure team

---

#### Redis Key Evictions
**Alert:** `MrWhoOidcRedisEvictions`

| Attribute | Value |
|-----------|-------|
| **Severity** | Warning |
| **Threshold** | > 100 evictions in 5 minutes |
| **Duration** | 5 minutes |
| **Metric** | `redis_evicted_keys_total` |

**Description:** Redis evicting keys due to memory pressure.

**Response:**
1. Check memory usage
2. Review eviction policy
3. Consider increasing memory
4. Analyze key patterns

**Escalation:** Notify infrastructure team

---

## Escalation Procedures

### Severity Definitions

| Severity | Response Time | Notification Method |
|----------|---------------|---------------------|
| **Critical** | Immediate (< 15 min) | Page (phone/SMS) |
| **Warning** | Within 1 hour | Chat/Email |
| **Info** | Next business day | Dashboard/Ticket |

### Escalation Matrix

| Time Elapsed | Critical | Warning |
|--------------|----------|---------|
| 0 minutes | On-call engineer | Team chat |
| 15 minutes | Team lead | - |
| 30 minutes | Engineering manager | - |
| 1 hour | VP Engineering | - |
| 2 hours | Executive team | - |

### On-Call Schedule

- **Primary:** Rotating weekly among senior engineers
- **Secondary:** Team lead (backup)
- **Escalation:** Engineering manager

## Runbook Links

### Application Runbooks

- [Application Error Troubleshooting](#application-error-troubleshooting)
- [Performance Troubleshooting](#performance-troubleshooting)
- [Service Recovery](#service-recovery)

### Database Runbooks

- [Database Connection Issues](#database-connection-issues)
- [Slow Query Analysis](#slow-query-analysis)
- [Database Failover](#database-failover)

### Infrastructure Runbooks

- [Certificate Renewal](#certificate-renewal)
- [Disk Space Cleanup](#disk-space-cleanup)
- [Container Troubleshooting](#container-troubleshooting)

### Security Runbooks

- [Security Incident Response](../for-security-teams/incident-response.md)
- [Brute Force Mitigation](#brute-force-mitigation)

---

## Appendix: Quick Reference Commands

### Check Application Status
```bash
curl -k https://localhost:8443/health
curl -k https://localhost:8443/ready
curl -k https://localhost:8443/metrics
```

### View Recent Logs
```bash
docker compose logs --tail=100 webauth
docker compose logs --follow webauth
```

### Check Database Connections
```sql
SELECT count(*) FROM pg_stat_activity;
SELECT * FROM pg_stat_activity WHERE state = 'active';
```

### Check Disk Usage
```bash
df -h
du -sh /var/lib/docker/*
```

### Restart Service
```bash
docker compose restart webauth
docker compose up -d webauth
```

---

**Document Version:** 1.0  
**Last Updated:** 2025-01-XX  
**Review Schedule:** Quarterly  
**Owner:** Operations Team
