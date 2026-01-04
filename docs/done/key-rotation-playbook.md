# Provider Key Rotation Playbook

**Version**: 1.0  
**Date**: 2025-10-14  
**Scope**: Identity Provider signing key rotation for outbound JAR

---

## Overview

This playbook provides step-by-step procedures for rotating Identity Provider signing keys used for outbound JAR (JWT-secured Authorization Requests) to upstream IdPs. The goal is zero-downtime key rotation using an overlap strategy.

### Key Concepts

- **Active Key**: Used by MrWhoOidc to sign outbound JAR requests
- **Publishable Key**: Exposed via `/providers/{providerName}/jwks` for upstream IdP verification
- **Overlap Period**: Time window where both old and new keys are published (24-48 hours recommended)
- **Grace Period**: Additional time before deactivating old key (allows for cached JWKS TTL)

---

## Prerequisites

- [ ] Admin access to MrWhoOidc Admin UI (`/admin`)
- [ ] Access to upstream IdP configuration (if manual key registration required)
- [ ] Understanding of upstream IdP's JWKS caching behavior (TTL, cache-control headers)
- [ ] Monitoring/alerting configured for key expiry warnings (`oidc.keys.expiry_warning`)

---

## Rotation Timeline

### Recommended Schedule (7-Day Overlap)

| Day | Time | Action | Status |
|-----|------|--------|--------|
| **T-7** | 09:00 | Generate new key | New key created, **not active**, **not publishable** |
| **T-2** | 09:00 | Publish new key | New key becomes **publishable** (added to JWKS) |
| **T+0** | 09:00 | Activate new key | New key becomes **active** (used for signing) |
| **T+2** | 09:00 | Unpublish old key | Old key removed from JWKS |
| **T+3** | 09:00 | Deactivate old key | Old key marked inactive (can be deleted later) |

**Notes:**
- T+0 is the "cutover" moment when new key becomes the active signing key
- 2-day overlap (T-2 to T+0) allows upstream IdPs to fetch new key before cutover
- 2-day grace period (T+0 to T+2) handles JWKS cache TTL (default 5 min, but may be longer)
- Total window: 10 days from generation to old key deactivation

---

## Step-by-Step Procedures

### Step 1: Generate New Key (T-7)

**Goal**: Create new signing key without impacting current operations

1. Navigate to **Admin → Identity Providers → [Provider Name] → Keys**
2. Click **Add New Key** or **Import Key**
3. Choose key generation method:
   
   **Option A: Generate RSA Key**
   - Algorithm: RS256 (recommended) or PS256
   - Key Size: 2048 bits (minimum) or 4096 bits (recommended)
   - Purpose: Signing
   - Kid: Auto-generated (e.g., `rsa-2025-10-14-v2`)
   - Expiration: 365 days from creation (optional)

   **Option B: Import Existing PEM Key**
   ```bash
   # Generate key via OpenSSL
   openssl genrsa -out provider-key-2025-10.pem 4096
   openssl rsa -in provider-key-2025-10.pem -pubout -out provider-key-2025-10-pub.pem
   
   # Convert to JWK (use online tool or custom script)
   # Import private JWK via Admin UI
   ```

4. Set initial state:
   - ✅ **Active**: `false` (will activate at T+0)
   - ✅ **Publishable**: `false` (will publish at T-2)
   - ✅ **Algorithm**: `RS256` or `PS256`
   - ✅ **Kid**: Unique identifier (e.g., `2025-10-v2`)

5. Click **Save**

**Verification**:
- [ ] New key appears in keys list with **Active=false**, **Publishable=false**
- [ ] Kid is unique (no conflicts with existing keys)
- [ ] Provider's outbound JAR still uses old key

---

### Step 2: Publish New Key (T-2)

**Goal**: Make new key discoverable by upstream IdPs via JWKS endpoint

1. Navigate to **Admin → Identity Providers → [Provider Name] → Keys**
2. Locate new key created at T-7
3. Click **Publish** action
   - This sets `Publishable = true`
   - Key will immediately appear in `/providers/{providerName}/jwks`

