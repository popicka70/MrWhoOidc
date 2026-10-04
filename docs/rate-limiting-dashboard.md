# Rate Limiting Dashboard

## Overview

MrWhoOidc enforces rate limits with ASP.NET Core's rate limiter (named policies on each endpoint, plus a global per-IP limiter) and, when Redis is configured, a distributed limiter for the OAuth endpoints. There is **no rate-limit dashboard**: the former `/admin/rate-limits` page and `/admin/api/rate-limits/metrics` only ever showed placeholder data and were removed. Use the OpenTelemetry metrics (below) for real numbers.

## Admin API

`GET /admin/api/rate-limits/overview`, `/events` and `/client/{clientId}` (also under `/t/{slug}/admin/api` and `/platform-admin/api`) still exist because `mrwho-cli rate-limits overview|events|client` calls them, but they answer **HTTP 501 Not Implemented** with a ProblemDetails message instead of fake data.

## Enforced Policies

Defined in `MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/RateLimitingExtensions.cs` (hard-coded, not configurable). Rejections return HTTP 429. All windows are 1 minute.

| Policy | Limit | Partition |
| --- | --- | --- |
| Global limiter | 1000 (token bucket) | client IP |
| `rl-authorize` | 60 | IP |
| `rl-userinfo` | 120 | IP |
| `rl-par` | 60 | hash(tenant, client_id, IP); client_id only from the Basic header or the form value stashed by the distributed limiter |
| `rl-introspect` | 60 | IP |
| `rl-jwks` | 300 | IP |
| `rl-admin` | 200 | IP |
| `rl-logout`, `rl-revoke`, `rl-external` | 30 | IP |
| `rl-qr-poll` | 60 (sliding) | QR session token |
| `rl-qr-confirm` / `rl-qr-cancel` | 5 / 10 | IP |
| `email-discovery` | 5 (POST only) | IP |

These limiters are per replica. IP partitioning uses the connection's remote address, so configure forwarded headers correctly behind a proxy.

## Distributed Limiter (Redis)

When `ConnectionStrings:redis` is set, `DistributedRateLimiterMiddleware` additionally limits `/token` (100/min, token exchange 40/min), `/introspect` (80/min), `/par` and `/revoke` (60/min) across replicas, also for tenant-prefixed paths. The client_id is not authenticated at that point, so the partition key is a hash of tenant, client_id and caller IP: another caller cannot exhaust a client's budget by sending its client_id. Redis errors fail open for these counters.

## Token Exchange Limiter

Token exchange additionally has a per-client limiter keyed by tenant and client (in-memory, or Redis when `ConnectionStrings:redis` is set):

```json
{
  "TokenExchangeRateLimit": {
    "Enabled": true,
    "PerClientPerMinute": 60
  }
}
```

## Metrics

Real counters are emitted through OpenTelemetry, e.g. `oidc.token_exchange.ratelimit.allowed` and `oidc.token_exchange.ratelimit.blocked`. Scrape them through your OpenTelemetry/Prometheus exporter.
