# ADR-0009: JWKS Endpoint Design for Provider Key Publication

**Status**: Accepted  
**Date**: 2025-10-14  
**Decision Makers**: Engineering Team  
**Related**: [ADR-0007: Key Management](./adr-0007-key-management.md), [ADR-0008: Correlation Tracking](./adr-0008-correlation-tracking.md)

---

## Context

MrWhoOidc implements outbound JAR (JWT-secured Authorization Requests, RFC 9101) when federating to upstream identity providers. This requires signing authorization requests with a provider-specific signing key. Upstream IdPs need a trustworthy mechanism to obtain our public keys for JWT signature verification.

### Problem Statement

**How should MrWhoOidc expose provider signing keys to upstream IdPs?**

Constraints:
1. **Zero manual coordination**: Avoid email/ticket-based key exchanges
2. **Seamless rotation**: Support key rollover without service interruption
3. **Security**: Prevent accidental exposure of private keys or staging keys
4. **Performance**: Minimize latency for JWKS fetches (CDN-friendly caching)
5. **Privacy**: Do not leak internal infrastructure details (tenant IDs, client details)
6. **Compatibility**: Follow OIDC/OAuth 2.0 JWKS conventions (RFC 7517)

### Requirements

- Upstream IdPs can fetch public keys via HTTP GET
- Keys are cached efficiently (reduce load on Auth server)
- Only **active and publishable** keys are exposed
- Private key material **never** leaves the server
- Rollover workflow is operator-friendly
- Optional aggregation for multi-provider scenarios

---

## Decision

**We will implement three JWKS endpoints, each behind a feature flag:**

1. **Per-Provider JWKS**: `/providers/{providerName}/jwks`
2. **Aggregated Provider JWKS**: `/providers/jwks`
3. **Client JWKS (Optional)**: `/clients/{clientId}/jwks`

### Design Principles

1. **Explicit Opt-In**: Endpoints require feature flags (`ExposeProviderJwks`, `ExposeAggregatedProviderJwks`, `ExposeClientJwks`)
2. **Dual-Gate Publishing**: Keys must be both `Active=true` **and** `Publishable=true` to appear in JWKS
3. **No Discovery Advertisement**: JWKS URLs are **not** included in `/.well-known/openid-configuration` to avoid noise and support internal-only usage
4. **Strong Caching**: ETags + `Cache-Control` headers enable efficient conditional requests
5. **Rate Limiting**: Apply global anonymous rate limiter (`rl-jwks`) to prevent abuse

---

## Architecture

### Endpoint Paths

| Endpoint | Feature Flag | Purpose |
|----------|--------------|---------|
| `/providers/{providerName}/jwks` | `ExposeProviderJwks` | Per-provider signing keys for outbound JAR |
| `/providers/jwks` | `ExposeAggregatedProviderJwks` | All provider keys combined (multi-issuer support) |
| `/clients/{clientId}/jwks` | `ExposeClientJwks` | Client public keys (inbound JAR; rarely used) |

**Path Rationale**:
- `/providers/*` chosen over `/.well-known/jwks/{provider}` to avoid confusion with standard OIDC discovery
- `{providerName}` uses database `Name` field (machine-safe, unique, case-sensitive)
- No `/api/` prefix (these are public, not admin endpoints)

### Data Model

**IdentityProviderKeys Table**:
```sql
CREATE TABLE "IdentityProviderKeys" (
    "Id" UUID PRIMARY KEY,
    "IdentityProviderId" UUID NOT NULL,
    "Purpose" INT NOT NULL,  -- 0=Signing, 1=Encryption
    "Jwk" TEXT NOT NULL,     -- JSON Web Key (includes private params)
    "Alg" VARCHAR(20),       -- RS256, PS256, ES256, etc.
    "Active" BOOLEAN,        -- Used for signing operations
    "Publishable" BOOLEAN,   -- Exposed in JWKS endpoint
    "Kid" VARCHAR(200),      -- Key ID (unique per provider)
    "CreatedAt" TIMESTAMPTZ,
    "ExpiresAt" TIMESTAMPTZ
);

-- NEW: Composite index for JWKS lookups
CREATE INDEX "IX_IdentityProviderKeys_ProvId_Active_Pub_Purpose"
ON "IdentityProviderKeys" ("IdentityProviderId", "Active", "Publishable", "Purpose");
```

