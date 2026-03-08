# Rate Limiting Dashboard

## Overview

The Rate Limiting Dashboard provides real-time monitoring of rate limiting across all OIDC endpoints in MrWhoOidc. It helps administrators identify abuse patterns, diagnose performance issues, and tune rate limit configurations.

## Features

- **Policy Status**: View all active rate limiting policies with request counts
- **Request Metrics**: Track allowed vs blocked requests over 24-hour periods  
- **Recent Events**: See the latest rate limit events with timestamps and client information
- **Block Rate Analysis**: Visual indicators showing what percentage of requests are being blocked

## Accessing the Dashboard

Navigate to `/admin/rate-limits` after logging in as an admin user. The dashboard requires `admin` authorization.

## API Endpoints

### GET /admin/api/rate-limits/overview
Returns current status of all rate limiting policies.

**Response:**
```json
{
  "activePolicies": [
    {
      "policyName": "Token Exchange",
      "isEnabled": true,
      "currentRequests": 1250,
      "maxRequests": null,
      "timeWindow": null
    }
  ],
  "totalBlockedRequests24H": 125,
  "totalAllowedRequests24H": 1250,
  "snapshotTime": "2026-03-08T18:30:00Z"
}
```

### GET /admin/api/rate-limits/client/{clientId}
Returns detailed rate limit usage for a specific client.

### GET /admin/api/rate-limits/events
Returns recent rate limiting events with pagination support.

**Query Parameters:**
- `page`: Page number (default: 1, max: 100)
- `pageSize`: Items per page (default: 50, max: 100)
- `clientFilter`: Filter by client ID
- `policyNameFilter`: Filter by policy name

### GET /admin/api/rate-limits/metrics
Exports metrics in JSON format for Grafana/Prometheus integration.

## Rate Limiting Policies

The following policies are monitored:

| Policy Name | Endpoint | Max Requests/Window | Description |
|-------------|----------|---------------------|-------------|
| Token Exchange | /token (grant_type=urn:ietf:params:oauth:grant-type:token-exchange) | 60/min | RFC 8693 token exchange operations |
| Token | /token | 30/min | Standard OAuth2/OIDC token requests |
| Authorize | /authorize | 60/min | Authorization endpoint |
| UserInfo | /userinfo | 120/min | User info endpoint |

## Metrics Export

The dashboard exports metrics in a format compatible with OpenTelemetry exporters. To integrate with Prometheus:

```yaml
# prometheus.yml
scrape_configs:
  - job_name: 'mrwhooidc'
    static_configs:
      - targets: ['localhost:8080']
    metrics_path: '/admin/api/rate-limits/metrics'
```

## Auto-refresh

The dashboard automatically refreshes every 30 seconds. Click the "Refresh" button to manually update or pause auto-refresh.

## Configuration

Rate limit policies are configured via `appsettings.json`:

```json
{
  "TokenExchangeRateLimit": {
    "PermitLimit": 60,
    "Window": "00:01:00",
    "EnableRedis": true
  }
}
```

## Future Enhancements

- [ ] Real-time WebSocket updates for live event streaming
- [ ] Export rate limit events to CSV/Parquet
- [ ] Historical trend analysis charts
- [ ] Per-client rate limit configuration UI
- [ ] Automated alerting when block rates exceed thresholds
