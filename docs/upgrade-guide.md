# Upgrade and Rollback Guide

Use this guide with the [deployment guide](deployment-guide.md). WebAuth applies EF Core migrations during startup. An image update can therefore change persistent state; reverting the image alone is not always a rollback.

## Pre-Upgrade Checklist

- Record the running image digest or source commit, database version, and active Compose files/overrides.
- Review the target release's configuration changes and migrations. Verify available registry tags rather than assuming major/minor aliases exist.
- Back up PostgreSQL, deployment settings, and the certificates/private keys/passwords needed to decrypt the DataProtection key ring.
- Test restoration and the upgrade against an isolated copy. Keep real email and back-channel recipients unreachable during the drill.
- Define maintenance, traffic drain, rollback criteria, decision owner, and acceptable data loss from restoring the backup.
- Verify current health so pre-existing problems are not attributed to the upgrade.

## Backup Procedures

Use [backup and recovery](deployment-guide.md#backup-and-recovery) and the [verification procedure](for-operators/backup-restore/verification-testing.md). Check the backup command's exit status and perform a restore test. A nonempty file or a successful compression check does not verify database contents or decryption material.

## Version Pinning Strategy

For a published image, select an existing immutable digest or a release tag whose mutability policy you understand. Record the resolved digest. Do not use a moving `latest` tag as your rollback record.

For a source-built deployment, record the commit and build inputs. The source production Compose file contains both `image` and `build`; changing an image name alone does not establish which artifact your workflow deploys. Use the intended source-build or published-image workflow consistently.

Restarting a container does not pull a new image. Recreating a container does not rotate an existing PostgreSQL role password. Changing `.env` requires container recreation to apply the new environment.

## Upgrade Steps

These examples use the source Compose service name `webauth`. In the published deployment repository, use its service names. Include the same `-f` options throughout if your installation uses overrides.

1. Stage and validate the target configuration with `docker compose config --quiet`; do not publish expanded configuration containing secrets.
2. Prepare the target artifact before the maintenance window: pull the selected published image, or build the selected source revision.
3. Drain application traffic and stop all application replicas that could write during the migration. Leave PostgreSQL running. A maintenance window is safer than assuming mixed-version compatibility.
4. Start the intended application version and inspect startup logs for migration or production-configuration errors.

```sh
docker compose ps
docker compose logs --tail=100 webauth
```

For a published-image workflow with its image reference already pinned, `docker compose pull webauth` followed by `docker compose up -d --no-build webauth` selects the image rather than building source. For a source workflow, build the intended revision with `docker compose build webauth`, then recreate it with `docker compose up -d --no-build webauth`. Confirm the resulting artifact before reopening traffic.

## Automatic Database Migrations

Startup calls `Database.MigrateAsync` for relational databases and fails startup on an exception. Transaction behavior depends on the migration operations; do not infer that every failure leaves the database unchanged.

Inspect logs and migration history before retrying. Avoid concurrent migration attempts and do not manually mark migrations applied or remove migration history to force startup. Blue/green instances sharing one database still require schema compatibility; a second container does not isolate a database migration.

## Verification Steps

Replace the host and tenant slug with the deployment's actual values:

```sh
curl --fail --show-error https://auth.example.com/health
curl --fail --show-error https://auth.example.com/t/default/.well-known/openid-configuration
```

Verify administrative login, a representative client token flow, JWKS/key continuity, tenant isolation, and enabled dependencies. Check migration and decryption logs. Test SMTP and back-channel delivery only against controlled recipients before normal traffic resumes.

## Rollback Procedure

### Compatible Image Rollback

Use the previous recorded artifact and its matching configuration only after confirming it supports the current database schema and protected data. Drain traffic, replace the application artifact, and repeat verification. Do not assume semantic versioning alone proves schema compatibility.

### Database Recovery

If the previous application cannot use the migrated schema, restore the pre-upgrade recovery set into an isolated empty database and validate it using the [recovery procedure](for-operators/backup-restore/verification-testing.md). Stop all writers before cutover. Obtain explicit approval for losing or reconciling changes made since the backup.

Do not restore blindly into an existing populated database, delete Docker volumes, or drop the production database as a retry step. Recover matching DataProtection certificates as well as PostgreSQL data. Review stale sessions and revocations before accepting traffic.

## Troubleshooting Failed Upgrades

| Failure | Next action |
| --- | --- |
| Certificate or key-ring error | Check mounted paths, permissions, passwords, application name, and retained decryption certificates |
| Migration failure | Preserve logs, inspect database state, and compare with the tested migration path before retrying |
| Wrong image running | Inspect the deployed artifact and build/pull workflow, including overrides |
| Redis behavior changed | Review `REDIS_CONNECTION_STRING`; a nonempty value enables WebAuth's Redis connection |
| Wrong issuer or redirects | Compare public URL, proxy trust, and client registration with the previous configuration |

## Upgrade Record

Record old/new artifacts, migration set, backup reference, operator, test results, maintenance duration, and rollback decision. Retain recovery material according to the approved policy, including keys needed by older retained backups.

Reviewed 2026-09-05. No production upgrade or rollback was executed during this documentation review.
