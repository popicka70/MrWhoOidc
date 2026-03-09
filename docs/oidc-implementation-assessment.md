# OIDC Implementation Assessment

**Date**: March 9, 2026  
**Product**: MrWhoOidc  
**Overall Rating**: A- (90/100) - Production-ready with targeted interoperability/security gaps

---

## Executive Summary

MrWhoOidc is a **production-ready OpenID Connect Provider** with comprehensive OAuth 2.0 support. The implementation demonstrates strong security practices, particularly in DPoP support and token handling. Recent work closed major gaps in token-endpoint mTLS behavior, refresh token hardening, and token-endpoint resource/claims parity.

**Recommendation**: Focus next on OIDF conformance automation and remaining advanced/optional interoperability scenarios.

---

## Correctly Implemented Features

### Core OIDC/OAuth 2.0

| Feature | Status | Implementation Quality |
|---------|--------|----------------------|
| Authorization Code Flow with PKCE | Complete | Excellent |
| Client Credentials Grant | Complete | Good |
| Refresh Token Grant | Complete | Good |
| Device Authorization Grant (RFC 8628) | Complete | Good |
| Token Exchange (RFC 8693) | Complete | Excellent |
| Dynamic Client Registration (RFC 7591) | Feature-flagged | Good |
| Pushed Authorization Requests (RFC 9126) | Feature-flagged | Good |
| JWT-Secured Authorization Requests (RFC 9101) | Complete | Good |
| JWT-Secured Authorization Responses | Complete | Good |

### Security Features

| Feature | Status | Implementation Quality |
|---------|--------|----------------------|
| DPoP (RFC 9449) | Complete | **Excellent** |
| JWT Access Tokens | Complete | Good |
| Opaque Access Tokens | Complete | Good |
| Backchannel Logout | Complete | Good |
| Token Introspection (RFC 7662) | Complete | Good |
| Token Revocation (RFC 7009) | Complete | Good |
| JWKS Endpoint | Complete | Good |

---

## Detailed Feature Analysis

### DPoP Implementation (RFC 9449) - Excellent

**Location**: `MrWhoOidc.Security/DPoP.cs`, `MrWhoOidc.WebAuth/Infrastructure/`

**Strengths**:
- Full proof validation (htm, htu, jti, iat, ath)
- JWK thumbprint computation (RFC 7638)
- Replay cache with Redis/in-memory backends
- Nonce management with challenge/response
- Token binding validation at `/token`, `/userinfo`, `/introspect`
- DPoP bridging policies for token exchange (Deny, RequireSameJkt, AllowSameJktOnly)

**Key Features**:
```csharp
// DPoP validation enforces:
// 1. typ = "dpop+jwt"
// 2. Required claims: htm, htu, jti, iat
// 3. HTTP method matching
// 4. URL path matching
// 5. Time window validation (5 minutes)
// 6. ath claim for token binding
// 7. Replay detection via jti
```

### Token Exchange (RFC 8693) - Excellent

**Location**: `MrWhoOidc.Auth/Services/TokenExchangeService.cs`

**Strengths**:
- Supports JWT and opaque access tokens as subject tokens
- DPoP bridging with configurable policies per client
- Delegation depth enforcement (max depth configurable)
- Audience validation with allow-list
- Scope downgrading support
- `act` claim population for delegation tracking

**DPoP Bridging Modes**:

| Mode | Behavior |
|------|----------|
| Deny | Rejects DPoP-bound subject tokens |
| RequireSameJkt | Requires same key, binds outgoing to same |
| AllowSameJktOnly | Requires subject to be DPoP-bound |

### Authorization Code Flow - Good

**Location**: `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs`, `AuthorizationCodeGrantHandler.cs`

**Strengths**:
- PKCE enforcement with S256
- State parameter handling
- Prompt parameter support (none, login, consent, select_account)
- Max age enforcement
- ACR value validation
- Consent processing
- IDP selection with sticky preference

**Observations**:
- PKCE should be mandatory for all public clients (OAuth 2.1)
- State parameter present but could have stricter validation

### Backchannel Logout - Good

**Location**: `MrWhoOidc.Web/Backchannel/`, `MrWhoOidc.Auth/Services/Token/LogoutTokenService.cs`

**Strengths**:
- Logout token validation (signature, nonce, events, sid/sub)
- Distributed revocation store with Redis
- Cookie principal validation against blacklist
- Replay cache for logout tokens

**Key Features**:
```csharp
// Validates:
// 1. Signature with OP keys
// 2. Nonce presence (no replay)
// 3. events claim with backchannel-logout event
// 4. sid or sub claim for session identification
// 5. Audience validation
```

### Discovery Endpoint - Good (with issues)

