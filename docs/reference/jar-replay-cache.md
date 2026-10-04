# Inbound JAR replay cache (Redis)

This server validates JWT-secured authorization requests (JAR, RFC 9101) and protects against replays using a bounded-time cache. In production, use Redis as a distributed replay cache.

## What is cached
- Replay key: iss (client_id) + aud (/authorize) + jti (preferred) or nonce.
- Expiration: taken from exp (+ clock skew). If exp is missing, `RequestObjectReplayTtlSeconds` is used (minimum 60s).
- A request object with neither `jti` nor `nonce` is not replay-tracked; clients should always send a unique `jti`.

## How to enable Redis
- Configure a connection string named `redis`:
  - appsettings.Production.json:  
    `{ "ConnectionStrings": { "redis": "localhost:6379,ssl=false" } }`
  - or environment: `ConnectionStrings__redis=localhost:6379,ssl=false`
- When present, the app uses `RedisJarReplayCache` (keys `jar:replay:*`, `SET NX` with TTL) automatically. Without Redis, it falls back to an in-memory cache, which is per-replica and therefore only suitable for single-instance/dev deployments.

## AuthOptions knobs (appsettings)
```json
{
  "Auth": {
    "RequestObjectClockSkewSeconds": 120,
    "RequestObjectReplayTtlSeconds": 300,
    "RequestObjectMaxLifetimeSeconds": 300,
    "RequestObjectAllowedAlgorithms": ["RS256", "PS256", "ES256", "ES384", "ES512"],
    "RequestObjectAllowedAlgorithmsPerClient": { "some-client": ["ES256"] }
  }
}
```

## Recommended values
- Clock skew: 60–120s (default 120) to absorb small clock drift.
- Replay TTL: 300s when exp is not present. If clients always include exp, prefer validating via exp; TTL is a fallback.
- Max lifetime: 300s (5 minutes) to bound request validity window.

## Discovery alignment
- `.well-known/openid-configuration` emits `request_object_signing_alg_values_supported` from `Auth:RequestObjectAllowedAlgorithms` so advertised capabilities match enforcement.

## Testing
- `MrWhoOidc.UnitTests/RequestObjectValidatorTests.cs` covers replay rejection via jti, acceptance at the skew boundary, rejection beyond skew, client JWK/JWKS URI keys and encrypted nested request objects. These tests use the in-memory cache.
