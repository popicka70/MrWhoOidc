# Deployment Checklist: URL Convention Migration to kebab-case

**Feature**: 002-url-kebab-case-conversion  
**Target Date**: Day 30 (TBD)  
**Migration Strategy**: Clean break, no backward compatibility

---

## Pre-Deployment (Day 0 - Day 29)

### Day 0: Notification Campaign Launch

- [ ] Execute contact list generation queries (see `notification-template.md`)
- [ ] Export IdP contacts to CSV
- [ ] Export RP client contacts to CSV
- [ ] Export tenant admin contacts to CSV
- [ ] Send Day 0 notification email to all contacts
- [ ] Create notification tracking spreadsheet
- [ ] Schedule reminder emails (Day 7, 14, 21, 28)

### Day 7-28: Monitoring & Reminders

- [ ] Day 7: Send reminder email, update tracking spreadsheet
- [ ] Day 14: Send reminder email, update tracking spreadsheet
- [ ] Day 21: Send reminder email, update tracking spreadsheet
- [ ] Day 28: Send final 48-hour warning email, update tracking spreadsheet

### Day 20: Staging Environment Deployment

- [ ] Deploy kebab-case changes to staging environment
- [ ] Verify all endpoints return 200 OK
- [ ] Test OIDC authorization code flow end-to-end
- [ ] Test federated authentication callback
- [ ] Test admin UI navigation
- [ ] Test user account pages
- [ ] Notify all contacts that staging is ready for testing
- [ ] Monitor staging logs for integration attempts

### Day 25: Pre-Deployment Validation

- [ ] Run full test suite: `dotnet test`
- [ ] Verify zero PascalCase URLs in codebase: `grep -r "BuildTenantPath.*[A-Z]" MrWhoOidc.WebAuth/`
- [ ] Verify all `@page` directives use kebab-case
- [ ] Review staging environment logs for errors
- [ ] Confirm all external parties acknowledged notification (follow up with non-responders)
- [ ] Review rollback procedure with operations team
- [ ] Prepare deployment communication (status page, social media)

### Day 28: Final Preparations

- [ ] Lock code freeze for URL-related changes
- [ ] Create production deployment package
- [ ] Verify backup/restore procedures
- [ ] Schedule deployment window (low-traffic period recommended)
- [ ] Assign on-call engineer for deployment monitoring
- [ ] Prepare emergency contact list
- [ ] Test rollback procedure in staging environment
- [ ] Update status page: "Scheduled maintenance on Day 30"

---

## Deployment Day (Day 30)

### Pre-Deployment Window (T-2 hours)

- [ ] Verify database backup completed successfully
- [ ] Verify no critical alerts in monitoring dashboards
- [ ] Post "Maintenance in progress" on status page
- [ ] Send deployment start notification to all contacts
- [ ] Enable deployment logging (verbose mode)

### Deployment Execution (T=0)

- [ ] Deploy code to production environment
- [ ] Verify application starts successfully
- [ ] Fetch discovery document, confirm kebab-case endpoints
- [ ] Smoke test: GET `/.well-known/openid-configuration`
- [ ] Smoke test: GET `/auth/external/start` (expect 405 Method Not Allowed, not 404)
- [ ] Smoke test: GET `/Auth/External/Start` (expect 404 with kebab-case suggestion)
- [ ] Verify custom 404 handler shows helpful error message
- [ ] Test complete OIDC authorization code flow
- [ ] Test federated authentication callback
- [ ] Test admin UI login and navigation
- [ ] Test user account access

### Post-Deployment Monitoring (T+1 hour)

- [ ] Monitor HTTP 404 error rate (expect spike, then decline)
- [ ] Monitor authentication success rate (should remain stable)
- [ ] Monitor federation callback success rate (watch for IdP integration issues)
- [ ] Monitor application logs for unexpected errors
- [ ] Monitor support ticket queue for integration issues
- [ ] Update status page: "Maintenance complete"
- [ ] Send deployment complete notification to all contacts

### Post-Deployment Communication (T+2 hours)

- [ ] Post "All systems operational" on status page
- [ ] Send Day 31 follow-up email (see `notification-template.md`)
- [ ] Update documentation with new URL references
- [ ] Update API reference documentation
- [ ] Update integration guides
- [ ] Publish blog post announcing migration completion (optional)

---

## Post-Deployment (Day 31 - Day 37)

