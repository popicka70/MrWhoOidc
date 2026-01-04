# Client Secret Rotation Playbook

**Audience**: MrWhoOidc operators, SREs, and administrators  
**Version**: 1.0  
**Date**: October 17, 2025  
**Status**: Production-ready

---

## Purpose

This playbook provides operational procedures for managing client secret rotation, expiry enforcement, and incident response in MrWhoOidc. It complements the user-facing [Client Secret Rotation Guide](client-secret-rotation-guide.md) and follows patterns established in [Key Rotation Playbook](key-rotation-playbook.md).

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Routine Operations](#routine-operations)
3. [Monitoring & Alerts](#monitoring--alerts)
4. [Incident Response](#incident-response)
5. [Maintenance Procedures](#maintenance-procedures)
6. [Rollback Scenarios](#rollback-scenarios)
7. [Security Audit Procedures](#security-audit-procedures)

---

## Architecture Overview

### Multi-Secret Model

MrWhoOidc supports **overlapping client secrets** to enable zero-downtime rotation:

- Each confidential client can have up to **3 active secrets** simultaneously
- Secrets have lifecycle states: **Inactive → Active → Expired/Revoked**
- One secret can be marked as **Primary** (advisory only, doesn't restrict usage)
- Secrets have optional **expiry dates** (default: 90 days from activation)

### Data Model

**ClientSecret Entity:**

```csharp
- Id (Guid) — Unique secret identifier
- ClientId (Guid) — FK to Client
- SecretHash (string) — Argon2id/BCrypt hash
- Description (string) — User-friendly label
- CreatedAtUtc, ActivatedAtUtc, ExpiresAtUtc, RevokedAtUtc (DateTime?)
- IsPrimary (bool) — Recommended secret flag
- CreatedBy, ActivatedBy, RevokedBy (string) — Audit trail
- LastUsedAtUtc, UsageCount (tracking fields)
```

### Validation Flow

Client authentication at `/token` endpoint:

1. Load client record with all `ClientSecrets`
2. Filter active secrets: `ActivatedAtUtc != null AND RevokedAtUtc == null AND (ExpiresAtUtc == null OR ExpiresAtUtc > UtcNow)`
3. Hash-compare provided secret against all active secret hashes
4. If match: authenticate, record usage metric, return success
5. If no match but matches expired secret: log warning, return 401
6. If no match at all: return 401

**Performance**: Constant-time comparison via `IPasswordHasher.Verify`; typical latency <10ms for 3 secrets.

---

## Routine Operations

### Monthly Secret Audit

**Frequency**: First Monday of each month  
**Duration**: ~30 minutes

**Procedure:**

1. **Review expiring secrets** (expires within 30 days):

   ```sql
   SELECT c.ClientId, cs.Id, cs.Description, cs.ExpiresAtUtc, cs.LastUsedAtUtc
   FROM ClientSecrets cs
   JOIN Clients c ON cs.ClientId = c.Id
   WHERE cs.ActivatedAtUtc IS NOT NULL
     AND cs.RevokedAtUtc IS NULL
     AND cs.ExpiresAtUtc IS NOT NULL
     AND cs.ExpiresAtUtc BETWEEN NOW() AND NOW() + INTERVAL '30 days'
   ORDER BY cs.ExpiresAtUtc ASC;
   ```

   Or use Admin API:

   ```bash
   curl -H "Authorization: Bearer $ADMIN_TOKEN" \
     "https://auth.example.com/health/client-secrets"
   ```

2. **Notify client owners** via email/Slack with link to [Rotation Guide](client-secret-rotation-guide.md)

3. **Document notifications** in audit log (date, recipients, secrets)

4. **Follow up** 2 weeks before expiry if no action taken

### Weekly Health Check

**Frequency**: Every Monday at 9 AM  
**Duration**: ~10 minutes

**Procedure:**

1. **Check health endpoint**:

   ```bash
   curl https://auth.example.com/health/client-secrets
   ```

   Expected response: `{ "status": "Healthy" }` or `{ "status": "Degraded", "warnings": [...] }`

2. **Review metrics dashboard** (Grafana/Application Insights):
   - `oidc.client_secrets.days_until_expiry` — Should be >7 for all clients
   - `oidc.client_secrets.authentication_failure{reason="expired"}` — Should be 0
   - `oidc.client_secrets.active_count` — No clients with >3 active secrets

3. **Review expiry monitor logs**:

   ```bash
   grep "Client secret expiring soon" /var/log/mrwhooidc/app.log
   ```

4. **Escalate** any `status: "Unhealthy"` or expired secret authentication failures

### Client Onboarding

When creating a new confidential client:

1. **Generate initial secret** via Admin UI or API (do NOT activate immediately)
2. **Provide secret to client owner** via secure channel (e.g., password manager, 1Password share)
3. **Set expiry** to 90 days (default) or per client's security policy
4. **Document** client owner contact info in Client description or external CMDB
5. **Activate secret** only after client confirms application is configured
6. **Set as primary** after successful first authentication

---

## Monitoring & Alerts

### Critical Alerts

Configure these alerts in your observability platform (Prometheus, Azure Monitor, Datadog, etc.):

#### Alert: Secret Expired (CRITICAL)

**Condition:**

```promql
oidc_client_secrets_authentication_failure{reason="expired"} > 0
```

**Severity**: P1 (Critical)  
**Response Time**: Immediate  
**Action**:

1. Identify affected client via metrics tags or logs
2. Contact client owner immediately (phone/SMS if after hours)
3. Generate new secret via Admin API
4. If client owner unavailable, activate immediately and provide secret via secure emergency channel
5. Document incident in post-mortem

#### Alert: Secrets Expiring Soon (WARNING)

**Condition:**

```promql
oidc_client_secrets_days_until_expiry < 7
```

**Severity**: P3 (Warning)  
**Response Time**: Within 24 hours  
**Action**:

1. Query for affected clients:

   ```bash
   curl "https://auth.example.com/health/client-secrets"
   ```

2. Send reminder notification to client owners
3. Update ticket/issue tracker
4. Escalate to P2 if <3 days remain

#### Alert: Too Many Active Secrets (INFO)

**Condition:**

```promql
oidc_client_secrets_active_count > 3
```

**Severity**: P4 (Info)  
**Response Time**: Within 1 week  
**Action**:

1. Contact client owner to complete rotation (revoke old secrets)
2. Review admin audit logs for incomplete rotation workflows
3. Clean up orphaned secrets if confirmed unused (verify `LastUsedAtUtc`)

### Metrics Dashboard

**Recommended panels** (Grafana/Azure Dashboard):

1. **Active Secrets per Client** (gauge):

   ```promql
   oidc_client_secrets_active_count
   ```

2. **Days Until Nearest Expiry** (gauge, color-coded):

   ```promql
   oidc_client_secrets_days_until_expiry
   ```

   - Green: >30 days
   - Yellow: 7-30 days
   - Red: <7 days

3. **Authentication Failures by Reason** (counter, stacked area):

   ```promql
   rate(oidc_client_secrets_authentication_failure[5m])
   ```

   Split by `reason`: expired, revoked, invalid, missing

4. **Primary vs Secondary Secret Usage** (pie chart):

   ```promql
   sum by (is_primary) (rate(oidc_client_secrets_authentication_success[1h]))
   ```

   Alert if secondary usage >50% (indicates incomplete rotation)

5. **Secret Rotation Events** (counter, timeline):

   ```promql
   oidc_client_secrets_rotation_events
   ```

   Split by `action`: created, activated, revoked, set_primary

---

## Incident Response

### Scenario 1: Client Secret Compromised

**Severity**: P1 (Critical if production client)

**Detection**:

- Security team reports leaked credentials in public repo
- Anomalous authentication patterns (unusual IPs/geolocations)
- Client owner reports suspected compromise

**Response Procedure:**

1. **Immediate containment** (within 15 minutes):
   - Revoke compromised secret via Admin UI or API:

     ```bash
     curl -X DELETE \
       "https://auth.example.com/api/admin/clients/{clientId}/secrets/{secretId}" \
       -H "Authorization: Bearer $ADMIN_TOKEN"
     ```

   - If client has no other active secrets, generate and activate emergency secret first

2. **Generate replacement secret** (within 30 minutes):
   - Create new secret with description "Emergency rotation YYYY-MM-DD"
   - Activate immediately
   - Provide to client owner via secure channel (phone + encrypted email)

3. **Audit and investigation** (within 2 hours):
   - Query authentication logs for usage of compromised secret:

     ```sql
     SELECT * FROM AuditLogs 
     WHERE SecretId = '{compromised-secret-id}'
       AND Timestamp >= '{suspected-compromise-date}'
     ORDER BY Timestamp DESC;
     ```

   - Review metrics for anomalous usage patterns
   - Check if any tokens were issued using compromised secret
   - Revoke active tokens if necessary (via revocation endpoint)

4. **Post-incident** (within 24 hours):
   - Document timeline in incident report
   - Update security procedures if process gaps identified
   - Consider forced rotation for all client secrets if systemic issue

### Scenario 2: All Secrets Expired (Lockout)

**Severity**: P1 (Critical — client authentication down)

**Detection**:

- Health check endpoint returns `Unhealthy` for client
- Client owner reports "invalid_client" errors
- Authentication failure metrics spike for specific client

**Response Procedure:**

1. **Verify lockout** (within 5 minutes):

   ```bash
   curl "https://auth.example.com/api/admin/clients/{clientId}/secrets" \
     -H "Authorization: Bearer $ADMIN_TOKEN"
   ```

   Confirm all secrets have `expiresAtUtc` in the past

2. **Generate emergency secret** (within 10 minutes):
   - Create new secret via Admin API
   - Activate immediately
   - Set expiry to 30 days (shorter than default for expedited follow-up)

3. **Notify client owner** (within 15 minutes):
   - Provide new secret via secure channel
   - Request immediate application update
   - Offer live support during deployment

4. **Root cause analysis** (within 24 hours):
   - Review expiry warning logs — were notifications sent?
   - Check email delivery logs
   - Update contact info if notifications failed
   - Consider mandatory rotation reminders for this client

### Scenario 3: Mass Expiry Event

**Severity**: P0 (Multiple clients affected)

**Detection**:

- Multiple clients reporting authentication failures simultaneously
- Health check shows many clients with expired secrets
- Spike in `authentication_failure{reason="expired"}` metric

**Response Procedure:**

1. **Assess scope** (within 10 minutes):

   ```sql
   SELECT COUNT(DISTINCT ClientId) AS affected_clients
   FROM ClientSecrets
   WHERE RevokedAtUtc IS NULL
     AND ExpiresAtUtc <= NOW()
     AND NOT EXISTS (
       SELECT 1 FROM ClientSecrets cs2
       WHERE cs2.ClientId = ClientSecrets.ClientId
         AND cs2.ActivatedAtUtc IS NOT NULL
         AND cs2.RevokedAtUtc IS NULL
         AND (cs2.ExpiresAtUtc IS NULL OR cs2.ExpiresAtUtc > NOW())
     );
   ```

2. **Triage and prioritize** (within 30 minutes):
   - Identify critical production clients (higher priority)
   - Group clients by owner/team for batch notification
   - Assign responders to each group

3. **Batch emergency response**:
   - Script mass secret generation (see [Automation](#automation) below)
   - Notify all affected client owners simultaneously (email + Slack blast)
   - Set up war room/Slack channel for coordinated response

4. **Post-mortem** (within 1 week):
   - Analyze why mass expiry occurred (monitoring gap, process failure)
   - Implement automated rotation reminders
   - Consider extending default expiry period if justified

---

## Maintenance Procedures

### Database Cleanup: Revoked Secrets

**Frequency**: Quarterly  
**Purpose**: Remove old revoked/expired secrets to reduce database size

**Procedure:**

1. **Identify cleanup candidates** (revoked >1 year ago):

   ```sql
   SELECT Id, ClientId, Description, RevokedAtUtc
   FROM ClientSecrets
   WHERE RevokedAtUtc < NOW() - INTERVAL '1 year';
   ```

2. **Archive audit records** (if required for compliance):

   ```sql
   -- Export to archive table or external storage
   INSERT INTO ClientSecretsArchive
   SELECT * FROM ClientSecrets
   WHERE RevokedAtUtc < NOW() - INTERVAL '1 year';
   ```

3. **Delete old secrets**:

   ```sql
   DELETE FROM ClientSecrets
   WHERE RevokedAtUtc < NOW() - INTERVAL '1 year';
   ```

4. **Verify deletion**:

   ```sql
   SELECT COUNT(*) FROM ClientSecrets WHERE RevokedAtUtc IS NOT NULL;
   ```

### Legacy Secret Migration

**Purpose**: Migrate clients using deprecated single `ClientSecretHash` to multi-secret model

**Status**: Optional (backward compatibility maintained indefinitely)

**Procedure:**

1. **Identify legacy clients**:

   ```sql
   SELECT Id, ClientId, ClientSecretHash
   FROM Clients
   WHERE ClientSecretHash IS NOT NULL
     AND NOT EXISTS (
       SELECT 1 FROM ClientSecrets cs WHERE cs.ClientId = Clients.Id
     );
   ```

2. **Migrate via Admin API** (per client):

   ```bash
   curl -X POST \
     "https://auth.example.com/api/admin/clients/{clientId}/migrate-secrets" \
     -H "Authorization: Bearer $ADMIN_TOKEN"
   ```

   This creates a `ClientSecret` record from the legacy hash, marks as primary and activated, then clears `ClientSecretHash`.

3. **Verify migration**:

   ```bash
   curl "https://auth.example.com/api/admin/clients/{clientId}/secrets" \
     -H "Authorization: Bearer $ADMIN_TOKEN"
   ```

   Should show 1 active secret with description "Migrated from legacy secret"

4. **Test authentication** before proceeding to next client

5. **Notify client owner** to rotate secret when convenient (not urgent)

---

## Rollback Scenarios

### Rollback: Secret Revoked by Mistake

**Scenario**: Admin accidentally revokes active secret; client authentication fails

**Recovery:**

Unfortunately, **revoked secrets cannot be un-revoked** (no "undo" button). Instead:

1. Generate new secret immediately:

   ```bash
   curl -X POST \
     "https://auth.example.com/api/admin/clients/{clientId}/secrets" \
     -H "Authorization: Bearer $ADMIN_TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"description":"Replacement for accidentally revoked secret","expiresInDays":90,"activateImmediately":true}'
   ```

2. Provide new secret to client owner via secure channel

3. Client must update application config and redeploy

**Prevention**: Always confirm revocation via UI dialog; double-check client ID before revoking

### Rollback: Client Deployment Failed After Rotation

**Scenario**: Client deployed new secret but application failed to start; authentication down

**Recovery:**

If old secret still active (not revoked):

1. **Client rolls back deployment** to previous version with old secret
2. Authentication resumes with old secret
3. Revoke new (unused) secret to avoid confusion
4. Retry rotation after fixing deployment issue

If old secret already revoked:

1. **Re-activate old secret** (not currently supported in UI — requires DB update):

   ```sql
   UPDATE ClientSecrets
   SET RevokedAtUtc = NULL
   WHERE Id = '{old-secret-id}';
   ```

2. Invalidate cache:

   ```bash
   # Trigger cache invalidation (implementation-specific)
   ```

3. Client rolls back deployment
4. Document this as emergency procedure; fix deployment issue before re-attempting rotation

---

## Security Audit Procedures

### Quarterly Security Review

**Checklist:**

- [ ] **Secret expiry compliance**: All production clients have expiry dates ≤90 days
- [ ] **Rotation frequency**: All production clients rotated within last 90 days
- [ ] **Orphaned secrets**: No clients with >3 active secrets
- [ ] **Audit trail**: All secret lifecycle events have `CreatedBy/ActivatedBy/RevokedBy` populated
- [ ] **Access control**: Admin API endpoints require appropriate RBAC roles
- [ ] **Secret storage**: All secrets hashed with Argon2id (verify hash prefix `$argon2id$`)
- [ ] **Logging**: No plaintext secrets in application logs (grep for `MrWho_` prefix)
- [ ] **Tenant isolation**: Cross-tenant secret access tests fail (pen test)

### Annual Penetration Testing

Include client secret scenarios:

- **Test 1**: Attempt to authenticate with expired secret (should fail)
- **Test 2**: Attempt to authenticate with revoked secret (should fail)
- **Test 3**: Attempt to retrieve secret via Admin API (should return error, not secret value)
- **Test 4**: Attempt to access another tenant's client secrets (should fail with 404/403)
- **Test 5**: Timing attack on secret validation (should have constant-time comparison)

---

## Automation

### Scripted Secret Generation (Bash)

```bash
#!/bin/bash
# generate-client-secret.sh
# Usage: ./generate-client-secret.sh <client-id> <description>

CLIENT_ID=$1
DESCRIPTION=$2
ADMIN_TOKEN=${ADMIN_TOKEN:-$(cat ~/.mrwhooidc-admin-token)}

RESPONSE=$(curl -s -X POST \
  "https://auth.example.com/api/admin/clients/${CLIENT_ID}/secrets" \
  -H "Authorization: Bearer ${ADMIN_TOKEN}" \
  -H "Content-Type: application/json" \
  -d "{\"description\":\"${DESCRIPTION}\",\"expiresInDays\":90,\"activateImmediately\":false}")

SECRET_VALUE=$(echo $RESPONSE | jq -r '.secretValue')
SECRET_ID=$(echo $RESPONSE | jq -r '.secretId')

echo "✅ Secret generated successfully"
echo "Secret ID: ${SECRET_ID}"
echo "Secret Value (save this now): ${SECRET_VALUE}"
echo ""
echo "Next steps:"
echo "1. Provide secret to client owner via secure channel"
echo "2. After client updates config, activate: ./activate-secret.sh ${CLIENT_ID} ${SECRET_ID}"
```

### Bulk Expiry Notification (Python)

```python
#!/usr/bin/env python3
# notify-expiring-secrets.py

import requests
import datetime

ADMIN_TOKEN = os.getenv('ADMIN_TOKEN')
API_BASE = 'https://auth.example.com/api/admin'

# Find clients with secrets expiring in 7 days
response = requests.get(
    f'{API_BASE}/clients',
    headers={'Authorization': f'Bearer {ADMIN_TOKEN}'}
)
clients = response.json()

for client in clients:
    secrets_response = requests.get(
        f'{API_BASE}/clients/{client["id"]}/secrets',
        headers={'Authorization': f'Bearer {ADMIN_TOKEN}'}
    )
    secrets = secrets_response.json()['secrets']
    
    for secret in secrets:
        if secret['status'] == 'active' and secret.get('expiresAtUtc'):
            expiry = datetime.datetime.fromisoformat(secret['expiresAtUtc'])
            days_remaining = (expiry - datetime.datetime.now(datetime.timezone.utc)).days
            
            if days_remaining <= 7:
                print(f"⚠️  {client['clientId']}: Secret expires in {days_remaining} days")
                # TODO: Send email/Slack notification to client owner
```

---

## Appendix: Admin API Quick Reference

| Operation | Method | Endpoint |
|-----------|--------|----------|
| List secrets | GET | `/api/admin/clients/{id}/secrets` |
| Create secret | POST | `/api/admin/clients/{id}/secrets` |
| Activate secret | POST | `/api/admin/clients/{id}/secrets/{secretId}/activate` |
| Set primary | POST | `/api/admin/clients/{id}/secrets/{secretId}/set-primary` |
| Revoke secret | DELETE | `/api/admin/clients/{id}/secrets/{secretId}` |
| Health check | GET | `/health/client-secrets` |

**Authorization**: All endpoints require `Authorization: Bearer {admin-token}` header with `admin:clients:write` scope.

---

## Related Documentation

- [Client Secret Rotation Guide](client-secret-rotation-guide.md) — User-facing rotation instructions
- [Key Rotation Playbook](key-rotation-playbook.md) — Similar patterns for signing key rotation
- [Admin Guide](admin-guide.md) — General admin UI usage
- [Telemetry Taxonomy](telemetry-taxonomy.md) — Metrics and logging reference

---

**Document History:**

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-10-17 | AI Assistant | Initial operational playbook |
