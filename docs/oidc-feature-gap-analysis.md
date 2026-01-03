# OIDC Feature Gap Analysis and Implementation Roadmap

**Date:** 2026-01-03  
**Version:** 1.0  
**Status:** In progress (partially implemented)

## Implementation Status (Repository Reality)

The following items in this roadmap have been implemented since this document was first drafted:

- ✅ OIDC `claims` parameter: parsing/validation + persistence in auth-code request context, and userinfo claim filtering driven by the access token.
- ✅ OIDC `claims` parameter constraints: `essential` + `value`/`values` are enforced for **ID tokens** (at `/token`) and for **UserInfo** responses (at `/userinfo`) using constraints embedded in the access token.
- ✅ OIDC `prompt` enforcement in `/authorize` (including `prompt=none` error semantics).
- ✅ OIDC `max_age` enforcement in `/authorize` using `auth_time` from the user session.
- ✅ OIDC `acr_values` (basic validation + best-effort session satisfaction) and discovery support via `acr_values_supported` when configured.

All tests are currently green (`dotnet test` on the solution).

## Executive Summary

This document analyzes the MrWhoOidc discovery document against the OpenID Connect specifications to identify gaps and propose implementations. The server already has excellent coverage of core and advanced OIDC features, but several metadata items and optional features would improve interoperability and compliance.

---

## Current Feature Matrix

### ✅ Already Implemented (Excellent Coverage)

| Category | Feature | Spec Reference |
|----------|---------|----------------|
| **Core** | Authorization Endpoint | OIDC Core 1.0 |
| **Core** | Token Endpoint | OIDC Core 1.0 |
| **Core** | UserInfo Endpoint | OIDC Core 1.0 |
| **Core** | JWKS Endpoint | OIDC Core 1.0 |
| **Core** | Discovery (.well-known) | OIDC Discovery 1.0 |
| **Logout** | End Session Endpoint | OIDC Session 1.0 |
| **Logout** | Front-channel Logout | OIDC Front-Channel 1.0 |
| **Logout** | Back-channel Logout | OIDC Back-Channel 1.0 |
| **Security** | PKCE (S256) | RFC 7636 |
| **Security** | JAR (Request Objects) | RFC 9101 |
| **Security** | PAR (Pushed Auth Requests) | RFC 9126 |
| **Security** | DPoP | RFC 9449 |
| **Security** | mTLS (partial) | RFC 8705 |
| **Security** | JARM (JWT Auth Response) | JARM |
| **OAuth** | Token Introspection | RFC 7662 |
| **OAuth** | Token Revocation | RFC 7009 |
| **OAuth** | Token Exchange | RFC 8693 |
| **OAuth** | Resource Indicators | RFC 8707 |
| **Privacy** | Pairwise Subject IDs | OIDC Core 1.0 |
| **Auth** | private_key_jwt | OIDC Core 1.0 |

---

## Gap Analysis

### Phase 1: Discovery Metadata Improvements (LOW EFFORT - High Value)

These items only require updating the DiscoveryHandler to advertise existing or easily-added capabilities.

#### 1.1 ACR Values Support
**Priority:** HIGH  
**Effort:** 2-4 hours  
**Spec:** OIDC Core 1.0 §2, §3.1.2.1

**Current State:** Implemented: `acr_values_supported` is advertised when configured; `/authorize` enforces `acr_values` best-effort.

**Implementation:**
```csharp
// In DiscoveryHandler.cs
["acr_values_supported"] = new[] { "urn:mrwho:acr:bronze", "urn:mrwho:acr:silver", "urn:mrwho:acr:gold" }
```

**Tasks:**
- [ ] Define ACR value taxonomy (bronze/silver/gold or aal1/aal2/aal3)
- [ ] Map ACR values to authentication methods
- [x] Add `acr_values_supported` to discovery
- [ ] Return actual `acr` claim in ID token based on auth method used
- [x] Add tests for ACR enforcement

