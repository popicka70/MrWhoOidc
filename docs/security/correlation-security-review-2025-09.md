# Security Review: Correlation Handles & State Cache

Date: 2025-09-29  
Reviewer: Platform security triad (appsec + identity + observability)

## Scope

Assess the new correlation pipeline covering `CorrelationTrackingMiddleware`, `CorrelationStateCache`, external OIDC handlers, and admin middleware. Goals:

- Confirm correlation identifiers and handles meet entropy requirements.
- Ensure no user-identifiable data leaks via headers, logs, or state payloads.
- Validate cache TTL/eviction and Redis interactions align with privacy policy.
- Review failure handling for stale handles and invalid headers.

## Architecture summary

1. Client submits optional `X-Correlation-Id` header (`<=64` chars, `[A-Za-z0-9-_]`). Middleware validates and adopts; otherwise generates a 128-bit random Crockford Base32 ID.
2. External `/authorize` path persists `cid_ref` handles (96-bit random Base64Url) in `CorrelationStateCache` (IMemoryCache + optional Redis) with a fixed 10-minute TTL.
3. Front-channel embeds only the opaque handle; the raw CID never traverses the browser.
4. Callbacks resolve the handle without consuming it (refreshing TTL) and hash the handle before logging (`ShortHash` → 24-bit hex).
5. Admin APIs warn on missing headers but never emit correlation IDs into responses.

## Checklist

| Check | Status | Notes |
|-------|--------|-------|
| Correlation ID entropy ≥ 96 bits | ✅ | `CorrelationIdGenerator.GenerateCorrelationId()` uses 128-bit RNG and Crockford Base32 encoding (no padding, fixed length). |
| Handle entropy ≥ 96 bits | ✅ | Handles use 96-bit RNG (`GenerateHandle`) and Base64Url encoding; length 16 chars (trimmed padding). |
| Header validation prevents injection | ✅ | `IsValidHeader` restricts to alphanum plus `-` and `_`; length clamp at 64 ensures log safety. |
| Handles not persisted beyond TTL | ✅ | `CorrelationStateCache` stores entries in memory with 10-minute expiry and optional Redis key `cid:handle:*` with same TTL; stale entries counted and removed. |
| Logging avoids raw handles | ✅ | `HashHandleForLog` short-hashes handles before logging; correlation IDs may appear in structured logs but are non-PII random tokens. |
| Redis fallbacks safe | ✅ | Exceptions caught and downgraded to warnings; cache miss simply regenerates CID. No secret leakage. |
| Error responses avoid sensitive data | ✅ | Friendly error page receives handle only; messages are user-safe. |
| Admin pipeline enforces reuse | ✅ | `AdminCorrelationMiddleware` (via `CorrelationTrackingMiddleware`) reuses existing IDs; missing header triggers warning not failure. |
| Replay/guess resistance | ✅ | Handles require 96-bit entropy; guessing success probability < 2^-96, acceptable per threat model. |
| Documentation updated | ✅ | Developer/admin guides + ADR-0008 link to this review. |

## Threat discussion

- **Handle theft**: Exposure provides only CID handle—useless without cache entry. TTL and randomization mitigate reuse. Friendly error minted when stale.
- **Header abuse**: Validation prevents CRLF/header injection and overlong values. Invalid headers trigger warning in logs without adopting value.
- **Redis compromise**: Keys are opaque, but still treat store as sensitive. Recommendation: restrict Redis ACLs to service principal; rotate if breach suspected.
- **Correlation/PII linkage**: CID is random and not tied to user attributes; no persistence beyond logs. Ensure log retention policies treat correlation IDs as non-PII tokens.

## Recommendations

1. **Monitoring**: Alert when `oidc.correlation.cache.stale` spikes (>1% over 5 min) to detect Redis issues.
2. **Pen-test**: Include correlation handle replay attempts in next external penetration test (documented in backlog).
3. **Redis ACL**: Ensure production Redis allows only app identity (tracked separately in infrastructure repo).

## Conclusion

The correlation pipeline meets the security expectations outlined in ADR-0008. Entropy, validation, and logging controls are sufficient. No additional engineering work required beyond the monitoring recommendation. Backlog item "Security review" can be marked complete.
