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

## Upgrade Notes for the Current Release

- **Migration `PersistAuthorizationCodeLoginContext`** adds nullable columns (`Sid`, `UpstreamIdp`, `UpstreamAcr`, `UpstreamAmr`, `MappedClaimsJson`) to authorization codes. It is additive; authorization codes issued by the old version before the upgrade lose their login context, so drain in-flight logins.
- **Introspection is deny-by-default.** Resource servers that introspect tokens for an audience other than their own `client_id` need a per-client `IntrospectionAudiencesJson` or `Auth__IntrospectionPermissions__<clientId>__N` entry, otherwise they receive `{"active":false}`.
- **Resource indicators** (`resource`) must be in `Auth__ApiAudiences` or the client's allowed-audience list; others fail with `invalid_target`.
- **Client authentication** enforces the registered `token_endpoint_auth_method` and grant types. Confidential clients without a valid credential (including all secrets revoked/expired) can no longer authenticate with `client_id` alone; `/device/authorize` and `/bc-authorize` require `AllowDeviceAuthorization`/`AllowCiba`.
- **Forwarded client certificates** (`Security__CertificateForwarding__Enabled`) are honoured only from loopback or `ForwardedHeaders` known proxies/networks. Add the proxy address before upgrading mTLS deployments.
- **Background jobs** (key rotation, token/PAR cleanup, back-channel logout dispatch) now run for every active tenant. Non-default tenants whose keys were never rotated, or whose expired rows were never pruned, may see rotation and cleanup activity on the first run.
- **Private signing keys** are no longer written to Redis; no action is needed.
- **The tenant data filter fails closed.** Before this release, a database query issued without a tenant context (background jobs, platform pages, startup code) returned every tenant's rows. It now returns no tenant-scoped rows; only platform-wide rows (`TenantId` null) of optional-tenant entities such as global scopes, platform identity providers and licences stay visible. Code paths that are cross-tenant by design now say so explicitly with `TenantFilterScope.BeginSystemScope()` or `IgnoreQueryFilters()`. These include tenantless `/platform-admin/**` and `/health/**` requests, the platform-admin and tenant-admin authorization checks, the tenant switcher, the CLI `seed` command, startup maintenance, the client-secret expiry monitor, the back-channel alert sampler and the public JWKS cache.
  - **Emergency escape hatch:** `MultiTenancy__TenantFilterFailOpen=true` (`MultiTenancy:TenantFilterFailOpen`) restores the old fail-open behaviour for queries without a tenant, and the server logs a `SECURITY` warning at startup while it is set. Use it only to recover from an outage caused by a tenantless code path that was missed, report that path, and remove the setting once it is fixed. A tenant set on the request is always enforced.
- **Migration `ClientScopeAndGrantDefaults`: client scopes and grants are default-deny.**
  - A client may request only the scopes assigned to it (ClientScopes); with nothing assigned only `openid` is allowed. This applies to `/authorize`, PAR, scopes implied by the `claims` parameter, and device/CIBA issuance (`offline_access` now needs an assignment there too).
  - A client without registered grant types (`GrantTypesJson` empty) may use only `authorization_code` + `refresh_token`. `AllowClientCredentials`, `AllowDeviceAuthorization` and `AllowCiba` now default to `false` for new clients and are derived from the client's grant types.
  - Existing clients keep their behaviour: the migration assigns every scope visible to the client's tenant (except the protected `tenants` and `mrwho:admin`) to clients that had none, creates the global `openid`/`profile`/`email`/`offline_access`/`roles` scopes if missing, and records the grants each client could already use (`authorization_code`, `refresh_token`, plus `client_credentials`/device/CIBA per the existing `Allow*` flag, plus token exchange unless OBO was disabled).
  - New clients: the admin UI Add page has grant-type checkboxes and assigns `openid profile email offline_access`; the admin API honours `scope` and `grantTypes`; DCR assigns the registered `scope` (unknown and protected scopes are dropped, and the effective list is echoed) or the defaults.
  - **Seed/import manifests:** add `"grantTypes": ["client_credentials"]` (or the device/CIBA/token-exchange URNs) to machine-to-machine and similar clients. Without `grantTypes`, a newly created client gets `authorization_code` + `refresh_token` only. An empty `allowedScopes` gives a new client the default scopes and leaves an existing client's scopes unchanged.
  - Rolling upgrade: a client created by an old pod after the migration has run has neither scopes nor grant types, so new pods allow it only `openid` and the default grants. Assign its scopes and grants after the rollout.
  - `Clients.RegistrationSource` (new, nullable) records the origin (`dcr`, `admin`, `api`, `import`, `seed`, `cli`); it is backfilled for DCR and CLI clients. DCR registrations emit the audit event `client.registered.dcr`.

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
| Certificate or key-ring error | Check mounted paths, permissions, passwords, and retained decryption certificates |
| Migration failure | Preserve logs, inspect database state, and compare with the tested migration path before retrying |
| Wrong image running | Inspect the deployed artifact and build/pull workflow, including overrides |
| Redis behavior changed | Review `REDIS_CONNECTION_STRING`; a nonempty value enables WebAuth's Redis connection |
| Wrong issuer or redirects | Compare public URL, proxy trust, and client registration with the previous configuration |

## Upgrade Record

Record old/new artifacts, migration set, backup reference, operator, test results, maintenance duration, and rollback decision. Retain recovery material according to the approved policy, including keys needed by older retained backups.
