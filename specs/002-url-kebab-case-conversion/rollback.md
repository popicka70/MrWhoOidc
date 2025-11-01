# Rollback Procedure: URL Convention Migration to kebab-case

**Feature**: 002-url-kebab-case-conversion  
**Purpose**: Restore PascalCase URLs if kebab-case migration causes critical issues  
**Rollback Window**: 4 hours post-deployment (after that, external parties may have updated)

---

## ⚠️ CRITICAL WARNING

**This rollback restores PascalCase URLs. External parties who already updated their configurations to kebab-case will experience integration failures until they revert.**

**Decision Authority**: Deployment Lead or Platform Engineer

**Execution Time**: ~15 minutes (code revert + deployment)

---

## Rollback Decision Criteria

Execute rollback if any of the following occur within **4 hours** of production deployment:

1. **Authentication failure rate** exceeds 5% (baseline < 0.1%)
2. **Federation callback failure rate** exceeds 20% (baseline < 1%)
3. **Critical security vulnerability** discovered in kebab-case implementation
4. **Application crashes** or fails to start
5. **Database corruption** detected
6. **Major external partner** (e.g., Google, Microsoft IdP) reports broken integration with high impact
7. **More than 5 critical support tickets** related to URL migration within 2 hours

---

## Pre-Rollback Validation

**Before initiating rollback, verify the issue is URL-related:**

```bash
# Check 404 error rate (high 404s indicate URL issues)
curl -s https://[domain]/health | jq '.http404Rate'

# Check authentication success rate
curl -s https://[domain]/health | jq '.authSuccessRate'

# Check federation callback success rate
curl -s https://[domain]/health | jq '.federationCallbackSuccessRate'
```

**If metrics show critical degradation, proceed with rollback.**

---

## Rollback Procedure

### Step 1: Notify Stakeholders (T=0, Duration: 2 minutes)