**Location**: `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs`

**Correctly Advertised**:
- All supported grant types
- Response types/modes
- Token endpoint auth methods (partial - see issues)
- JAR/JARM algorithms
- DPoP algorithms
- Scopes and claims

**Issues**: See "Critical Issues" section below

### Token Introspection (RFC 7662) - Good

**Location**: `MrWhoOidc.WebAuth/Handlers/Introspection/`

**Strengths**:
- Supports JWT and opaque access tokens
- Refresh token introspection
- DPoP validation at endpoint
- Audience policy enforcement
- Response shaping based on client permissions
- Inactive token handling

---

## Critical Issues

### 1. MTLS Authentication - Implemented Across Token Endpoint Flows

**Severity**: Medium  
**Location**: `DiscoveryHandler.cs:207-208, 353-360`

**Issue**:
```json
"token_endpoint_auth_methods_supported": [
  "client_secret_basic",
  "client_secret_post", 
  "private_key_jwt",
   "self_signed_tls_client_auth"
]
```

Discovery advertises `self_signed_tls_client_auth` and token-endpoint mTLS client authentication is now accepted consistently when a client has configured thumbprints and presents a matching certificate.

**Impact**: Interoperability improved for clients using mTLS auth on non-`client_credentials` token grants.

**Status**:
- Implemented broader token-endpoint mTLS auth acceptance.
- Discovery/tests aligned with current behavior.
- Certificate-bound access-token issuance remains scoped to supported issuance paths.

**Files to Modify**:
- `MrWhoOidc.Auth/Services/Authentication/ClientAuthenticationService.cs`
- `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs`

---

### 2. Pairwise Subject IDs - Implemented (No Gap)

**Severity**: Medium  
**Location**: `DiscoveryHandler.cs:201`

**Issue**:
```json
"subject_types_supported": ["public", "pairwise"]
```

Pairwise subject support is implemented and wired into token issuance. Existing tests validate stable pairwise behavior.

**Impact**: No remediation required beyond regression coverage.

**Remediation**:
- Keep discovery as-is.
- Maintain integration tests for pairwise generation/consistency.

**Files to Modify**:
- `MrWhoOidc.UnitTests/Integration/PairwiseSubjectIdentifiersTests.cs` (regression guardrails)

---

### 3. Frontchannel Logout - Implemented with Reliability Caveat

**Severity**: Medium  
**Location**: `DiscoveryHandler.cs:202-203`

**Issue**:

```json
"frontchannel_logout_supported": true,
"frontchannel_logout_session_supported": true
```

Backchannel and frontchannel logout are both implemented. The main remaining concern is operational reliability and timeout behavior for iframe fan-out, not missing functionality.

**Impact**: In slow browser/network conditions, logout propagation timing may be inconsistent.

**Remediation**:
- Keep discovery values as `true`.
- Add configurable frontchannel timeout and test for deterministic behavior.

---

### 4. Claims Parameter - Implemented for Authorization Code Token Exchange

**Severity**: Low  
**Location**: `MrWhoOidc.Auth/Protocols/OidcClaimsRequestParser.cs`

**Issue**:
The `claims` request parameter is parsed/normalized and now accepted at the token endpoint for `authorization_code`, with precedence/validation applied before ID token and userinfo claim shaping.

**Impact**: Better parity between authorize-time and token-time claim requests for authorization code flows.

**Follow-up**:
1. Expand tests around essential/non-essential constraint behavior
2. Document unsupported advanced modes (aggregated/distributed claims)

**Files to Modify**:
- `MrWhoOidc.Auth/Services/AuthorizationCodeService.cs`
- `MrWhoOidc.Auth/Services/Token/AccessTokenClaimBuilder.cs`

---

### 5. Resource Indicators - Enforced Across Additional Grants

**Severity**: Low  
**Location**: `DiscoveryHandler.cs:266`

**Issue**:
```json
"resource_indicators_supported": true
```

Resource indicators are validated and enforced at token endpoint for `authorization_code` and `refresh_token`, including `audience`/`resource` conflict checks and absolute URI validation.

**Impact**: Reduced audience confusion and stronger RFC 8707 behavior at token endpoint.

**Status**:
1. Token endpoint `resource` handling implemented for auth code and refresh grants
2. Audience/resource consistency checks implemented
3. Grant-specific validation tests added

**Files to Modify**:
- `MrWhoOidc.Auth/Services/AuthorizationCodeService.cs`
- `MrWhoOidc.WebAuth/TokenEndpoint/Grants/AuthorizationCodeGrantHandler.cs`
- `MrWhoOidc.Auth/Services/Token/ClientCredentialsTokenFactory.cs`

---

### 6. Encrypted ID Tokens - Advertising Unclear

