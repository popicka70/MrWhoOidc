# Rate Limiting Dashboard

## Overview

MrWhoOidc enforces rate limits with ASP.NET Core's rate limiter (named policies on each endpoint, plus a global per-IP limiter). An admin dashboard and admin API exist for inspecting them, but **the dashboard data is currently placeholder**: the overview lists the four main policies with zero counts, the client and events endpoints return empty data, and the metrics export returns zeros. Use the OpenTelemetry metrics (below) for real numbers.

## Dashboard

`/admin/rate-limits` (Razor page, `admin` authorization policy). It reloads every 30 seconds; the Refresh button reloads manually.

## Admin API

Mapped under `/admin/api`, `/t/{slug}/admin/api` (both `tenant-admin` policy) and `/platform-admin/api` (`platform-admin` policy), all with the `rl-admin` limiter. Platform admins may pass `?tenantId=` to the overview; tenant admins are restricted to their own tenant. The CLI wraps these as `mrwho-cli rate-limits overview|events|client`.

| Endpoint | Current behaviour |
| --- | --- |
| `GET /admin/api/rate-limits/overview` | Static list of `Token Exchange`, `Token`, `Authorize`, `UserInfo` with `currentRequests: 0` and no limits. |
| `GET /admin/api/rate-limits/client/{clientId}` | Placeholder DTO with no usage. |
| `GET /admin/api/rate-limits/events?page=&pageSize=&clientFilter=` | Empty list. `page` and `pageSize` must be positive; `pageSize` max 100 (default 50). |
| `GET /admin/api/rate-limits/metrics` | JSON with zero values; not a Prometheus exposition endpoint. |

## Enforced Policies

Defined in `MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/RateLimitingExtensions.cs` (hard-coded, not configurable). Rejections return HTTP 429. All windows are 1 minute.

| Policy | Limit | Partition |
| --- | --- | --- |
| Global limiter | 1000 (token bucket) | client IP |
| `rl-authorize` | 60 | IP |
| `rl-token` | 30 | client_id, else IP |
| `rl-token-exchange` | 60 | client_id, else IP |
| `rl-userinfo` | 120 | IP |
| `rl-par` | 60 | client_id, else IP (Redis-backed when Redis is configured) |
| `rl-introspect` | 60 | IP |
| `rl-jwks` | 300 | IP |
| `rl-admin` | 200 | IP |
| `rl-logout`, `rl-revoke`, `rl-external` | 30 | IP |
| `rl-qr-poll` | 60 (sliding) | QR session token |
| `rl-qr-confirm` / `rl-qr-cancel` | 5 / 10 | IP |
| `email-discovery` | 5 (POST only) | IP |

Apart from `rl-par`, these limiters are per replica. IP partitioning uses the connection's remote address, so configure forwarded headers correctly behind a proxy.

## Token Exchange Limiter

Token exchange additionally has a per-client limiter (in-memory, or Redis when `ConnectionStrings:redis` is set):

```json
{
  "TokenExchangeRateLimit": {
    "Enabled": true,
    "PerClientPerMinute": 60
  }
}
```

## Metrics

Real counters are emitted through OpenTelemetry, e.g. `oidc.token_exchange.ratelimit.allowed` and `oidc.token_exchange.ratelimit.blocked`. Scrape them through your OpenTelemetry/Prometheus exporter, not through `/admin/api/rate-limits/metrics`.
