# MrWhoOidc Deployment Guide

This guide covers container configuration, startup checks, and routine operations for WebAuth. For the first tenant and administrator, follow the [production bootstrap guide](production-setup-guide.md). For a seeded local environment, use [Getting Started](for-developers/quickstart-15-min.md).

## Prerequisites

- Docker with the Compose V2 plugin and access to the image registry.
- Git when using a repository checkout.
- A PostgreSQL database and credentials for applying the application's migrations.
- A public HTTPS URL and a certificate or TLS-terminating proxy trusted by your clients.
- A DataProtection certificate and a recovery plan for its private key and password.

Check Docker before configuring the application:

```sh
docker --version
docker compose version
docker ps
```

Shell examples use Bash unless marked PowerShell. The local certificate setup scripts require the .NET 10 SDK. Do not run development setup scripts against a production certificate directory.

## Quick Start

Choose the repository that matches the deployment:

| Repository and file | Purpose |
| --- | --- |
| [MrWho deployment repository](https://github.com/popicka70/MrWho#readme) | Run the published image; follow that repository's setup and release instructions |
| This repository's [docker-compose.dev.yml](../docker-compose.dev.yml) | Build the local development stack, with test settings, seeded accounts, and sample applications |
| This repository's [docker-compose.yml](../docker-compose.yml) | Production-shaped layout with a local build context; requires the full source checkout |

Downloading this repository's Compose file into an empty directory is not a source installation: its `build` section needs the Dockerfile and project files. Keep the checkout, `.env`, certificates, and any bind-mounted data in a persistent directory.

For the source repository's production Compose file:

1. Copy `.env.example` to `.env` only if you do not already have local configuration.
2. Configure the database, public URL, TLS, DataProtection, and proxy trust described below. Keep `ASPNETCORE_ENVIRONMENT=Production`.
3. Set a random, temporary `BOOTSTRAP_TOKEN` for a database with no tenants.
4. Validate Compose without printing expanded secrets, then build and start:

  ```sh
  docker compose config --quiet
  docker compose up -d --build
  docker compose ps
  docker compose logs --tail=100 webauth
  ```

After startup, complete the [bootstrap procedure](production-setup-guide.md#production-bootstrap-process). Compose loads `.env` for substitution; it does not export `BOOTSTRAP_TOKEN` into the calling shell.

Remove the bootstrap token and run `docker compose up -d` to apply that environment change. `docker compose restart` alone does not apply changed environment variables.

Check tenant discovery and sign in with the administrator created during bootstrap:

```sh
curl --fail --show-error https://auth.example.com/t/default/.well-known/openid-configuration
```

Replace the example host and tenant slug with yours. Inspect connection failures and HTTP error bodies before retrying bootstrap. A `409 already_bootstrapped` response means a tenant already exists, not that the database needs to be erased.

## System Requirements

Size the deployment from a representative workload. Concurrent login flows, password hashing, token exchange, audit volume, and database latency affect capacity differently.

Measure CPU, memory, disk growth, request latency, and error rate under load before setting production limits. Account for PostgreSQL backups and migration work as well as normal traffic. This guide does not establish a supported user count, request rate, startup duration, or Redis performance gain.

## Configuration

ASP.NET Core environment keys use `__` for nesting, for example `Oidc__PublicBaseUrl`. Compose variables such as `OIDC_PUBLIC_BASE_URL` are inputs to the Compose file, not application settings by themselves.

Adding a name to `.env` does not pass it into WebAuth unless the Compose file references it. Use a Compose override for additional application settings, or set them directly through your hosting platform.

The source production Compose file mounts `./certs` at `/https` read-only and uses named volumes for PostgreSQL and Redis. Protect `.env` and private keys with host access controls; do not commit them or share expanded Compose output.

## Environment Variables

These mappings apply to the source repository's production Compose file. The deployment repository and development stack may use different inputs.

| Compose input | Application setting or use |
| --- | --- |
| `POSTGRES_PASSWORD` | PostgreSQL initialization password and default `ConnectionStrings__authdb` password |
| `CONNECTION_STRING_AUTHDB` | Overrides `ConnectionStrings__authdb` for an external database |
| `OIDC_PUBLIC_BASE_URL` | `Oidc__PublicBaseUrl`; public service base URL, e.g. `https://auth.example.com` |
| `CERT_PASSWORD` | `ASPNETCORE_Kestrel__Certificates__Default__Password` |
| `ASPNETCORE_Kestrel__Certificates__Default__Path` | Container path to the HTTPS PFX, default `/https/aspnetapp.pfx` |
| `BOOTSTRAP_TOKEN` | `Bootstrap__Token`; remove after initialization |
| `DATAPROTECTION_CERTIFICATE_PATH` | `DataProtection__CertificatePath` |
| `DATAPROTECTION_CERTIFICATE_BASE64` | `DataProtection__CertificateBase64`; alternative to a mounted PFX |
| `DATAPROTECTION_CERTIFICATE_PASSWORD` | `DataProtection__CertificatePassword` |
| `DATAPROTECTION_ALLOW_UNENCRYPTED_KEY_RING` | Explicit risk opt-in, default `false` |
| `MAIL_ENABLED`, `MAIL_SMTP_HOST`, `MAIL_SMTP_PORT`, `MAIL_SMTP_USE_SSL` | Mail enablement, server, port, and TLS configuration |
| `MAIL_SMTP_USERNAME`, `MAIL_SMTP_PASSWORD` | `Mail__Username`, `Mail__Password` |
| `MAIL_FROM_ADDRESS`, `MAIL_FROM_NAME` | Sender identity |
| `LOGGING_LEVEL` | Default and Microsoft.AspNetCore log-level inputs |

For direct hosting, use the [application setting reference](production-setup-guide.md#environment-variables-reference). Do not assume that old `MULTITENANT_ENABLED`, `Seeder__AutoSeedEnabled`, or `Redis__Enabled` examples configure the current application.

### Reverse Proxy / Forwarded Headers (Optional)

Set the public base URL to the address clients use. Configure trusted proxy source addresses and an allowed public host:

```dotenv
FORWARDED_HEADERS_KNOWN_PROXY_0=10.0.0.10
FORWARDED_HEADERS_ALLOWED_HOST_0=auth.example.com
FORWARDED_HEADERS_ENFORCE_HOST_ALLOW_LIST=true
```

Replace the example proxy IP with the actual address. The source Compose file also supports `FORWARDED_HEADERS_KNOWN_NETWORK_0` for a trusted CIDR, `FORWARDED_HEADERS_FORWARD_LIMIT` for proxy depth, and `FORWARDED_HEADERS_REQUIRE_HEADER_SYMMETRY`.

Leave `FORWARDED_HEADERS_UNSAFE_TRUST_ALL=false` unless proxy addresses cannot be enumerated and network controls guarantee that clients cannot connect directly to WebAuth. A host allow-list alone does not make arbitrary forwarded client IP or scheme headers trustworthy.

Ensure the proxy overwrites client-supplied forwarding headers. Forwarded client certificates need separate trust configuration; see the [security guide](docker-security-best-practices.md).

## PostgreSQL Configuration

The Compose stack uses PostgreSQL 16 and a named `postgres-data` volume. `POSTGRES_PASSWORD` initializes an empty PostgreSQL data directory; changing it in `.env` does not change an existing database role's password.

WebAuth applies EF Core migrations at startup and stops if migration application fails. Applied migrations are tracked in the database. This does not guarantee zero downtime or compatibility with an older application image.

Before an upgrade, back up the database, test the migration against a restored copy, and plan application rollout and rollback together. See [upgrade-guide.md](upgrade-guide.md).

For an external database, set `CONNECTION_STRING_AUTHDB` and adjust the local PostgreSQL service dependency in your deployment configuration. Use the database provider's TLS and certificate verification requirements. Do not remove a local database service or volume until its data has been accounted for.

Useful checks:

```sh
docker compose ps postgres
docker compose logs --tail=100 postgres
docker compose exec postgres psql -U oidc -d authdb -c "SELECT version();"
```

## Redis Configuration (Optional)

WebAuth registers Redis when `ConnectionStrings__redis` is nonempty. The source production Compose file maps `REDIS_CONNECTION_STRING` to that key, with an empty default. `REDIS_ENABLED` is no longer used; remove it from existing environment files.

To connect to the included Redis service, set this value in `.env`:

```dotenv
REDIS_CONNECTION_STRING=redis:6379,abortConnect=false
```

If an existing `.env` has a nonempty `REDIS_CONNECTION_STRING`, this mapping now activates it even if the old `REDIS_ENABLED` value was `false`. Clear the connection string before deployment if you do not want WebAuth to connect to Redis. Remove any earlier workaround override that supplies `ConnectionStrings__redis` directly, or it will take precedence over the base file.

Apply the environment change:

```sh
docker compose config --quiet
docker compose up -d
docker compose exec redis redis-cli ping
```

The base source Compose file already includes the Redis service. Starting Redis and connecting WebAuth to it are separate steps. `PING` should return `PONG`; also check application logs and actual behavior.

`abortConnect=false` permits connection retry behavior; it does not guarantee that every Redis-dependent operation falls back to memory. Test outage and restart behavior for your workload before relying on it. Multi-instance deployments also need to verify which state is shared and which remains process-local.

Monitor memory, evictions, and persistence errors:

```sh
docker compose logs --tail=100 redis
docker compose exec redis redis-cli INFO memory
docker compose exec redis redis-cli INFO stats
```

The source configuration uses RDB snapshots (`--save 60 1`). Choose persistence and eviction policy based on the state stored by your enabled features. Do not use `FLUSHALL` or delete volumes as a generic cache repair. Redis command tracing can expose sensitive values and should not be left enabled.

To disconnect WebAuth from Redis, clear `REDIS_CONNECTION_STRING` and run `docker compose up -d` to recreate WebAuth with the updated environment. Check for overrides that set `ConnectionStrings__redis` directly. Test the resulting behavior before stopping Redis; the Redis container is still part of the base stack.

## TLS Certificates

Clients must reach the production issuer over trusted HTTPS. Either terminate TLS at WebAuth or use a trusted reverse proxy with restricted backend access.

For direct HTTPS, mount a PFX containing the certificate chain and private key, set its container path and password, and ensure the container user can read it. Use a certificate from a public CA or an internal CA trusted by all clients. Plan renewal and service reloads; issuing a certificate once does not configure renewal.

The source Compose file expects `/https/aspnetapp.pfx` by default. If you terminate TLS at a proxy and use HTTP internally, explicitly adjust the listener, certificate settings, exposed ports, health check, and proxy trust together. Merely adding a proxy does not remove the Compose file's HTTPS listener.

Local development uses [certs/README.md](../certs/README.md). The setup scripts export a PFX and attempt local trust; confirmation or OS-specific steps may be needed. Do not use `changeit` or `curl -k` in a production procedure.

### DataProtection Key-Ring Encryption

The DataProtection key ring is stored in PostgreSQL. Production startup requires a PFX to encrypt it at rest unless you explicitly opt into unencrypted storage. This certificate has a different purpose from the public HTTPS certificate.

For a mounted certificate:

```dotenv
DATAPROTECTION_CERTIFICATE_PATH=/https/dataprotection.pfx
DATAPROTECTION_CERTIFICATE_PASSWORD=<certificate-secret>
```

Alternatively, use `DATAPROTECTION_CERTIFICATE_BASE64` where the host accepts only text secrets. Base64 is not encryption; protect it as private-key material. Keep the password separately protected.

Preserve the database key ring and the certificates required to decrypt it across restarts, replicas, and recovery. A database backup without the required private keys is not a complete recovery set. See [production setup](production-setup-guide.md#dataprotection-key-ring-error-on-startup) for certificate provisioning.

## Deployment Scenarios

- **Local development:** use the source development Compose file and its `DEV_*` settings. Rebuild after source changes; it is not a hot-reload setup.
- **Published image:** follow the deployment repository and pin the chosen image tag or digest for a reproducible rollout.
- **Source-built production:** keep the full checkout, record its commit, and build from it. Do not substitute the seeded development stack.
- **Multiple tenants or replicas:** review tenant configuration, shared state, proxy routing, keys, and failure behavior. Redis alone is not a complete scale-out configuration.

The [Compose examples](docker-compose-examples.md) provide additional layouts. Compare their setting names with the current application reference and your selected Compose file before using them.

## Troubleshooting

| Symptom | First checks |
| --- | --- |
| WebAuth exits during startup | Read logs for migration, database connection, certificate, or production-secret validation errors |
| PostgreSQL password mismatch | Compare the application credential with the existing database role; recreating a container does not rotate the role password |
| Certificate access denied | Check the mounted path, container user, and host permissions; do not make production private keys world-readable |
| Redirect loop or wrong issuer | Check the public base URL, trusted proxies, forwarded scheme, and listener/redirection configuration |
| Tenant discovery returns 404 | Confirm the tenant slug and whether first-run bootstrap completed |
| Bootstrap returns 404 / 401 / 409 | Token not configured / token mismatch / an existing tenant; see [bootstrap troubleshooting](production-setup-guide.md#troubleshooting) |
| Redis volume permission error | Inspect that mount's ownership and service logs; `docker compose down -v` would also remove database volumes |
| Port already allocated | Identify the owning process before stopping it; update client URLs and issuer configuration if you change public bindings |
| Cookies fail after redeployment | Check key-ring persistence, application name, and decryption certificates before treating this as a browser problem |

Use service names, not assumed container names:

```sh
docker compose ps
docker compose logs --tail=100 webauth
docker compose logs --tail=100 postgres
```

Redact passwords, tokens, connection strings, and personal data before sharing logs. Do not assume tools such as `ping`, `nc`, or `netstat` are installed in the application image.

## Security Best Practices

Before exposing the service:

- Use unique generated secrets and keep them out of source control, command history, and shared diagnostics.
- Restrict backend, database, and Redis access. Publish only the ports needed by clients or your proxy.
- Configure trusted HTTPS and forwarding rules; verify discovery reports the intended issuer and endpoints.
- Keep the DataProtection key ring encrypted and retain recovery copies of the required certificates.
- Disable the bootstrap token after initialization and avoid development seed/test settings.
- Test mail delivery if users must confirm email or recover accounts.
- Review image updates and migrations in a test environment before rollout.
- Verify container identity, mount permissions, resource limits, and log retention in the actual deployed configuration.

For additional controls, see [docker-security-best-practices.md](docker-security-best-practices.md).

## Monitoring and Logging

Monitor application health together with tenant discovery and a representative authentication flow. A responding discovery endpoint alone does not verify database writes, email delivery, or every optional dependency.

```sh
curl --fail --show-error https://auth.example.com/health
curl --fail --show-error https://auth.example.com/t/default/.well-known/openid-configuration
docker compose logs --tail=100 webauth
docker stats --no-stream
```

Retain enough logs to investigate failed logins, startup failures, and migrations without enabling verbose diagnostics indefinitely. The application uses structured logging and OpenTelemetry; configure collectors and exporters for your environment rather than assuming a monitoring container is already wired up.

## Backup and Recovery

Back up PostgreSQL, deployment configuration, image/commit identifiers, and the private keys and passwords needed to decrypt protected data. Store backup secrets separately with appropriate access controls. Set retention and recovery objectives from business requirements, then measure them in restore tests.

For the source Compose database, this Bash example creates a custom-format dump without relying on a generated container name:

```bash
mkdir -p backups
backup_file="backups/authdb-$(date +%Y%m%d-%H%M%S).dump"
docker compose exec -T postgres pg_dump -U oidc -d authdb -Fc > "$backup_file"
```

Check the command's exit status before accepting the backup. A created file is not proof that `pg_dump` succeeded. These binary-redirection commands are Bash examples; use a binary-safe backup workflow on Windows rather than assuming all PowerShell versions handle native output identically.

Restore into an isolated, empty database first. Do not run this against a live production database or a database containing data you intend to retain:

```bash
docker compose exec -T postgres pg_restore -U oidc -d authdb --exit-on-error < "$backup_file"
```

Confirm that this Compose project points to the intended recovery database, with no application instances writing to it. Use compatible PostgreSQL tools, restore the matching configuration and DataProtection material, and start the application version associated with the backup before attempting upgrades.

Verify login, tenant/client records, discovery, key availability, and an application token flow. Check logs for decryption and migration failures. Plan the production cutover only after the restore test succeeds; do not add database drops or volume deletion as an automatic retry step.

For upgrade-specific recovery, see [upgrade-guide.md](upgrade-guide.md). For ongoing exercises, see [backup verification](for-operators/backup-restore/verification-testing.md).

## Support

- [Documentation index](index.md)
- [GitHub issues](https://github.com/popicka70/MrWhoOidc/issues)

**Last reviewed:** 2026-09-05. Configuration was checked against the source files; this review did not perform a production deployment or restore exercise.
