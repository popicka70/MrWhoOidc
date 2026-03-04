# Security Incident Response Plan

This document outlines the procedures for responding to security incidents affecting MrWhoOidc deployments.

## Incident Classification

### P0 - Critical Security Breach
**Response Time:** Immediate (within 15 minutes)

Examples:
- Active unauthorized access to production systems
- Confirmed data breach involving user credentials or PII
- Compromise of signing keys or certificates
- Ransomware or destructive attack in progress
- Complete service outage due to security incident

### P1 - High Severity
**Response Time:** Within 1 hour

Examples:
- Suspected unauthorized access (under investigation)
- Vulnerability exploitation attempt detected
- Partial data exposure (non-credential)
- Authentication bypass discovered
- Critical security control failure

### P2 - Medium Severity
**Response Time:** Within 4 hours

Examples:
- Security misconfiguration discovered
- Failed authentication attacks (brute force, credential stuffing)
- Suspicious activity patterns detected
- Non-critical vulnerability exploitation attempt
- Security monitoring system failure

### P3 - Low Severity
**Response Time:** Within 24 hours

Examples:
- Policy violations (non-malicious)
- Minor security configuration issues
- Informational security alerts
- Security documentation gaps
- Low-risk vulnerability disclosures

## Breach Response Procedures

### Phase 1: Detection and Triage (0-30 minutes)

1. **Acknowledge the alert**
   - Log incident start time
   - Assign incident commander
   - Open incident communication channel

2. **Initial assessment**
   - Determine incident classification (P0-P3)
   - Identify affected systems and scope
   - Check for active threats

3. **Immediate containment (if active threat)**
   - Isolate affected systems
   - Revoke compromised credentials
   - Block suspicious IP addresses
   - Enable emergency maintenance mode if needed

### Phase 2: Investigation (30 minutes - 4 hours)

1. **Evidence collection**
   - Preserve log files (authentication, access, audit)
   - Capture system state snapshots
   - Document timeline of events
   - Identify attack vectors

2. **Impact assessment**
   - Determine data exposure scope
   - Identify affected users/tenants
   - Assess system integrity
   - Evaluate business impact

3. **Root cause analysis**
   - Identify vulnerability or misconfiguration
   - Determine if exploit was successful
   - Document attack methodology

### Phase 3: Eradication and Recovery (4-24 hours)

1. **Remove threat**
   - Patch vulnerabilities
   - Remove malicious access
   - Reset compromised credentials
   - Update security configurations

2. **System recovery**
   - Restore from clean backups if needed
   - Verify system integrity
   - Re-enable services gradually
   - Monitor for re-infection

3. **Credential rotation**
   - Rotate all potentially compromised keys
   - Update client secrets
   - Regenerate signing certificates
   - Notify affected parties

### Phase 4: Post-Incident (24-72 hours)

1. **Communication**
   - Notify affected users (if required)
   - Report to management/stakeholders
   - Prepare public statement (if needed)
   - Contact law enforcement (if required)

2. **Documentation**
   - Complete incident report
   - Update runbooks based on learnings
   - Document lessons learned
   - Update threat models

## Key Compromise Response

### Signing Key Compromise

**Immediate Actions:**
1. Generate new signing key pair immediately
2. Update JWKS endpoint with new public key
3. Set `kid` (key ID) to new value
4. Revoke all tokens signed with compromised key
5. Force re-authentication for all active sessions

**Communication:**
1. Notify all relying parties (clients)
2. Update key rotation documentation
3. Coordinate with downstream systems

### Certificate Compromise

**Immediate Actions:**
1. Revoke compromised certificate at CA
2. Generate new certificate
3. Deploy new certificate to all instances
4. Update certificate pinning if used

**Verification:**
1. Test TLS connections
2. Verify certificate chain
3. Confirm OCSP/CRL status

### Client Secret Compromise

**Immediate Actions:**
1. Rotate affected client secret
2. Revoke all tokens issued to compromised client
3. Audit client activity for unauthorized access
4. Notify client application owner

**Prevention:**
1. Review secret rotation policies
2. Implement secret expiration
3. Enable client secret rotation reminders

## Audit Trail Access Procedures

### Accessing Authentication Logs

```sql
-- Recent authentication attempts
SELECT 
    "Timestamp",
    "TenantId",
    "ClientId",
    "UserId",
    "Success",
    "FailureReason",
    "IpAddress"
FROM "AuthenticationEvents"
WHERE "Timestamp" > NOW() - INTERVAL '24 hours'
ORDER BY "Timestamp" DESC;
```

### Accessing Token Activity

```sql
-- Token issuance and validation
SELECT 
    "Timestamp",
    "TokenType",
    "ClientId",
    "UserId",
    "Scopes",
    "Success"
FROM "TokenEvents"
WHERE "Timestamp" > NOW() - INTERVAL '24 hours'
ORDER BY "Timestamp" DESC;
```