### Daily Monitoring (7 days)

- [ ] Day 31: Review 404 error rate (should decline as parties update configurations)
- [ ] Day 32: Review authentication success rate (should return to baseline)
- [ ] Day 33: Review federation callback success rate (watch for lagging IdPs)
- [ ] Day 34: Review support tickets for unresolved integration issues
- [ ] Day 35: Review 404 error patterns (identify stragglers)
- [ ] Day 36: Follow up with contacts reporting issues
- [ ] Day 37: Final monitoring checkpoint

### Week 1 Retrospective (Day 37)

- [ ] Analyze 404 error logs for most common PascalCase URLs accessed
- [ ] Review support ticket themes
- [ ] Document lessons learned
- [ ] Update migration playbook for future breaking changes
- [ ] Schedule follow-up with external parties experiencing ongoing issues
- [ ] Create final migration report for stakeholders

---

## Success Criteria

### Technical Validation

- [ ] All automated tests pass (100% pass rate)
- [ ] Zero PascalCase URLs remain in codebase (verified by grep)
- [ ] Discovery document contains only kebab-case endpoints
- [ ] Custom 404 handler provides helpful suggestions
- [ ] All admin UI navigation links work correctly
- [ ] All user-facing page links work correctly

### Integration Validation

- [ ] External IdP federation works with kebab-case callback URLs
- [ ] RP clients successfully connect using discovery-based configuration
- [ ] Manually configured RP clients updated and working
- [ ] Email confirmation links use kebab-case URLs
- [ ] Deep links in existing documentation updated

### Operational Validation

- [ ] Authentication success rate >= 99.9% (matches pre-migration baseline)
- [ ] Federation callback success rate >= 99% (minor drop acceptable during transition)
- [ ] HTTP 404 error rate declining over 7 days (parties updating configurations)
- [ ] No critical support tickets related to URL migration
- [ ] All external parties confirmed successful migration

---

## Rollback Decision Criteria

**Execute rollback if any of the following occur within first 4 hours of deployment:**

- [ ] Authentication success rate drops below 95%
- [ ] Federation callback success rate drops below 80%
- [ ] Critical security vulnerability discovered in new code
- [ ] Database corruption or data loss detected
- [ ] Application fails to start or experiences crashes
- [ ] More than 5 critical support tickets related to URL migration
- [ ] Major external partner (e.g., Google, Microsoft IdP) reports broken integration

**Rollback Procedure**: See `rollback.md`

---

## Deployment Team

| Role | Name | Contact |
|------|------|---------|
| Deployment Lead | [Name] | [Phone/Email] |
| Database Admin | [Name] | [Phone/Email] |
| Platform Engineer | [Name] | [Phone/Email] |
| Support Lead | [Name] | [Phone/Email] |
| External Comms | [Name] | [Phone/Email] |

---

## Communication Channels

| Channel | Purpose | URL/Contact |
|---------|---------|-------------|
| Status Page | Public deployment status | [URL] |
| Internal Slack | Real-time team coordination | #deployment-002 |
| Support Tickets | User-reported issues | [Ticket System URL] |
| Emergency Hotline | Critical issues | [Phone] |

---

## Monitoring Dashboards

| Dashboard | Metrics | URL |
|-----------|---------|-----|
| Application Health | Response times, error rates, throughput | [Dashboard URL] |
| Authentication Metrics | Login success, MFA usage, session counts | [Dashboard URL] |
| Federation Metrics | IdP callback success, token exchange rates | [Dashboard URL] |
| Infrastructure | CPU, memory, disk, network | [Dashboard URL] |

---

## Documentation Updates Required

- [ ] `docs/developer-guide.md` - Update all URL examples
- [ ] `docs/admin-guide.md` - Update admin UI navigation instructions
- [ ] `docs/idp-chaining-client-configuration.md` - Update callback URL examples
- [ ] `README.md` - Update quick start URLs
- [ ] API reference docs - Update all endpoint URLs
- [ ] Postman collection - Update all request URLs
- [ ] Integration guides - Update all code samples

---

## Notes

- **Traffic Pattern**: Schedule deployment during low-traffic window (e.g., 2 AM UTC)
- **Communication Timing**: Send notifications during business hours for best visibility
- **Rollback Window**: 4-hour window to decide on rollback (after that, external parties may have updated configurations)
- **Support Coverage**: Ensure 24/7 support coverage for first 3 days post-deployment