---

#### 1.2 Display Values Support
**Priority:** MEDIUM  
**Effort:** 1-2 hours  
**Spec:** OIDC Core 1.0 §3.1.2.1

**Implementation:**
```csharp
["display_values_supported"] = new[] { "page", "popup" }
```

**Tasks:**
- [x] Add `display_values_supported` to discovery
- [ ] Verify login pages respect `display` parameter for mobile/popup styling

---

#### 1.3 Claim Types Supported
**Priority:** MEDIUM  
**Effort:** 30 minutes  
**Spec:** OIDC Discovery 1.0

**Implementation:**
```csharp
["claim_types_supported"] = new[] { "normal" }
```

**Current State:** Implemented in discovery.

---

#### 1.4 UI Locales Supported
**Priority:** MEDIUM  
**Effort:** 1 hour  
**Spec:** OIDC Core 1.0 §3.1.2.1

**Implementation:**
```csharp
["ui_locales_supported"] = new[] { "en", "en-US" }
```

**Notes:** Extend when additional language packs are added.

**Current State:** Implemented in discovery when `AuthOptions.UiLocalesSupported` is configured (omitted when empty).

---

#### 1.5 Service Documentation URLs
**Priority:** LOW  
**Effort:** 30 minutes  
**Spec:** OIDC Discovery 1.0

**Implementation:**
```csharp
["service_documentation"] = "https://docs.mrwho.local/oidc",
["op_policy_uri"] = "https://mrwho.local/privacy",
["op_tos_uri"] = "https://mrwho.local/terms"
```

**Current State:** Implemented in discovery via `AuthOptions.ServiceDocumentationUrl`, `AuthOptions.OpPolicyUrl`, and `AuthOptions.OpTosUrl` (omitted when not configured).

---

### Phase 2: ID Token & UserInfo Signing Improvements (MEDIUM EFFORT)

#### 2.1 Expand ID Token Signing Algorithms
**Priority:** HIGH (FIPS compliance)  
**Effort:** 4-8 hours  
**Spec:** OIDC Core 1.0 §8

**Current:** Only RS256  
**Target:** RS256, ES256, ES384, ES512, PS256

**Tasks:**
- [ ] Update key rotation to support EC keys for ID tokens
- [ ] Allow client registration to specify preferred algorithm
- [ ] Use client's `id_token_signed_response_alg` claim
- [ ] Update discovery: `["id_token_signing_alg_values_supported"]`
- [ ] Add tests for non-RS256 ID tokens

---

#### 2.2 ID Token Encryption Support
**Priority:** MEDIUM  
**Effort:** 2-3 days  
**Spec:** OIDC Core 1.0 §8

**Implementation:**
```csharp
["id_token_encryption_alg_values_supported"] = new[] { "RSA-OAEP", "A256KW" },
["id_token_encryption_enc_values_supported"] = new[] { "A256GCM", "A128CBC-HS256" }
```

**Tasks:**
- [ ] Add `id_token_encrypted_response_alg` to Client entity
- [ ] Add `id_token_encrypted_response_enc` to Client entity
- [ ] Store client's JWK or JWKs URI for encryption key
- [ ] Implement JWE generation in TokenService
- [ ] Add discovery metadata
- [ ] Add tests

---

#### 2.3 UserInfo Endpoint Signing & Encryption
**Priority:** LOW  
**Effort:** 2-3 days  
**Spec:** OIDC Core 1.0 §5.3.2

**Implementation:**
```csharp
["userinfo_signing_alg_values_supported"] = new[] { "RS256", "ES256", "none" },
["userinfo_encryption_alg_values_supported"] = new[] { "RSA-OAEP" },
["userinfo_encryption_enc_values_supported"] = new[] { "A256GCM" }
```