4. Verify JWKS exposure:
   ```bash
   # Fetch provider JWKS
   curl -i https://your-mrwhooidc-domain/providers/{providerName}/jwks
   
   # Expected: HTTP 200 with JSON containing both old and new keys
   # Cache-Control: public, max-age=300
   # ETag: "abc123..."
   
   # Sample response:
   {
     "keys": [
       {
         "kty": "RSA",
         "use": "sig",
         "kid": "2025-09-v1",  // OLD KEY
         "alg": "RS256",
         "n": "...",
         "e": "AQAB"
       },
       {
         "kty": "RSA",
         "use": "sig",
         "kid": "2025-10-v2",  // NEW KEY
         "alg": "RS256",
         "n": "...",
         "e": "AQAB"
       }
     ]
   }
   ```

5. **If upstream IdP requires manual key registration:**
   - Copy new key's public JWK from JWKS response
   - Register in upstream IdP's client configuration
   - Update allowed signing algorithms if needed

**Verification**:
- [ ] New key visible in JWKS endpoint (both keys present)
- [ ] ETag changed (indicates cache invalidation)
- [ ] Old key still **active** (provider still signs with old key)
- [ ] Upstream IdP can fetch JWKS successfully
- [ ] Wait 48 hours for upstream IdPs to cache new JWKS

---

### Step 3: Activate New Key (T+0 – Cutover)

**Goal**: Switch active signing key from old to new

1. Navigate to **Admin → Identity Providers → [Provider Name] → Keys**
2. Locate new key (published at T-2)
3. Click **Activate** action
   - This sets `Active = true` for new key
   - Automatically sets `Active = false` for previous active key (only one active signing key allowed per provider)

4. **Critical**: Verify cutover immediately:
   ```bash
   # Test outbound JAR signing with test client
   # Check upstream IdP logs for successful verification
   
   # Monitor metrics:
   # - oidc.external_callback.outcomes (should remain successful)
   # - No spike in access_denied errors from upstream
   ```

**Rollback Procedure** (if issues detected within 1 hour):
1. Navigate back to **Admin → Identity Providers → [Provider Name] → Keys**
2. Click **Activate** on old key to revert
3. Investigate issue before reattempting

**Verification**:
- [ ] New key shows **Active=true**, **Publishable=true**
- [ ] Old key shows **Active=false**, **Publishable=true** (still in JWKS)
- [ ] External OIDC sign-ins succeed with upstream IdPs
- [ ] No error spikes in logs/metrics
- [ ] Wait 48 hours for monitoring

---

### Step 4: Unpublish Old Key (T+2)

**Goal**: Remove old key from public JWKS (but keep it in DB for audit)

1. Navigate to **Admin → Identity Providers → [Provider Name] → Keys**
2. Locate old key (now inactive)
3. Click **Unpublish** action
   - This sets `Publishable = false`
   - Key removed from `/providers/{providerName}/jwks` immediately

4. Verify JWKS:
   ```bash
   curl -i https://your-mrwhooidc-domain/providers/{providerName}/jwks
   
   # Expected: HTTP 200 with JSON containing ONLY new key
   # ETag changed again
   
   {
     "keys": [
       {
         "kty": "RSA",
         "use": "sig",
         "kid": "2025-10-v2",  // NEW KEY ONLY
         "alg": "RS256",
         "n": "...",
         "e": "AQAB"
       }
     ]
   }
   ```

**Verification**:
- [ ] JWKS contains only new key
- [ ] Old key still exists in database (for audit)
- [ ] No errors in external sign-in flows
- [ ] Wait 24 hours before final cleanup

---

### Step 5: Deactivate Old Key (T+3 – Final Cleanup)

**Goal**: Mark old key as inactive (optional: delete after retention period)

1. Navigate to **Admin → Identity Providers → [Provider Name] → Keys**
2. Locate old key (unpublished at T+2)
3. Confirm old key shows:
   - **Active**: `false`
   - **Publishable**: `false`

4. **Optional**: Delete old key if:
   - Retention policy allows (e.g., 90 days post-deactivation)
   - No audit/compliance requirements to preserve
   - Click **Delete** action (requires confirmation)