**Dual-Gate Logic**:
```csharp
// Only these keys are exposed:
var publishableKeys = await _db.IdentityProviderKeys
    .Where(k => k.IdentityProviderId == providerId 
             && k.Active == true 
             && k.Publishable == true 
             && k.Purpose == IdentityProviderKeyPurpose.Signing)
    .ToListAsync();
```

### Response Format

**Standard JWKS (RFC 7517)**:
```json
{
  "keys": [
    {
      "kty": "RSA",
      "use": "sig",
      "kid": "2025-10-v2",
      "alg": "RS256",
      "n": "<base64url-encoded-modulus>",
      "e": "AQAB"
    }
  ]
}
```

**Key Sanitization**:
- Strip private parameters (`d`, `p`, `q`, `dp`, `dq`, `qi`, `k`)
- Include only: `kty`, `use`, `kid`, `alg`, public params (`n`, `e` for RSA; `x`, `y`, `crv` for EC)
- Omit `exp` (expiration in JWKS is non-standard; document rotation policy instead)

### Caching Strategy

**HTTP Headers**:
```http
HTTP/1.1 200 OK
Content-Type: application/json
Cache-Control: public, max-age=300
ETag: "sha256-abc123..."
```

**ETag Calculation**:
```csharp
// Strong validator: hash of sorted (kid + alg) pairs
var etag = SHA256(string.Join("|", keys.OrderBy(k => k.Kid).Select(k => $"{k.Kid}:{k.Alg}")));
```

**Conditional Requests**:
- Client sends `If-None-Match: "abc123..."`
- Server returns `304 Not Modified` if ETag matches (no body)
- Server returns `200 OK` with new ETag if keys changed

**Cache Lifetime**:
- **TTL**: 5 minutes (`max-age=300`)
- **Rationale**: Balance between freshness (rotation) and load reduction
- **Upstream behavior**: Most OIDC clients respect `Cache-Control`; some may cache longer (1 hour)

### Security Considerations

1. **Private Key Protection**:
   - Private JWK params stripped in `SanitizeJwk()` method
   - Database encryption recommended for `Jwk` column (TDE or column-level encryption)

2. **Accidental Exposure Prevention**:
   - `Publishable` flag defaults to `false`
   - Admin UI shows warning: "Unpublishing active JAR key will break upstream verification"
   - Cannot unpublish active signing key while `UseJAR=true` (enforced guard)

3. **Rate Limiting**:
   - Policy: `rl-jwks` (global anonymous limiter)
   - Recommended: 60 requests/min per IP, 1000 requests/min global
   - Response: `429 Too Many Requests` with `Retry-After` header

4. **No Authentication**:
   - JWKS endpoints are public (no auth required)
   - Rationale: Standard OIDC practice; keys are public anyway
   - Mitigation: Rate limiting + ETag caching reduces abuse surface

---

## Alternatives Considered

### Alternative 1: Include JWKS URLs in Discovery

**Approach**: Add `jwks_uri` per provider to `/.well-known/openid-configuration`

**Rejected Reasons**:
- **Discovery bloat**: Single discovery doc would list 10+ provider JWKS URLs
- **Internal-only use**: Most upstream IdPs don't need MrWhoOidc's discovery doc (they have their own)
- **Complexity**: Multi-provider discovery spec is non-standard

**Decision**: Document JWKS URLs separately; upstream IdPs configure manually or via out-of-band config

---