**Tasks:**
- [ ] Add `userinfo_signed_response_alg` to Client entity
- [ ] Add `userinfo_encrypted_response_alg/enc` to Client entity
- [ ] Modify UserInfoHandler to return JWT when requested
- [ ] Support nested JWE for signed+encrypted responses
- [ ] Add discovery metadata
- [ ] Add tests

---

### Phase 3: Claims Request Parameter (MEDIUM EFFORT)

#### 3.1 Claims Parameter Support
**Priority:** HIGH (enables fine-grained claim requests)  
**Effort:** 1-2 days  
**Spec:** OIDC Core 1.0 §5.5

**Implementation:**
```csharp
["claims_parameter_supported"] = true
```

**Current State:** Implemented: claims JSON is parsed/validated, persisted with the authorization code context, and used to filter UserInfo claims.

**Tasks:**
- [x] Parse claims JSON object from authorize request
- [x] Validate claims request structure
- [x] Filter ID token claims based on request (implemented behind `AuthOptions.RestrictIdTokenClaimsToClaimsRequest`; default `false`)
- [x] Filter userinfo claims based on request
- [x] Support `essential` vs optional (ID token + userinfo)
- [x] Support `value` and `values` constraints (ID token + userinfo)
- [x] Add discovery metadata
- [x] Add comprehensive tests

**Example Claims Request:**
```json
{
  "id_token": {
    "auth_time": {"essential": true},
    "acr": {"values": ["urn:mrwho:acr:gold"]}
  },
  "userinfo": {
    "email": {"essential": true},
    "picture": null
  }
}
```

---

### Phase 4: mTLS Enhancements (MEDIUM EFFORT)

#### 4.1 Advertise mTLS Support in Discovery
**Priority:** HIGH  
**Effort:** 1-2 hours  
**Spec:** RFC 8705

**Current State:** mTLS is implemented but not advertised.

**Status (implemented):** Discovery now advertises `self_signed_tls_client_auth` for token + introspection, and the token endpoint supports mTLS-only client auth for `client_credentials` when the client has an allow-list of certificate thumbprints.

**Implementation (current direction):**
```csharp
// We support self-signed mTLS client authentication (thumbprint allow-list),
// but we do not currently issue TLS certificate-bound access tokens.
["token_endpoint_auth_methods_supported"] = new[] { 
  "client_secret_basic", 
  "client_secret_post", 
  "private_key_jwt",
  "self_signed_tls_client_auth"
},
["introspection_endpoint_auth_methods_supported"] = new[] { 
  "client_secret_basic", 
  "client_secret_post", 
  "private_key_jwt",
  "self_signed_tls_client_auth"
}
```

**Tasks:**

- [x] Add `self_signed_tls_client_auth` to auth methods
- [ ] Add `tls_client_auth` if/when subject/SAN based mapping is implemented
- [x] Configure mTLS base URL in options (only needed for `mtls_endpoint_aliases`)
- [x] Add mtls_endpoint_aliases to discovery (operator-configured)
- [x] Document mTLS setup requirements

**mTLS setup (operator notes):**

- Ensure the server (or your reverse proxy) is configured to request/require client certificates for the mTLS-protected host/alias.
- If running behind a proxy, enable certificate forwarding so `HttpContext.Connection.ClientCertificate` is populated.
- Configure allow-lists:
  - Token endpoint (client_credentials): per-client allow-list stored on the Client record (`M2MMtlsThumbprintsJson`).
  - Introspection: `Auth:IntrospectionMtlsCertificates` (or per-client DB field `IntrospectionMtlsThumbprintsJson`).
  - Revocation: `Auth:RevocationMtlsCertificates`.
- Thumbprint formats supported for allow-lists: RFC 8705 `x5t#S256` (base64url) or SHA-256 hex fingerprint.
- Optional discovery aliases: set `Auth:MtlsEndpointAliasesBaseUrl` (absolute URL) to emit `mtls_endpoint_aliases` pointing to `/token`, `/introspect`, `/revoke` under that base. The alias host should be the one that enforces client certificates.