**Verification**:
- [ ] Old key deactivated or deleted
- [ ] Rotation complete
- [ ] Document rotation in audit log / runbook

---

## Emergency Rotation (Compromised Key)

If a signing key is compromised, follow accelerated rotation:

### Immediate Actions (Within 1 Hour)

1. **Generate and activate new key immediately** (skip T-7 and T-2 steps):
   - Admin → Identity Providers → [Provider] → Keys → Add New Key
   - Set **Active=true** and **Publishable=true** immediately
   
2. **Deactivate compromised key**:
   - Set **Active=false** and **Publishable=false**
   
3. **Notify upstream IdPs** (if manual key registration required):
   - Provide new public JWK urgently
   - Request expedited JWKS cache invalidation

4. **Monitor for abuse**:
   - Check upstream IdP logs for suspicious auth requests signed with old key
   - Review audit logs for unauthorized external sign-ins

### Post-Incident (Within 24 Hours)

5. **Investigate compromise source**:
   - How was key leaked?
   - Update key storage security (move to Key Vault if not already)

6. **Update procedures**:
   - Enforce shorter key lifetimes (e.g., 90 days instead of 365)
   - Implement automated key rotation

---

## JWKS Endpoint Reference

### Per-Provider JWKS

**Endpoint**: `/providers/{providerName}/jwks`

**Feature Flag**: `AuthOptions.ExposeProviderJwks` (must be `true`)

**Response**:
```json
{
  "keys": [
    {
      "kty": "RSA",
      "use": "sig",
      "kid": "2025-10-v2",
      "alg": "RS256",
      "n": "<modulus>",
      "e": "AQAB"
    }
  ]
}
```

**Headers**:
- `Cache-Control: public, max-age=300` (5 min cache)
- `ETag: "<hash>"` (strong validator for conditional requests)

**Caching Behavior**:
- Clients should respect `Cache-Control` header
- Use `If-None-Match` with ETag for conditional requests (returns `304 Not Modified` if unchanged)
- ETag changes when key set changes (add/remove publishable keys)

**Example with cURL**:
```bash
# Initial fetch
curl -i https://your-domain/providers/azure-ad/jwks
# Note: Copy ETag from response header

# Conditional fetch (checks if keys changed)
curl -i -H 'If-None-Match: "abc123..."' https://your-domain/providers/azure-ad/jwks
# Returns 304 if unchanged, 200 with new ETag if changed
```

---

### Aggregated Provider JWKS

**Endpoint**: `/providers/jwks`

**Feature Flag**: `AuthOptions.ExposeAggregatedProviderJwks` (must be `true`)

**Response**: Combined keys from all enabled providers

**Use Case**: Upstream IdPs that accept multiple issuers can fetch all keys at once

**Note**: Kid conflicts across providers will be deduplicated (first seen wins)

---

### Client JWKS (Optional)

**Endpoint**: `/clients/{clientId}/jwks`

**Feature Flag**: `AuthOptions.ExposeClientJwks` (must be `true`, default `false`)

**Use Case**: Rarely used; only enable if downstream ecosystem tools need client public keys

---

## Monitoring & Alerts

### Key Metrics

Monitor these metrics for rotation health:

1. **`oidc.provider_jwks.requests`** (counter)
   - Tags: `provider`, `status` (200/304/404)
   - Alert: Spike in 404 errors (indicates invalid provider name in upstream config)

2. **`oidc.provider_jwks.keys_returned`** (gauge)
   - Tags: `provider`
   - Alert: Zero keys for enabled provider with `UseJAR=true`

3. **`oidc.provider_jwks.etag_changes`** (counter)
   - Tags: `provider`
   - Alert: Unexpected ETag changes (indicates unauthorized key modifications)

4. **`oidc.keys.expiry_warning`** (counter) [P1 - Not Yet Implemented]
   - Tags: `provider`, `kid`, `days_until_expiry`
   - Alert: Key expiring within 7 days

### Log Events

Watch for these structured log events:

- **`ZeroKeysJarEnabled`**: Warning when provider has `UseJAR=true` but zero publishable keys
- **`ZeroKeysActiveNonPublishable`**: Warning when provider has active signing keys but none are publishable
- **`JwksCacheMiss`**: Info event when JWKS cache refreshed (should correlate with ETag changes)

---

## Troubleshooting

### Issue: Upstream IdP rejects outbound JAR after activation

**Symptoms**: `access_denied` or `invalid_request_object` errors from upstream

**Diagnosis**:
1. Check upstream IdP logs for JWT signature validation errors
2. Verify upstream cached old JWKS (check their JWKS TTL)
3. Confirm new key is **both Active and Publishable**

**Resolution**:
- **Option A**: Rollback to old key (re-activate) and extend T-2 to T+0 window
- **Option B**: Manually invalidate upstream JWKS cache (if supported)
- **Option C**: Wait for upstream cache TTL expiry (may take up to 1 hour)

---

### Issue: New key not appearing in JWKS after publishing

**Symptoms**: `/providers/{providerName}/jwks` returns only old key

**Diagnosis**:
1. Verify `Publishable = true` in database:
   ```sql
   SELECT * FROM "IdentityProviderKeys" 
   WHERE "IdentityProviderId" = '<guid>' 
   AND "Publishable" = true;
   ```
2. Check JWKS cache expiry (5 min TTL)
3. Verify provider name in URL matches database `Name` (case-sensitive)

**Resolution**:
- Wait 5 minutes for cache refresh
- Or restart application to force cache invalidation (not recommended in production)

---

### Issue: ETag not changing after publish/unpublish

**Symptoms**: ETag remains same after key operations

**Diagnosis**:
- Check if duplicate kid caused deduplication logic to skip new key
- Verify `PublicJwksCache` service is invalidating properly

**Resolution**:
- Ensure kid is unique across all provider keys
- Check logs for `JwksCacheMiss` events

---

## Best Practices

### Key Lifetime

- **Recommended**: 365 days (1 year)
- **Maximum**: 730 days (2 years)
- **Minimum**: 90 days (for high-security environments)

### Rotation Frequency

- **Standard**: Rotate every 12 months (before expiry)
- **High-Security**: Rotate every 6 months
- **Compliance-Driven**: Follow organizational policy (e.g., PCI-DSS requires annual rotation)

### Key Storage

- **Development**: Database storage acceptable
- **Production**: Use Azure Key Vault or AWS KMS for private key storage
  - Store only key reference/ID in database
  - Fetch private key on-demand for signing operations

### Automation

Consider automating rotation for production:

1. **Automated Generation**: Scheduled job to generate new key at T-7
2. **Automated Publishing**: Scheduled job to publish at T-2
3. **Manual Activation**: Keep T+0 manual to allow operator verification
4. **Automated Cleanup**: Scheduled job to unpublish/deactivate old keys at T+2/T+3

---

## Appendix: Configuration Reference

### Feature Flags

Enable JWKS endpoints in `appsettings.json`:

```json
{
  "Auth": {
    "ExposeProviderJwks": true,          // Per-provider JWKS
    "ExposeAggregatedProviderJwks": true, // Aggregated JWKS
    "ExposeClientJwks": false             // Client JWKS (usually disabled)
  }
}
```

### Rate Limiting

JWKS endpoints use `rl-jwks` policy (global anonymous limiter):

**Recommended Limits**:
- **Per-IP**: 60 requests / minute
- **Global**: 1000 requests / minute

Configure in `appsettings.json`:
```json
{
  "RateLimiting": {
    "Policies": {
      "rl-jwks": {
        "PermitLimit": 60,
        "Window": "00:01:00",
        "QueueLimit": 0
      }
    }
  }
}
```

---

## Change Log

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-10-14 | AI Assistant | Initial playbook for P0 production readiness |

---

## Related Documents

- [Admin Guide](./admin-guide.md) - Identity Provider configuration
- [Developer Guide](./developer-guide.md) - JWKS endpoint integration
- [ADR-0009: JWKS Endpoints](./adr/adr-0009-jwks-endpoints.md) - Design decisions
- [Security Review: Key Management](./security/key-management-review.md) - Security considerations
