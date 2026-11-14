# Inbound JAR replay cache

This server validates JWT-secured authorization requests (JAR, RFC 9101) and protects against replays using a bounded-time cache. In production, the replay cache must be shared by all replicas: use the database provider (default) or Redis.

## What is cached
- Replay key: iss (client_id) + aud (/authorize) + jti (preferred) or nonce.
- Expiration: taken from exp (+ clock skew). If exp is missing, `RequestObjectReplayTtlSeconds` is used (minimum 60s).
- A request object with neither `jti` nor `nonce` is not replay-tracked; clients should always send a unique `jti`.

## How to select the backend
- Default: `DistributedState:Provider=Database` stores the cache in the `DistributedStateEntries` table (see [distributed-state.md](distributed-state.md)).
- Redis: set `DistributedState:Provider=Redis` and provide a connection string named `redis`:
  - appsettings.Production.json:
    `{ "ConnectionStrings": { "redis": "localhost:6379,ssl=false" } }`
  - or environment: `ConnectionStrings__redis=localhost:6379,ssl=false`
- `Memory`/`InMemory` is only suitable for tests and single-replica development; it is per-process.
- A Redis connection string alone no longer switches the backend. The provider setting is explicit so a
  missing Redis instance cannot silently downgrade replay protection.

The store-backed cache uses keys `jar:replay:*` and a set-if-absent operation with TTL. If the store is
unavailable the request is rejected (fail closed) rather than accepted without replay tracking.

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
