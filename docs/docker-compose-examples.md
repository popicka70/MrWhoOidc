# Docker Compose Variants

Use the repository's Compose files as the baseline, not a copied full-stack example. These variants apply to [the source repository's production Compose](../docker-compose.yml); the separate published-image repository can have different service names and mappings. Begin with the [deployment guide](deployment-guide.md) and complete [production setup](production-setup-guide.md) before starting a production instance.

Commands below run from the repository root in PowerShell. They validate configuration without starting services. Configuration output can contain secrets: use `config --quiet` for routine validation and do not attach rendered configuration to public tickets.

## Choose a Baseline

- Source development: `docker-compose.dev.yml`, Development environment, explicit auto-seeding, local certificates, and MailHog. Follow the [quickstart](for-developers/quickstart-15-min.md).
- Production-oriented source stack: `docker-compose.yml`, Production by default, explicit bootstrap, production TLS and DataProtection requirements. It contains both `build` and `image`; choose deliberately whether to build source or pull a pinned image.
- Published deployment: use the Compose supplied with that release and inspect its actual mappings. Do not mix its environment file with the source development stack.

```powershell
docker compose -f docker-compose.yml config --quiet
docker compose -f docker-compose.dev.yml config --quiet
```

Validation checks interpolation and structure, not certificate trust, network reachability, database credentials, migrations, or application readiness. Use the same explicit `-f` files and project context for validation, startup, inspection, and upgrades.

## Redis Connection

The base Compose maps `REDIS_CONNECTION_STRING` into `ConnectionStrings__redis`. Leave it empty to omit the application's Redis connection, or use the internal service:

```dotenv
REDIS_CONNECTION_STRING=redis:6379,abortConnect=false
```

For external Redis, supply its approved connection string, credentials, and TLS settings through protected configuration. An arbitrary `REDIS_ENABLED` flag has no effect on this mapping. An empty connection string does not remove the included Redis container, and a connection option is not proof of seamless cache failover. See [hybrid caching](hybrid-cache-guide.md) for application context.

## SMTP

Use the existing mappings rather than introducing `Mail__SmtpUsername` or `Mail__SmtpPassword`, which are not the configured credential properties:

```dotenv
MAIL_ENABLED=true
MAIL_SMTP_HOST=smtp.example.com
MAIL_SMTP_PORT=587
MAIL_SMTP_USE_SSL=true
MAIL_FROM_ADDRESS=no-reply@example.com
MAIL_FROM_NAME=Example Identity
```

Supply `MAIL_SMTP_USERNAME` and `MAIL_SMTP_PASSWORD` securely when the relay requires authentication. They map to `Mail__Username` and `Mail__Password`. Verify the relay's supported TLS/authentication mode with the actual mail transport; a port number alone does not establish TLS compatibility. Send a controlled test and check receipt before enabling production recovery mail.

For captured local mail, use [MailHog development setup](mailhog-local-dev.md), not these production relay settings.

## Alternate Certificate Filename

The baseline mounts `./certs` read-only at `/https`. A certificate already placed in that directory can be selected using the existing mapping:

```dotenv
ASPNETCORE_Kestrel__Certificates__Default__Path=/https/production.pfx
```

Provide `CERT_PASSWORD` securely and ensure the PFX includes the required private key and chain. Do not place a certificate elsewhere and assume changing only its container path makes it available. Use [certificate guidance](deployment-guide.md#tls-certificates) for trust and conversion; never put a production private key into an online conversion tool.

Configure DataProtection separately. For example, `DATAPROTECTION_CERTIFICATE_PATH=/https/dataprotection.pfx` requires that certificate to be mounted there and its password supplied through `DATAPROTECTION_CERTIFICATE_PASSWORD`. Retain older decryption material needed by existing data and backups.

## External Database and Reverse Proxy

`CONNECTION_STRING_AUTHDB` overrides the application's database connection. It does not remove the local PostgreSQL service or its required `POSTGRES_PASSWORD` interpolation. Treat a managed-database deployment as a reviewed Compose adaptation, including network access, TLS validation, backup ownership, and removal of obsolete dependencies; do not merely point production at a new host and run migrations without a recovery plan.

For a reverse proxy, configure the public issuer and trusted proxy addresses/networks using the existing `FORWARDED_HEADERS_*` mappings. Restrict direct container access. An allowed-host list alone does not establish trusted forwarded IP or scheme headers. See [production setup](production-setup-guide.md) for the trust boundary and TLS requirements.

## Verification and Maintenance

After an approved startup, inspect container health and application logs, then verify discovery through the public HTTPS endpoint with normal certificate validation. Follow with an actual client login/token flow. A successful Compose render or internal health check is not a production acceptance test.

- [Upgrade and rollback](upgrade-guide.md)
- [Backup and isolated restore verification](for-operators/backup-restore/verification-testing.md)
- [Monitoring configuration](for-operators/monitoring/alerting-rules.md)

Reviewed against the source Compose mappings on 2026-09-05. No production deployment was started during this documentation review.
