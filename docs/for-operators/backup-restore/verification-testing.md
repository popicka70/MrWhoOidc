# Backup Verification and Recovery Exercises

A successful backup job is not proof that the service can be recovered. Test restoration of PostgreSQL together with the matching application version, configuration, and DataProtection decryption material.

This repository does not provide a ready-to-run Helm, Terraform, or regional failover environment. Provision an isolated recovery environment using your organization's infrastructure tooling. Do not run recovery tests against the production connection string or volumes.

## Recovery Objectives

The operator must define acceptable data loss (RPO), recovery time (RTO), backup frequency, retention, and who can authorize a recovery. Record the basis for those choices. A daily backup schedule does not guarantee a 24-hour RPO if jobs fail or retained backups cannot be restored.

Measure elapsed recovery time and the latest recovered data timestamp during exercises. Include access to secrets, infrastructure provisioning, restoration, application checks, and traffic cutover in the measurement. Do not claim synchronous replication or point-in-time recovery unless those facilities are configured and tested.

## Recovery Set

- PostgreSQL backup and, if configured, the WAL/base-backup chain required for point-in-time recovery.
- Exact application image digest or source commit and build record.
- Compose files, overrides, deployment settings, and references to securely stored credentials.
- DataProtection certificates, their private keys and passwords, including older certificates still needed to decrypt retained key-ring entries.
- TLS and upstream/client integration configuration required to resume service.

Client secrets are stored as hashes, not recoverable plaintext. Relying-party DPoP private keys belong to those clients; do not assume the IdP backup contains them. Protect restoration environments as sensitive systems because they contain real identity data.

## Exercise Procedure

1. Select a backup and record its creation time, source version, format, checksum, and backup-job exit status. Compression integrity and file size alone are insufficient.
2. Provision an isolated, empty PostgreSQL database with compatible tools. Confirm the destination host, database, Compose project, and volumes with another operator for production recovery work.
3. Disable outbound SMTP, back-channel delivery, and other integrations through environment/network isolation before starting a restored application. A recovered outbox must not send historical notifications to real clients during a drill.
4. Restore with error handling enabled. For custom-format dumps, use `pg_restore --exit-on-error`; for plain SQL, use `psql -v ON_ERROR_STOP=1`. See the [backup and recovery examples](../../deployment-guide.md#backup-and-recovery).
5. Inspect restore errors before retrying. Do not add `DROP DATABASE`, volume deletion, or ignore-errors options as automatic remediation.
6. Restore the required configuration and DataProtection material, then start the application version associated with the backup. WebAuth applies migrations at startup, so starting a newer image is also an upgrade test.
7. Verify the checks below, record results, and retain evidence with secrets and personal data redacted.
8. Remove the isolated test environment only after recording results and confirming its identity. Never use an unqualified cleanup command against shared infrastructure.

## Application Checks

| Check | Evidence to collect |
| --- | --- |
| Restore completion | Tool exit status and errors; expected schema and migration history |
| Tenant and client data | Compare known records and counts with the backup baseline using the restored schema, not generic SQL table names |
| Administrative access | Authorized test administrator can sign in and access only the intended tenant |
| Signing and protected data | JWKS is available; token issuance works; no key-ring decryption errors |
| Client integration | A controlled authorization-code/token flow completes against the restored issuer configuration |
| Revocation and sessions | Verify the acceptance window and stale-session behavior after recovery |
| Isolation | No real email, webhook, or back-channel recipient received drill traffic |
| Freshness | Latest recovered data meets the operator's RPO; elapsed recovery meets the agreed RTO |

Database recovery can reintroduce state from before revocation or account changes. Assess which sessions, grants, refresh tokens, and client credentials need invalidation or reconciliation before production cutover. Restoring data is not sufficient authorization to reopen traffic.

## Exercise Record

Record the operator, reviewer, UTC start/end, source backup, destination, application version, decryption material identifiers (not secret values), restore result, functional checks, measured RTO/RPO, failures, and assigned follow-up actions.

Schedule exercises based on risk and repeat after changes to database versions, migrations, encryption keys, backup tooling, or hosting. Retention and notification obligations need approval from the responsible organization; this guide does not prescribe legal retention periods.

## Related Procedures

- [Upgrade and rollback](../../upgrade-guide.md)
- [Incident response](../../for-security-teams/incident-response.md)
- [Monitoring](../monitoring/alerting-rules.md)

Reviewed 2026-09-05. The documentation review did not execute a backup, restore, or failover exercise.