- [ ] Post "Rollback in progress" on status page
- [ ] Send urgent notification to deployment team via Slack (#deployment-002)
- [ ] Alert support team to expect integration issue reports
- [ ] Log rollback decision with reason in incident tracker

### Step 2: Database Backup (T+2, Duration: 3 minutes)

```bash
# Create pre-rollback database snapshot
pg_dump -h [db-host] -U [db-user] -d authdb > rollback-backup-$(date +%Y%m%d-%H%M%S).sql
```

- [ ] Verify backup file size is non-zero
- [ ] Store backup in secure location

### Step 3: Revert Code Changes (T+5, Duration: 2 minutes)

**Option A: Git Revert (Recommended)**

```bash
# Checkout previous commit (before kebab-case migration)
git checkout [previous-commit-sha]

# Create rollback branch
git checkout -b rollback/002-url-kebab-case-$(date +%Y%m%d-%H%M%S)

# Push rollback branch
git push origin rollback/002-url-kebab-case-$(date +%Y%m%d-%H%M%S)
```

**Option B: Revert Specific Commit**

```bash
# Revert the kebab-case migration commit
git revert [kebab-case-commit-sha] --no-edit

# Push revert commit
git push origin main
```

- [ ] Confirm revert commit is on correct branch
- [ ] Verify no uncommitted changes remain

### Step 4: Rebuild Application (T+7, Duration: 3 minutes)

```bash
# Clean build artifacts
dotnet clean

# Rebuild solution
dotnet build --configuration Release

# Verify build succeeded (exit code 0)
echo $LASTEXITCODE  # Should be 0
```

- [ ] Verify no build errors
- [ ] Verify output assemblies are present in `bin/Release/`

### Step 5: Deploy Rollback to Production (T+10, Duration: 5 minutes)

**Follow standard deployment procedure with reverted code:**

```bash
# Deploy to production (exact command depends on infrastructure)
# Example for Docker:
docker build -t mrwhooidc:rollback .
docker stop mrwhooidc-prod
docker rm mrwhooidc-prod
docker run -d --name mrwhooidc-prod -p 443:443 mrwhooidc:rollback

# Example for Kubernetes:
kubectl set image deployment/mrwhooidc mrwhooidc=mrwhooidc:rollback
kubectl rollout status deployment/mrwhooidc
```

- [ ] Application starts successfully
- [ ] Health check endpoint returns 200 OK

### Step 6: Smoke Testing (T+15, Duration: 5 minutes)

```bash
# Test discovery document (should show PascalCase URLs)
curl https://[domain]/.well-known/openid-configuration | jq '.authorization_endpoint'
# Expected: https://[domain]/connect/authorize (unchanged)

# Test protocol endpoints (should work with PascalCase)
curl -I https://[domain]/Auth/External/Start
# Expected: 405 Method Not Allowed (or 302 redirect), NOT 404

# Test admin UI (should work with PascalCase)
curl -I https://[domain]/Admin/Providers
# Expected: 200 OK or 302 redirect, NOT 404
```

- [ ] Discovery document contains original URLs
- [ ] `/Auth/External/Start` returns 405 or 302 (not 404)
- [ ] `/admin/providers` returns 404 (kebab-case URL no longer works)
- [ ] `/Admin/Providers` returns 200 or 302 (PascalCase URL restored)

### Step 7: Post-Rollback Communication (T+20, Duration: 10 minutes)

**Immediate Notification (Within 30 minutes):**

**Subject**: [URGENT] MrWhoOidc URL Migration Rolled Back - Action Required

Dear [Recipient Name],

The URL convention migration deployed earlier today has been **rolled back** due to [brief reason, e.g., "critical integration issues"].

### Immediate Action Required

**All PascalCase URLs are restored. If you already updated your configuration to kebab-case, you MUST revert immediately:**

1. **External IdPs**: Change callback URL back to `https://[domain]/Auth/External/Callback`
2. **RP Clients**: Revert to PascalCase URLs or re-fetch discovery document
3. **Admin Users**: Use PascalCase URLs (e.g., `/Admin/Providers`, not `/admin/providers`)

### What Happened

[Brief explanation of rollback reason, e.g., "We detected elevated authentication failure rates affecting X% of users. Out of abundance of caution, we rolled back the change to ensure service stability."]

### Next Steps

We are investigating the root cause and will provide an updated migration timeline within 48 hours.

**Do NOT use kebab-case URLs until further notice.**

### Support

If you experience continued integration issues, contact:

- **Emergency Hotline**: [phone]
- **Email**: [support-email]

We apologize for the disruption and appreciate your understanding.

The MrWhoOidc Team

---

- [ ] Send urgent rollback notification to all external parties
- [ ] Update status page: "Migration rolled back, service restored"
- [ ] Post rollback announcement in internal Slack
- [ ] Alert customer success team to contact high-value partners directly

### Step 8: Monitoring (T+30, Duration: 2 hours)

**Monitor critical metrics for 2 hours post-rollback:**

- [ ] T+30: Check authentication success rate (should return to baseline)
- [ ] T+45: Check federation callback success rate (should return to baseline)
- [ ] T+60: Check HTTP 404 error rate (should return to baseline)
- [ ] T+90: Check support ticket queue (should decline)
- [ ] T+120: Final checkpoint - verify all metrics normal

**Expected Recovery:**

- Authentication success rate: >= 99.9% (baseline)
- Federation callback success rate: >= 99% (baseline)
- HTTP 404 error rate: < 0.5% (baseline)
- Critical support tickets: 0

### Step 9: Incident Review (T+2 hours, Duration: 1 week)

- [ ] Schedule incident retrospective meeting (within 48 hours)
- [ ] Document root cause analysis
- [ ] Identify preventative measures
- [ ] Update migration plan with lessons learned
- [ ] Re-evaluate kebab-case migration feasibility
- [ ] Create revised timeline (if proceeding with migration)

---

## Post-Rollback Actions

### Immediate (Within 24 hours)

- [ ] Publish detailed incident report
- [ ] Update documentation to reflect rollback
- [ ] Archive kebab-case code changes for future reference
- [ ] Conduct internal post-mortem with deployment team
- [ ] Review and update rollback procedure based on execution

### Short-Term (Within 1 week)

- [ ] Analyze logs to identify root cause of migration failure
- [ ] Determine if issue was code-related or external-party-related
- [ ] Decide on migration strategy:
  - **Option A**: Fix issues and reschedule migration with longer notice period
  - **Option B**: Implement dual-route support (backward compatibility)
  - **Option C**: Cancel migration and maintain PascalCase convention
- [ ] Communicate decision to external parties

### Long-Term (Within 1 month)

- [ ] If reattempting migration:
  - Extend notification period to 60 days
  - Implement more robust testing (chaos testing, load testing)
  - Consider gradual rollout (staging → 10% traffic → 50% traffic → 100%)
  - Implement feature flag for easy toggling
  - Add comprehensive monitoring/alerting
- [ ] Update incident response playbook

---

## Rollback Checklist Summary

**Critical Path (0-20 minutes):**

1. ✅ Notify stakeholders (2 min)
2. ✅ Backup database (3 min)
3. ✅ Revert code (2 min)
4. ✅ Rebuild application (3 min)
5. ✅ Deploy rollback (5 min)
6. ✅ Smoke test (5 min)
7. ✅ Communicate rollback (10 min)

**Monitoring (20 min - 2 hours 20 min):**

8. ✅ Monitor metrics recovery (2 hours)

**Follow-Up (2 hours+):**

9. ✅ Incident review and lessons learned (1 week)

---

## Rollback Validation Criteria

**Rollback is successful when:**

- [ ] Application is running and accessible
- [ ] PascalCase URLs return 200 OK (or appropriate status)
- [ ] kebab-case URLs return 404 (as expected pre-migration)
- [ ] Authentication success rate >= 99.9%
- [ ] Federation callback success rate >= 99%
- [ ] HTTP 404 error rate < 0.5%
- [ ] Zero critical support tickets for 30 minutes
- [ ] All external parties notified of rollback

---

## Emergency Contacts

| Role | Name | Phone | Email |
|------|------|-------|-------|
| Deployment Lead | [Name] | [Phone] | [Email] |
| Platform Engineer | [Name] | [Phone] | [Email] |
| Database Admin | [Name] | [Phone] | [Email] |
| Support Lead | [Name] | [Phone] | [Email] |
| Executive Sponsor | [Name] | [Phone] | [Email] |

---

## Notes

- **Practice Rollback**: Rehearse this procedure in staging environment before production deployment
- **Rollback Window**: After 4 hours, external parties may have updated configurations; rollback becomes more disruptive
- **Communication**: Over-communicate during rollback - stakeholders prefer transparency over silence
- **Blame-Free**: Focus on technical root cause, not individual blame
- **Documentation**: Record all decisions, timestamps, and observations for post-mortem