### Alternative 2: Aggregated JWKS Only (No Per-Provider)

**Approach**: Single `/jwks` endpoint with all provider keys, rely on `kid` uniqueness

**Rejected Reasons**:
- **Kid conflicts**: Different providers may generate same `kid` (e.g., `default`)
- **Noisy response**: Upstream IdP fetching 50+ keys when only 1 is relevant
- **Security**: Exposes all providers' keys even if some are internal-only

**Decision**: Implement **both** per-provider and aggregated; default to per-provider

---

### Alternative 3: Mutable JWKS (Auto-Add New Keys)

**Approach**: Automatically publish keys when marked `Active=true`

**Rejected Reasons**:
- **No overlap period**: Upstream IdPs cache old JWKS, cutover would fail
- **Operator control**: Admins should explicitly approve publication
- **Rollback complexity**: Cannot easily revert if upstream rejects new key

**Decision**: Require explicit `Publishable=true` toggle (dual-gate)

---

### Alternative 4: Client JWKS as Primary Use Case

**Approach**: Focus on client JWKS (`/clients/{clientId}/jwks`) for inbound JAR

**Rejected Reasons**:
- **Not needed**: Clients already register JWKS via Admin UI (`ClientKeys` table)
- **Privacy**: Leaks client IDs and key material to unauthorized parties
- **Rare use case**: Only useful if external tools need to sync client keys

**Decision**: Implement client JWKS as **opt-in** feature (default `false`)

---

## Implementation Details

### Feature Flags

**`appsettings.json`**:
```json
{
  "Auth": {
    "ExposeProviderJwks": true,           // Per-provider JWKS
    "ExposeAggregatedProviderJwks": true, // Aggregated JWKS
    "ExposeClientJwks": false             // Client JWKS (opt-in)
  }
}
```

**Environment Overrides**:
```bash
# Development: Enable all for testing
Auth__ExposeProviderJwks=true
Auth__ExposeAggregatedProviderJwks=true
Auth__ExposeClientJwks=true

# Production: Enable only per-provider
Auth__ExposeProviderJwks=true
Auth__ExposeAggregatedProviderJwks=false
Auth__ExposeClientJwks=false
```

### Endpoint Registration

**`Program.cs`**:
```csharp
// Provider JWKS endpoints
if (authOptions.ExposeProviderJwks)
{
    app.MapGet("/providers/{providerName}/jwks", PublicJwksHandler.GetProviderJwks)
       .WithName("ProviderJwks")
       .RequireRateLimiting("rl-jwks")
       .Produces<JsonWebKeySet>(200);
}

if (authOptions.ExposeAggregatedProviderJwks)
{
    app.MapGet("/providers/jwks", PublicJwksHandler.GetAggregatedProviderJwks)
       .WithName("AggregatedProviderJwks")
       .RequireRateLimiting("rl-jwks")
       .Produces<JsonWebKeySet>(200);
}

if (authOptions.ExposeClientJwks)
{
    app.MapGet("/clients/{clientId}/jwks", PublicJwksHandler.GetClientJwks)
       .WithName("ClientJwks")
       .RequireRateLimiting("rl-jwks")
       .Produces<JsonWebKeySet>(200);
}
```

### Caching Service

**`PublicJwksCache`**:
```csharp
public class PublicJwksCache
{
    private readonly IMemoryCache _cache;
    private readonly AuthDbContext _db;

    public async Task<(JsonWebKeySet, string etag)> GetProviderJwksAsync(string providerName)
    {
        return await _cache.GetOrCreateAsync($"jwks:provider:{providerName}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            
            var provider = await _db.IdentityProviders
                .FirstOrDefaultAsync(p => p.Name == providerName && p.Enabled);
            if (provider == null) return (null, null);

            var keys = await _db.IdentityProviderKeys
                .Where(k => k.IdentityProviderId == provider.Id 
                         && k.Active && k.Publishable 
                         && k.Purpose == IdentityProviderKeyPurpose.Signing)
                .ToListAsync();

            var jwks = new JsonWebKeySet
            {
                Keys = keys.Select(k => SanitizeJwk(k.Jwk)).ToList()
            };

            var etag = ComputeEtag(keys);
            return (jwks, etag);
        });
    }
}
```

