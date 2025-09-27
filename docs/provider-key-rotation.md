# Provider Key Rotation Playbook

Updated: 2025-09-27

Applies to: Outbound JAR / PAR signing keys stored in `IdentityProviderKeys` and published (when `Publishable=true`) via `/providers/{providerName}/jwks`.

## 1. Goals
- Maintain continuous trust for upstream Identity Providers (IdPs) that validate outbound JAR (JWT-secured authorization requests) signed by our provider keys.
- Allow seamless rollover without downtime or signature validation failures.
- Minimize blast radius of a compromised / expiring key.
- Provide clear auditability, metrics, and rollback steps.

## 2. Terminology
| Term | Meaning |
|------|---------|
| Active | Key is eligible for operational use (signing outbound JAR) & inclusion candidate for JWKS (still needs Publishable). |
| Publishable | Key may be exposed in provider JWKS endpoint. Safety gate to avoid premature exposure. |
| Purpose=Signing | Only signing keys (not encryption) are published in Phase 1. |
| Rollover Window | Overlap period where old and new keys are both published to external fetchers. |
| ETag | Hash of sorted published key `kid` values; changes whenever effective published set changes. |

## 3. Safety Mechanisms
- Dual flags (`Active` + `Publishable`) prevent accidental exposure of staging / future keys.
- JWKS endpoint filters: `Active && Publishable && Purpose==Signing`.
- Duplicate `kid` values are de-duplicated at output (first wins, warnings logged for duplicates).
- Metrics surface anomalies:
  - `oidc.provider_jwks.requests`
  - `oidc.provider_jwks.keys.returned`
  - `oidc.provider_jwks.zero_keys`
  - `oidc.provider_jwks.etag_changes`
- Warning emitted if a JAR-enabled provider serves zero keys.

## 4. Rotation Strategy Overview
1. **Generate new key** (offline or via Admin UI import) – mark `Active=true`, `Publishable=false` initially.
2. **Validate** (integrity, alg compatibility, correct `kid`).
3. **Publish but do not switch signing**: set `Publishable=true` while *keeping old key Active + Publishable* (overlap begins).
4. **Update outbound signer** (if code selects a single canonical key — choose new key when ready). If signer auto-picks the most recently created active key, ensure timing is intentional.
5. **Monitor** metrics/logs for any upstream failures (e.g., JAR validations) and confirm upstream has fetched new JWKS (look for ETag change + normal request volume).
6. **Deactivate old key**: set old key `Active=false` but keep it around (not deleted) for audit for at least retention period.
7. **Optionally unpublish old key** (set `Publishable=false`) after maximum acceptable grace window passes (ensures no stale caches rely on it).
8. **Archive / remove** old key after compliance retention period.

## 5. Recommended Timeline (Example: 24h Overlap)
| Time | Action | Notes |
|------|--------|-------|
| T-24h | Import new key (Active=true, Publishable=false) | Not yet visible externally. |
| T-24h to T-23h | Internal validation (test signing, preview JWKS locally) | Use Admin UI JWKS preview or a dev environment. |
| T-23h | Set `Publishable=true` on new key | ETag changes; both keys now in JWKS. |
| T-23h to T-1h | Observe metrics (`etag_changes`, `keys.returned`, absence of `zero_keys`) | Upstream IdPs should refresh JWKS within their cache TTL. |
| T-1h | Switch outbound signing to new key (if manual) | Ensure subsequent JARs use new `kid`. |
| T | Deactivate old key (`Active=false`) | Still optionally `Publishable=true` until T+? |
| T+1h (or > worst-case JWKS cache TTL) | Set old key `Publishable=false` | JWKS shrinks; ETag changes again. |
| T+7d | Delete/archive old key record (optional) | Keep if audit retention required. |

Adjust durations based on upstream JWKS caching policies and SLAs. If upstream caches for 6h, overlap should exceed that + clock skew (e.g., 8–12h minimum).

## 6. Detailed Step Procedures
### 6.1 Generate / Import New Key
Use Admin UI (Providers → Keys → Import) or Admin API:
```
POST /admin/api/providers/{providerName}/keys
{
  "kid": "2025-09-rot-a",
  "alg": "RS256",
  "purpose": "Signing",
  "active": true,
  "publishable": false,
  "jwk": { ... private or public JWK ... }
}
```
Validation checks: unique `kid` per provider, supported `alg`, proper JWK fields.

### 6.2 Promote to Publishable
```
PATCH /admin/api/providers/{providerName}/keys/{keyId}
{
  "publishable": true
}
```
Confirm JWKS endpoint now lists new `kid`.

### 6.3 Switch Signing (Outbound JAR)
Implementation-dependent. If signer selects:
- "First active key by sort": set ordering/SortOrder accordingly.
- "Latest created active key": ensure creation timestamp ordering is correct.
- Explicit selection (future enhancement): update provider config / key reference.