---

### Phase 5: Session Management (MEDIUM EFFORT)

#### 5.1 Check Session iFrame
**Priority:** LOW (being deprecated)  
**Effort:** 2-3 days  
**Spec:** OIDC Session 1.0 §4

**Note:** This feature is being phased out in favor of back-channel logout. Consider LOW priority.

**Implementation:**
```csharp
["check_session_iframe"] = $"{baseUrl}/connect/checksession"
```

**Tasks:**
- [ ] Create check_session endpoint serving iFrame
- [ ] Implement session state calculation (`session_state` response parameter)
- [ ] Handle postMessage for session polling
- [ ] Add discovery metadata
- [ ] Add tests

---

### Phase 6: Request Object Encryption (LOW EFFORT)

#### 6.1 JAR Encryption Support
**Priority:** LOW  
**Effort:** 1-2 days  
**Spec:** RFC 9101 §6

**Implementation:**
```csharp
["request_object_encryption_alg_values_supported"] = new[] { "RSA-OAEP" },
["request_object_encryption_enc_values_supported"] = new[] { "A256GCM" }
```

**Tasks:**
- [ ] Generate and publish OP encryption key in JWKS
- [ ] Decrypt incoming JAR request objects
- [ ] Add discovery metadata
- [ ] Add tests

---

### Phase 7: Advanced Features (HIGH EFFORT)

#### 7.1 Dynamic Client Registration
**Priority:** MEDIUM  
**Effort:** 1-2 weeks  
**Spec:** RFC 7591, RFC 7592

**Implementation:**
```csharp
["registration_endpoint"] = $"{baseUrl}/register"
```

**Tasks:**
- [ ] Create `/register` POST endpoint for initial registration
- [ ] Implement client metadata validation per spec
- [ ] Generate `registration_access_token`
- [ ] Create client configuration endpoint for updates
- [ ] Add software statement support
- [ ] Implement initial access token protection (optional)
- [ ] Add rate limiting
- [ ] Add tests

---

#### 7.2 Device Authorization Grant
**Priority:** MEDIUM (IoT/CLI scenarios)  
**Effort:** 1 week  
**Spec:** RFC 8628

**Implementation:**
```csharp
["device_authorization_endpoint"] = $"{baseUrl}/device"
```

**Grant Type:**
```csharp
grants.Add("urn:ietf:params:oauth:grant-type:device_code");
```

**Tasks:**
- [ ] Create device authorization endpoint
- [ ] Generate device_code and user_code
- [ ] Create user verification page
- [ ] Implement polling token endpoint logic
- [ ] Handle slow_down and authorization_pending errors
- [ ] Add configurable code expiration
- [ ] Add tests

---

#### 7.3 CIBA (Client Initiated Backchannel Authentication)
**Priority:** LOW (specialized use case)  
**Effort:** 2-3 weeks  
**Spec:** OpenID Connect CIBA

**Implementation:**
```csharp
["backchannel_authentication_endpoint"] = $"{baseUrl}/bc-authorize",
["backchannel_token_delivery_modes_supported"] = new[] { "poll", "ping" },
["backchannel_authentication_request_signing_alg_values_supported"] = new[] { "ES256", "RS256" }
```

**Tasks:**
- [ ] Create backchannel authentication endpoint
- [ ] Implement push notification integration
- [ ] Create user consent collection flow
- [ ] Implement poll mode token retrieval
- [ ] Implement ping mode callback
- [ ] Add binding_message support
- [ ] Add tests

---

## Implementation Priority Matrix