### Accessing Administrative Actions

```sql
-- Admin activity audit
SELECT 
    "Timestamp",
    "AdminUserId",
    "Action",
    "ResourceType",
    "ResourceId",
    "TenantId",
    "Success"
FROM "AuditLog"
WHERE "Timestamp" > NOW() - INTERVAL '7 days'
ORDER BY "Timestamp" DESC;
```

### Log Export for Forensics

```bash
# Export logs to secure location
docker compose exec webauth \
  cat /app/logs/*.json | \
  gzip > /secure/audit-logs-$(date +%Y%m%d-%H%M%S).json.gz
```

## Contact Information Template

### Internal Contacts

| Role | Name | Email | Phone | Availability |
|------|------|-------|-------|--------------|
| Incident Commander | [Name] | [email] | [phone] | 24/7 |
| Security Lead | [Name] | [email] | [phone] | Business hours |
| Platform Admin | [Name] | [email] | [phone] | 24/7 on-call |
| Database Admin | [Name] | [email] | [phone] | Business hours |
| Legal/Compliance | [Name] | [email] | [phone] | Business hours |

### External Contacts

| Organization | Contact | Purpose |
|--------------|---------|---------|
| Hosting Provider | [Support] | Infrastructure issues |
| Certificate Authority | [Support] | Certificate revocation |
| Law Enforcement | [Contact] | Criminal activity |
| Regulatory Body | [Contact] | Breach notification |
| Security Vendor | [Support] | Tool support |

### Communication Templates

**Initial Incident Notification:**
```
Subject: [SECURITY INCIDENT] - [Brief Description] - P[Severity]

Team,

A security incident has been detected:

- Incident ID: [INC-YYYY-NNNN]
- Classification: P[0-3]
- Detected: [Timestamp UTC]
- Affected Systems: [List]
- Current Status: [Investigating/Contained/Resolved]

Incident Commander: [Name]
Communication Channel: [Slack/Teams channel]

Next update: [Time]
```

**User Notification (if required):**
```
Subject: Important Security Notice from MrWhoOidc

Dear User,

We are writing to inform you of a security incident that may have 
affected your account.

What happened: [Brief, clear description]
When: [Date/time range]
What information was involved: [Specific data types]

What we are doing:
- [Action 1]
- [Action 2]
- [Action 3]

What you should do:
- [Recommended action 1]
- [Recommended action 2]

For more information: [Contact/URL]

We sincerely apologize for any inconvenience.

[Company Name] Security Team
```

## Post-Incident Review Process

### Timeline

1. **Immediate (0-24 hours):** Initial incident report
2. **Short-term (24-72 hours):** Detailed analysis complete
3. **Long-term (1-2 weeks):** Full post-mortem and improvements implemented

### Post-Mortem Document Structure

1. **Executive Summary**
   - Incident overview
   - Business impact
   - Key findings

2. **Timeline**
   - Detection time
   - Response actions
   - Resolution time

3. **Root Cause Analysis**
   - Primary cause
   - Contributing factors
   - Why it wasn't prevented

4. **Impact Assessment**
   - Users affected
   - Data exposed
   - Downtime duration
   - Financial impact

5. **Response Evaluation**
   - What worked well
   - What didn't work
   - Gaps in procedures

6. **Corrective Actions**
   - Immediate fixes applied
   - Long-term improvements
   - Owner and deadline for each

7. **Lessons Learned**
   - Process improvements
   - Tool enhancements
   - Training needs

### Continuous Improvement

1. **Update runbooks** based on incident learnings
2. **Improve monitoring** to detect similar issues earlier
3. **Conduct training** on identified gaps
4. **Test improvements** through tabletop exercises
5. **Review and update** this incident response plan quarterly

## Appendix: Quick Reference

### Emergency Commands

```bash
# Enable maintenance mode
docker compose exec webauth \
  dotnet MrWhoOidc.WebAuth.dll --maintenance-mode

# Force token revocation for all users
docker compose exec webauth \
  dotnet MrWhoOidc.WebAuth.dll --revoke-all-tokens

# Emergency key rotation
docker compose exec webauth \
  dotnet MrWhoOidc.WebAuth.dll --rotate-signing-keys
```

### Critical Configuration Files

- `/app/appsettings.Production.json` - Main configuration
- `/app/certs/` - Signing certificates
- `/app/logs/` - Application logs
- `/var/lib/postgresql/data/` - Database files

### Security Monitoring Endpoints

- Health: `https://<host>/health`
- Ready: `https://<host>/ready`
- Metrics: `https://<host>/metrics`
- Audit Log: `/admin/audit-log`

---

**Document Version:** 1.0  
**Last Updated:** 2025-01-XX  
**Review Schedule:** Quarterly  
**Owner:** Security Team