### 6.4 Deactivate Old Key
```
PATCH /admin/api/providers/{providerName}/keys/{oldKeyId}
{
  "active": false
}
```
Keep `Publishable=true` until certainty that upstream never uses old key.

### 6.5 Unpublish Old Key
After the overlap > max upstream cache TTL:
```
PATCH /admin/api/providers/{providerName}/keys/{oldKeyId}
{
  "publishable": false
}
```
ETag changes again. Monitor for any sudden upstream verification errors (should be none). If errors appear, temporarily re-publish (set `Publishable=true`) to restore JWKS continuity.

## 7. Rollback Plan
| Scenario | Action |
|----------|--------|
| New key rejected by upstream (signing errors) | Revert signer to old key; (optional) set new key `Publishable=false` to remove from JWKS; investigate algorithms or formatting. |
| Upstream still using old key after unpublish | Temporarily set old key `Publishable=true` again; extend overlap; communicate deprecation timeline. |
| Compromise of old key detected during overlap | Immediately set `Active=false`, `Publishable=false`; ensure new key published and active; consider issuing upstream advisory to invalidate caches. |
| New key compromise after promotion | Remove new key (`Active=false`, `Publishable=false`), generate and publish replacement; shorten overlap; rotate any derived secrets if applicable. |

## 8. Observability & Verification
Checklist after each rotation phase:
- Metrics: `provider_jwks.keys.returned` reflects expected count (pre-switch: 2; post-unpublish: 1).
- `provider_jwks.etag_changes` increments at each publish set mutation.
- Logs: No warnings about duplicate `kid`; no `zero keys` warnings for JAR-enabled provider.
- Outbound signed JAR requests use expected `kid` (inspect an auth redirect or captured request object).
- Upstream error logs: none referencing signature or unknown kid.

Optional scripted verification (pseudo PowerShell):
```powershell
$jwks = Invoke-RestMethod https://localhost:5001/providers/acme/jwks
$kids = $jwks.keys | ForEach-Object { $_.kid }
if($kids -notcontains '2025-09-rot-a') { throw 'New key missing' }
```

## 9. Key Selection Guidance
Until explicit key preference configuration exists, follow these conventions:
- Maintain at most 2 simultaneously `Active && Publishable` signing keys (old + new) to reduce JWKS size.
- Ensure `kid` encodes a sortable date or sequence (e.g., `yyyyMMdd` prefix) to simplify deterministic selection should code rely on lexical ordering.
- Never deactivate the last publishable active key for a JAR-enabled provider (Admin UI should enforce this safeguard).

## 10. Security Considerations
- Never mark a key `Publishable` before verifying its private component (if stored) is intact and algorithm correct.
- Avoid reusing `kid` values; uniqueness improves cache behavior and audit clarity.
- Keep private key material encrypted at rest (DPAPI / Key Vault) per security baseline.
- If a key compromise is suspected, treat associated signed artifacts as potentially replayable and consider upstream revocation guidance.

## 11. Automation Hooks (Future Enhancements)
Planned / backlog:
- Background service detecting `ExpiresAt` approaching (e.g., < 7 days) → emit metric + structured warning.
- Admin dashboard badge for providers with single publishable key (no redundancy) or upcoming expiry.
- CLI / script to batch import + promote keys across multiple providers.

## 12. Quick Reference Cheat Sheet
| Task | API / UI Action |
|------|-----------------|
| Import new key | POST `/admin/api/providers/{p}/keys` (Active=true, Publishable=false) |
| Publish key | PATCH key → `publishable=true` |
| Switch signer | Depends (ordering / key selection heuristic) |
| Deactivate old key | PATCH old key → `active=false` |
| Unpublish old key | PATCH old key → `publishable=false` |
| Verify JWKS | GET `/providers/{p}/jwks` |
| Force cache re-eval | Invalidate via Admin (or wait TTL) |

## 13. Example Timeline Snapshot
```
Day 0 09:00  Import new key (A new), publishable=false
Day 0 09:15  Promote new key publishable=true (JWKS: old+new)
Day 0 18:00  Switch outbound signer → new key
Day 1 09:30  Deactivate old key (JWKS still old+new if publishable)
Day 1 10:00  Unpublish old key (JWKS: new only)
Day 8 09:00  Delete old key record (optional)
```

## 14. FAQ
**Q:** Why separate `Active` and `Publishable`?  
**A:** Prevents premature exposure; allows validation burn-in period before external distribution.

**Q:** Can I rotate multiple providers simultaneously?  
**A:** Yes, but stagger if upstream fetch load or rollback complexity is a concern.

**Q:** What if upstream caches JWKS longer than expected?  
**A:** Extend overlap; do not deactivate or unpublish old key prematurely; coordinate with upstream for cache TTL reduction if chronic.

**Q:** How are ETags computed?  
**A:** Deterministically from sorted published `kid` list (fallback hash of JSON). Any publishable set change → new ETag.

---
**End of Playbook**