| Phase | Feature | Priority | Effort | Business Value |
|-------|---------|----------|--------|----------------|
| 1.1 | ACR Values Support | HIGH | Low | High - Compliance |
| 1.2 | Display Values | MEDIUM | Low | Medium - UX |
| 1.3 | Claim Types | MEDIUM | Trivial | Medium - Compliance |
| 1.4 | UI Locales | MEDIUM | Low | Medium - i18n |
| 1.5 | Service Docs URLs | LOW | Trivial | Low - DX |
| 2.1 | ID Token Alg Expansion | HIGH | Medium | High - FIPS |
| 2.2 | ID Token Encryption | MEDIUM | Medium | Medium - Privacy |
| 2.3 | UserInfo Signing/Enc | LOW | Medium | Low - Rare use |
| 3.1 | Claims Parameter | HIGH | Medium | High - Flexibility |
| 4.1 | mTLS Discovery | HIGH | Low | High - Enterprise |
| 5.1 | Check Session iFrame | LOW | Medium | Low - Deprecated |
| 6.1 | Request Object Enc | LOW | Medium | Low - Rare use |
| 7.1 | Dynamic Registration | MEDIUM | High | Medium - Automation |
| 7.2 | Device Auth Grant | MEDIUM | High | Medium - IoT/CLI |
| 7.3 | CIBA | LOW | Very High | Low - Specialized |

---

## Recommended Implementation Order

### Sprint 1 (Quick Wins - 1 Week)
1. Add `acr_values_supported` with ACR taxonomy
2. Add `display_values_supported`
3. Add `claim_types_supported`
4. Add `ui_locales_supported`
5. Add service documentation URLs
6. Advertise mTLS support properly

### Sprint 2 (Security & Compliance - 2 Weeks)
1. Expand ID token signing algorithms (ES256, PS256)
2. Implement full ACR claim emission based on auth method
3. Implement claims parameter support

### Sprint 3 (Advanced Security - 2 Weeks)
1. ID token encryption
2. Request object encryption for JAR

### Sprint 4 (Enterprise Features - 2 Weeks)
1. Device Authorization Grant
2. Dynamic Client Registration

### Future Consideration
- CIBA (only if customer demand)
- Check Session iFrame (only if legacy RP support needed)
- UserInfo signing/encryption (only if customer demand)

---

## Discovery Handler Changes Required

```csharp
// Phase 1 additions to DiscoveryHandler.cs body dictionary:

// ACR
["acr_values_supported"] = new[] { 
    "urn:mrwho:acr:mfa", 
    "urn:mrwho:acr:password",
    "urn:mrwho:acr:passkey" 
},

// Display & UI
["display_values_supported"] = new[] { "page", "popup" },
["claim_types_supported"] = new[] { "normal" },
["ui_locales_supported"] = new[] { "en" },

// Documentation (from config)
["service_documentation"] = authOptions.Value.ServiceDocumentationUrl,
["op_policy_uri"] = authOptions.Value.PrivacyPolicyUrl,
["op_tos_uri"] = authOptions.Value.TermsOfServiceUrl,

// Claims parameter
["claims_parameter_supported"] = authOptions.Value.EnableClaimsParameter,

// mTLS
["tls_client_certificate_bound_access_tokens"] = true,
```

---

## Testing Strategy

Each feature should include:
1. Unit tests for new services/handlers
2. Integration tests for endpoint behavior
3. Discovery metadata validation tests
4. Interoperability tests with common RPs (if applicable)

---

## References

- [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html)
- [OIDC Discovery 1.0](https://openid.net/specs/openid-connect-discovery-1_0.html)
- [OIDC Session 1.0](https://openid.net/specs/openid-connect-session-1_0.html)
- [RFC 7591 - Dynamic Client Registration](https://tools.ietf.org/html/rfc7591)
- [RFC 8628 - Device Authorization Grant](https://tools.ietf.org/html/rfc8628)
- [RFC 8705 - OAuth 2.0 Mutual-TLS](https://tools.ietf.org/html/rfc8705)
- [OpenID Connect CIBA](https://openid.net/specs/openid-client-initiated-backchannel-authentication-core-1_0.html)
