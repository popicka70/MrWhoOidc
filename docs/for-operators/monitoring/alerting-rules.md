# Monitoring and Alert Configuration

WebAuth emits structured logs and OpenTelemetry metrics. Metrics, traces, and logs are exported over OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; WebAuth exposes no Prometheus `/metrics` endpoint. Exported meters are ASP.NET Core, HttpClient, and runtime instrumentation plus the `MrWhoOidc.WebAuth` and licensing meters. This guide does not install exporters, dashboards, or an on-call schedule; [prometheus-rules.yml](prometheus-rules.yml) is a starting template.

## Application Signals

The instrument names below come from [OidcEndpointMetrics](../../../MrWhoOidc.WebAuth/Observability/OidcEndpointMetrics.cs). Exporters can rename instruments and change units. Inspect collected samples before writing queries; these names are not copy-ready PromQL expressions.

| Instruments | What to investigate |
| --- | --- |
| `oidc.token.requests`, `oidc.token.success`, `oidc.token.failures` | Token endpoint traffic and outcomes; distinguish expected protocol rejections from service failures |
| `oidc.token.duration.ms` | Token endpoint latency; correlate with password hashing, storage, and upstream dependencies |
| `oidc.token_exchange.requests`, `oidc.token_exchange.success`, `oidc.token_exchange.failures` | Exchange failures, client policy, subject token validity, and DPoP binding |
| `oidc.authorize.requests`, `oidc.authorize.duration.ms` | Authorization request volume and latency |
| `oidc.introspection.requests`, `oidc.introspection.active_false` | Introspection is deny-by-default; a jump in `active_false` after an upgrade may mean a resource server lacks `IntrospectionAudiencesJson` or `Auth:IntrospectionPermissions`; denied calls are logged by the introspection auditor with outcome `forbidden` |
| `oidc.bcl.backlog`, `oidc.bcl.failed`, `oidc.bcl.delivery.ms` | Back-channel logout queue (all tenants), failures, and delivery latency; `/health/backchannel` reports the same backlog |

[Support access metrics](../../../MrWhoOidc.WebAuth/Observability/TenantSupportAccessMetrics.cs) (`tenant_support_access.*`) share the `MrWhoOidc.WebAuth` meter. [Client secret metrics](../../../MrWhoOidc.Auth/Observability/ClientSecretMetrics.cs) use the `MrWhoOidc.Auth.ClientSecrets` meter, which is not registered with the exporter; poll `/health/client-secrets` for expiry instead.

When an OpenTelemetry Collector converts these to Prometheus, dots become underscores, counters gain `_total`, and units may add suffixes. Inspect collected samples before writing queries.

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
