# Monitoring and Alert Configuration

WebAuth emits structured logs and OpenTelemetry metrics. This guide describes how to build monitoring around them; it does not install Prometheus rules, exporters, dashboards, or an on-call schedule.

## Application Signals

The instrument names below come from [OidcEndpointMetrics](../../../MrWhoOidc.WebAuth/Observability/OidcEndpointMetrics.cs). Exporters can rename instruments and change units. Inspect collected samples before writing queries; these names are not copy-ready PromQL expressions.

| Instruments | What to investigate |
| --- | --- |
| `oidc.token.requests`, `oidc.token.success`, `oidc.token.failures` | Token endpoint traffic and outcomes; distinguish expected protocol rejections from service failures |
| `oidc.token.duration.ms` | Token endpoint latency; correlate with password hashing, storage, and upstream dependencies |
| `oidc.token_exchange.requests`, `oidc.token_exchange.success`, `oidc.token_exchange.failures` | Exchange failures, client policy, subject token validity, and DPoP binding |
| `oidc.authorize.requests`, `oidc.authorize.duration.ms` | Authorization request volume and latency |
| `oidc.bcl.delivery.ms` | Back-channel logout delivery latency; inspect dispatcher errors and queued notifications as well |

Also review [client secret metrics](../../../MrWhoOidc.Auth/Observability/ClientSecretMetrics.cs) and [support access metrics](../../../MrWhoOidc.WebAuth/Observability/TenantSupportAccessMetrics.cs) when configuring those operational workflows.

Metric registration, collection, export, and alert evaluation are separate steps. Verify the meters enabled by your deployment and the exporter/collector pipeline. Do not assume `/metrics` is a configured Prometheus scrape endpoint merely because the application uses OpenTelemetry.

## External and Infrastructure Checks

| Check | Source and required setup | Initial response |
| --- | --- | --- |
| HTTPS availability and certificate expiry | External probe against the public host, with TLS verification enabled | Check proxy, certificate chain, routing, and container status |
| Application health and tenant discovery | Probe `/health` and `/t/<slug>/.well-known/openid-configuration` | Compare health output with startup and database logs |
| Representative login/token flow | Dedicated test client and account with controlled permissions | Identify the failing step; discovery alone does not prove login works |
| Database connections, storage, and backup age | PostgreSQL/hosting monitoring plus backup-job results | Inspect connection limits, disk growth, and latest successful restore test |
| Container restarts and resource pressure | Container runtime or hosting provider metrics | Inspect exit reason and previous logs before restarting |
| Redis availability, memory, evictions, persistence | Redis monitoring when WebAuth is configured to use it | Check connectivity and affected features; do not assume automatic memory fallback |

Replication lag applies only when replication is actually configured. A container's last-seen timestamp is not a restart counter. Alert on absence of expected telemetry as well as on unhealthy values.

## Define and Test Each Alert

1. Assign an owner, destination, escalation route, and maintenance policy.
2. Observe a representative traffic baseline. Choose thresholds and evaluation windows from service objectives, not from generic percentages in a template.
3. For error ratios, define the failure population and a minimum request volume. A quiet service with one expected rejection should not produce the same alarm as a sustained outage.
4. Verify labels, units, histogram boundaries, aggregation across replicas, and missing-data behavior in the exported samples.
5. Trigger the condition in an isolated test environment, confirm notification delivery, and test recovery notification and suppression.
6. Record the tested query, expected response, owner, and test date in the deployment's monitoring configuration.

Do not include access tokens, client secrets, raw JWTs, or personal identifiers in alert payloads. Restrict access to logs and traces.

## First Checks

These commands use the source production Compose service names. Replace the example host and tenant slug. They inspect state without restarting services:

```sh
docker compose ps
docker compose logs --tail=100 webauth
docker compose logs --tail=100 postgres
curl --fail --show-error https://auth.example.com/health
curl --fail --show-error https://auth.example.com/t/default/.well-known/openid-configuration
```

Use [deployment troubleshooting](../../deployment-guide.md#troubleshooting) for startup and configuration errors, [incident response](../../for-security-teams/incident-response.md) for suspected compromise, and [backup verification](../backup-restore/verification-testing.md) for recovery exercises.

## Verification Boundary

Reviewed against source instrument definitions on 2026-09-05. No production alert pipeline or notification delivery was exercised during this documentation review. Operators must validate their own exporters, rules, thresholds, and contacts.
