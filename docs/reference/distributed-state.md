# Distributed state store

The host keeps a small amount of short-lived state that every replica must share:

- DPoP proof replay cache and DPoP nonce store
- JAR (request object) replay cache and client-assertion replay cache
- login, token-exchange and HTTP rate-limit counters
- correlation handles
- `IDistributedCache` consumers: sessions, login continuations, interaction markers and the HybridCache L2 tier

`IDistributedStateStore` is the single abstraction for this state. Three implementations exist:

| Provider | Use when | Notes |
| --- | --- | --- |
| `Database` (default) | Any deployment with the auth database | Uses the `DistributedStateEntries` table in `AuthDbContext`; atomic upserts/counters work across replicas. |
| `Redis` | You already operate Redis and want it as the shared backend | Opt-in; requires `ConnectionStrings:redis`. |
| `Memory` | Tests and single-process development | Per-process only. Never use with more than one replica. |

## Configuration

```json
{
  "DistributedState": {
    "Provider": "Database",
    "CleanupIntervalMinutes": 5,
    "DefaultEntryTtlMinutes": 20
  }
}
```

Environment variable form: `DistributedState__Provider=Redis`.

`Provider` accepts `Database`/`Db` (default when unset), `Redis`, and `Memory`/`InMemory`.
Selecting `Redis` without a Redis connection string fails startup instead of silently falling back.

When the database provider is selected, the cleanup worker deletes expired rows every
`CleanupIntervalMinutes` (minimum 1 minute). Redis expires keys natively; the memory provider
removes expired entries when they are read.

## Multi-replica safety

The database and Redis providers are safe with multiple replicas. The memory provider is not: replay
caches, rate limits and login state would be per-process. Set `Deployment:MultiInstance=true` to make
startup fail when the memory provider is selected.

## Argon2 concurrency

Argon2id operations allocate 128 MiB each. A shared gate bounds concurrent password and client-secret
hashing/verification. By default the limit is derived from the smaller of the CPU cap (4) and the
container memory budget (40% of the cgroup/GC limit). Override it with:

- `Auth:Argon2MaxConcurrentVerifications`
- `MRWHOIDC_ARGON2_MAX_CONCURRENT`

Verification fails closed when no slot becomes available; hashing throws rather than storing an empty
hash.