**Severity**: Low  
**Location**: `DiscoveryHandler.cs:216-217, 276-280`

**Issue**:
Discovery advertises ID token encryption:
```json
"id_token_encryption_alg_values_supported": ["RSA-OAEP"],
"id_token_encryption_enc_values_supported": ["A256CBC-HS512"]
```

Client model has encryption fields (`IdTokenEncryptedResponseAlg`, `IdTokenEncryptedResponseEnc`) but encryption path is not clearly implemented.

**Remediation**:
- Verify ID token encryption exists in token generation path
- If missing, implement or remove from discovery
- Test with encrypted ID token request from client

---

## Recommended Additional Features

### OAuth 2.1 Compliance

**Priority**: High

OAuth 2.1 draft recommendations:

1. **Require PKCE for ALL clients** (not just public)
   - Modify `Client` model to remove PKCE optional flag
   - Update authorization code validation

2. **Deprecate refresh tokens for public clients without rotation**
   - Already has rotation - good

3. **Stricter state parameter validation**
   - Require state matching
   - Add minimum entropy requirements

**Files to Modify**:
- `MrWhoOidc.Auth/Services/AuthorizeRequestValidator.cs`
- `MrWhoOidc.Auth/Services/AuthorizationCodeService.cs`

---

### Refresh Token Security Improvements

**Priority**: High

**Status update (implemented March 9, 2026)**:
- Absolute refresh token lifetime cap implemented with family-origin enforcement.
- Refresh token family lineage/revocation implemented for reuse remediation.
- DPoP `cnf.jkt` binding implemented for refresh tokens (issuance + exchange validation).

**Recommendations**:

1. **Absolute refresh token lifetime**
   - Implemented with tenant setting `refreshTokenAbsoluteLifetimeSeconds`
   - Enforced across rotation families via family origin timestamp

2. **Refresh token family revocation**
   - Implemented using refresh token lineage (`ReplacedById` parent linkage)
   - Reuse now triggers targeted family revocation instead of broad user/client revocation

3. **DPoP binding for refresh tokens**
   - Implemented with persisted `cnf.jkt` and key continuity checks during refresh exchange

**Files to Modify**:
- `MrWhoOidc.Auth/Persistence/Token.cs`
- `MrWhoOidc.Auth/Services/Token/RefreshTokenExchanger.cs`

---

### Admin API Hardening

**Priority**: Medium

**Recommendations**:

1. **Rate limiting on sensitive endpoints**
   - Client creation/modification
   - User management
   - Key rotation

2. **Audit logging**
   - All client configuration changes
   - Key generation/rotation
   - Permission changes

3. **Approval workflows**
   - Two-person rule for production changes
   - Change request tracking

**Files to Add**:
- `MrWhoOidc.WebAuth/Infrastructure/Audit/AuditLogger.cs`
- `MrWhoOidc.WebAuth/Infrastructure/Audit/AuditEvent.cs`

---

### Federation Improvements

**Priority**: Medium

**Recommendations**:

1. **Automatic IdP metadata refresh**
   - Cache duration based on IdP metadata
   - Background job for stale metadata

2. **IdP metadata signature validation**
   - If IdP signs metadata, validate

3. **Multiple signing keys per IdP**
   - Key rotation support for federation

**Files to Modify**:
- `MrWhoOidc.Auth/IdentityProviders/OidcProviderConfig.cs`
- `MrWhoOidc.Auth/Services/IdentityProviderService.cs`

---

## Security Observations

### Strengths

| Area | Assessment | Notes |
|------|-----------|-------|
| DPoP Implementation | Excellent | Production-grade compliance |
| Token Binding | Excellent | Proper ath claim computation |
| Replay Protection | Excellent | Multi-layer (jti, nonce, code reuse) |
| Audience Validation | Good | Allow-list policy |
| Backchannel Logout | Good | Distributed revocation storage |
| JWT Typ Enforcement | Good | Requires `typ=at+jwt` |

### Areas for Improvement

| Area | Risk | Remediation |
|------|------|-------------|
| DPoP refresh tokens | Low | Implemented: refresh `cnf.jkt` continuity enforced |
| Absolute token lifetime | Low | Implemented: absolute family lifetime cap |
| Family revocation | Low | Implemented: targeted refresh-family revocation on reuse |
| Token introspection rate limiting | Low | Add rate limiting to prevent enumeration |

---

## Compliance Checklist

