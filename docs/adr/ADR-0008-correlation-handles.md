# ADR-0008: Correlation ID Propagation with `cid_ref` Handles

Status: Accepted 2025-09-29

## Context

External OIDC flows span multiple hops: `/authorize` (local), browser redirects to upstream IdPs, callbacks, local token issuance, and occasionally admin API calls triggered from the same troubleshooting flow. We needed a reliable, privacy-safe way to:

- Accept a caller-provided correlation identifier when available (for SOC/SRE workflows).
- Reuse the same identifier across the front-channel browser redirect without exposing the raw value to intermediaries.
- Support short-lived flow state (PAR/JAR, PKCE) without bloating URLs.
- Emit consistent metrics so we can detect cache misses, stale handles, or missing headers.

## Decision

Adopt a two-part correlation model:

1. **Header (`X-Correlation-Id`)** – if a well-formed token (<= 64 chars, `[A-Za-z0-9-_]`) is supplied, we use it; otherwise we generate a 128-bit Base32 Crockford ID. The value is echoed on the response header and added to the logging scope.
2. **State handle (`cid_ref`)** – we never expose the raw correlation ID on the front channel. Instead we generate a 96-bit random handle, store the mapping (`handle → CID`) in an in-memory cache (with optional Redis backing, TTL 10 minutes), and embed only the handle inside the protected `state` payload (and appended to return URLs).

On callback we resolve the handle back to the CID, reapply the logging scope, and refresh the cache TTL. If the handle is missing or stale, we mint a fresh CID, log a warning with a hashed handle fingerprint, and return a friendly error prompting the user to restart the flow.

## Consequences

- Admin and API callers can thread the same correlation value through retries and support tickets.
- Browser flows never expose the CID directly; leaked state only leaks an opaque handle.
- Metrics (`oidc.correlation.cache.writes`, `.hits`, `.misses`, `.stale`) give insight into cache health.
- Requests missing the header still work; middleware emits a warning for admin APIs to encourage adoption.
- Redis is optional; when unavailable we rely on the in-process cache with the same TTL.

## Implementation Notes

- `CorrelationTrackingMiddleware` validates the header, generates new IDs for `/authorize`, and attaches the resulting CID to the `HttpContext` and response header.
- `CorrelationStateCache` persists handle→CID mappings in `IMemoryCache` and (optionally) Redis. TTL is 10 minutes.
- `ExternalOidcHandler` injects `cid_ref` into the state payload and resolves it on callback; stale handles trigger a friendly error and metric increment.
- Admin pipeline uses `AdminCorrelationMiddleware` to warn when clients forget to send `X-Correlation-Id`.
- Metrics are emitted via `OidcMetrics` counters (`oidc.correlation.cache.*`).

## Future Work

- Optionally map the CID into the W3C trace context (Activity TraceId / baggage) for downstream exporters.
- Document recommended Redis sizing and eviction metrics once production telemetry is collected.
- Decide whether additional services (sample API) should forward the correlation header automatically.