### Metrics

**OpenTelemetry Counters**:
- `oidc.provider_jwks.requests` (tags: `provider`, `status`)
- `oidc.provider_jwks.cache_hit` / `cache_miss` (tag: `provider`)
- `oidc.provider_jwks.zero_keys` (tag: `provider`) — Alert trigger
- `oidc.provider_jwks.keys_returned` (gauge, tag: `provider`)
- `oidc.provider_jwks.etag_changes` (tag: `provider`)

---

## Consequences

### Positive

1. **Operator-Friendly Rotation**: Dual-gate (`Active` + `Publishable`) allows safe overlap periods
2. **Performance**: ETag + caching reduces DB load by >95% (typical JWKS fetch rate)
3. **Security**: Private keys never exposed; accidental publication prevented
4. **Flexibility**: Per-provider and aggregated options support diverse upstream IdP requirements
5. **Standards Compliance**: RFC 7517 JWKS format ensures broad compatibility

### Negative

1. **Manual Configuration**: Upstream IdPs must manually discover JWKS URL (not in OIDC discovery)
2. **Cache Staleness**: 5-min TTL means key rotation has 5-min lag (acceptable for overlap strategy)
3. **Index Overhead**: New composite index adds ~10% to write operations (acceptable; reads dominate)

### Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| **Kid conflicts** (aggregated JWKS) | Medium | High | Deduplication logic (first seen wins); document best practice to use unique kids |
| **Cache poisoning** | Low | Medium | Strong ETag validation; rate limiting |
| **Upstream caches old JWKS** | High | Low | Rotation playbook mandates 48-hour overlap (T-2 to T+0) |
| **Zero keys published** | Low | High | Metrics alert (`oidc.provider_jwks.zero_keys`); Admin UI warning |

---

## Success Metrics

- **Cache Hit Rate**: >90% (target: 95%)
- **p99 Latency**: <50ms for cached JWKS fetch
- **Key Rotation Success**: Zero failed upstream auth requests during rotation
- **Uptime**: 99.9% (JWKS endpoint availability)

---

## Migration Plan

### Phase 1: Index Deployment (Immediate)
1. Deploy migration `AddIndexForPublicJwksCache` to staging
2. Run performance test: 1000 providers, 10 keys each, 1000 req/sec
3. Validate p99 latency <50ms
4. Deploy to production

### Phase 2: Documentation (Week 1)
1. Publish key rotation playbook
2. Update admin guide with JWKS endpoint usage
3. Add curl examples to developer guide

### Phase 3: Monitoring (Week 2)
1. Configure alerts for `zero_keys` metric
2. Build Grafana dashboard for JWKS metrics
3. Document recommended alert thresholds

---

## References

- [RFC 7517: JSON Web Key (JWK)](https://www.rfc-editor.org/rfc/rfc7517.html)
- [RFC 9101: JWT-Secured Authorization Request (JAR)](https://www.rfc-editor.org/rfc/rfc9101.html)
- [Key Rotation Playbook](../key-rotation-playbook.md)
- [PublicJwksEndpointsTests.cs](../../MrWhoOidc.UnitTests/PublicJwksEndpointsTests.cs)

---

## Approval

| Role | Name | Date | Signature |
|------|------|------|-----------|
| Lead Engineer | [Name] | 2025-10-14 | Approved |
| Security Review | [Name] | [Pending] | [Pending] |
| Platform Architect | [Name] | 2025-10-14 | Approved |

---

## Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-10-14 | AI Assistant | Initial ADR for P0 production readiness |