| Specification | Status | Notes |
|--------------|--------|-------|
| OIDC Core 1.0 | Mostly Complete | Pairwise subjects implemented; remaining fine-grained edge cases |
| OAuth 2.0 (RFC 6749) | Complete | All grants implemented |
| OAuth 2.1 Draft | Partial | PKCE should be mandatory |
| PKCE (RFC 7636) | Complete | S256 only |
| Token Exchange (RFC 8693) | Complete | With DPoP bridging |
| Token Introspection (RFC 7662) | Complete | JWT + opaque support |
| Token Revocation (RFC 7009) | Complete | Supported for access/refresh |
| Device Authorization (RFC 8628) | Complete | Feature-flagged |
| PAR (RFC 9126) | Complete | Feature-flagged |
| JAR (RFC 9101) | Complete | Signing + encryption |
| JARM | Complete | JWT response modes |
| DPoP (RFC 9449) | Complete | Full implementation |
| MTLS (RFC 8705) | Mostly Complete | Token endpoint mTLS auth behavior expanded; advanced profiles remain optional |
| Backchannel Logout | Complete | Session + subject-based |
| Frontchannel Logout | Complete | Implemented; reliability hardening recommended |
| Fine-Grained Claims | Mostly Complete | Token endpoint parity added for auth-code claims parameter |

---

## Testing Recommendations

### Immediate Actions

1. **Add conformance testing**
   - OIDF conformance suite for basic/profile
   - OAuth 2.0 Security BCP tests

2. **Expand discovery/endpoint regression coverage**
   - Keep `pairwise` and `frontchannel_logout_*` advertised
   - Keep token endpoint mTLS behavior and metadata in sync as new auth profiles are added
   - Verify `tls_client_certificate_bound_access_tokens` matches real issuance paths

3. **Add integration tests**
   - DPoP end-to-end flows
   - Token exchange with delegation
   - Backchannel logout scenarios

### Test Scenarios to Add

```yaml
# DPoP scenarios
- DPoP proof validation at /token
- DPoP proof validation at /userinfo
- DPoP proof validation at /introspect
- DPoP nonce challenge/response
- DPoP replay detection
- DPoP bridging in token exchange

# Token exchange scenarios
- JWT subject token exchange
- Opaque subject token exchange
- DPoP bridging with RequireSameJkt
- DPoP bridging with Deny
- Delegation depth enforcement
- Audience mismatch rejection

# Security scenarios
- Authorization code reuse detection
- Refresh token reuse detection
- Replay attack prevention
- Token expiration enforcement
```

---

## Files Requiring Changes

### High Priority

| File | Changes |
|------|---------|
| `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs` | Keep metadata aligned with runtime behavior as features evolve |
| `MrWhoOidc.Auth/Services/Authentication/ClientAuthenticationService.cs` | Maintain expanded token-endpoint mTLS auth behavior |
| `MrWhoOidc.Auth/Services/Token/RefreshTokenExchanger.cs` | Maintain refresh-family and DPoP-bound refresh enforcement |

### Medium Priority

| File | Changes |
|------|---------|
| `MrWhoOidc.Auth/Services/Token/AuthorizationCodeExchanger.cs` | Continue hardening claims/resource validation and constraints |
| `MrWhoOidc.Auth/Services/Token/RefreshTokenExchanger.cs` | Continue resource override and family-lineage safeguards |
| `MrWhoOidc.Auth/Services/AuthorizeRequestValidator.cs` | Stricter resource validation |

---

## Overall Assessment

### Scoring

| Category | Score | Notes |
|----------|-------|-------|
| Core OIDC Compliance | 90/100 | Pairwise/frontchannel and grant support are strong |
| Security Implementation | 95/100 | Excellent DPoP |
| Specification Accuracy | 89/100 | Major mTLS/resource/endpoint consistency gaps were closed |
| Code Quality | 90/100 | Clean architecture |
| Documentation | 80/100 | Good README, needs spec docs |
| **Overall** | **90/100** | **A-** |

### Deployment Readiness

| Scenario | Ready | Notes |
|----------|-------|-------|
| Standard OIDC integration | Yes | Authorization code, client credentials |
| High-security environment | Yes | With DPoP enabled |
| Token delegation/OBO | Yes | With proper client configuration |
| Multi-tenant SaaS | Yes | Built-in support |
| Federated identity | Yes | IdP chaining supported |
| Zero-trust architecture | Yes | DPoP provides strong binding |

---

## Conclusion

MrWhoOidc is a **production-ready OIDC provider** with excellent security fundamentals. The implementation demonstrates strong understanding of OAuth/OIDC security, particularly in DPoP and token exchange.

**Primary recommendation**: Continue expanding conformance test automation (OIDF profiles + critical RFC edge cases).

**Secondary recommendation**: Keep discovery metadata, grant-handler validation, and tenant token settings regression-tested together to prevent drift.

**Long-term**: Pursue OIDF conformance certification for formal compliance verification.
