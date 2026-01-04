# IntrospectionHandler Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                        HTTP Request                                 │
│                     POST /introspect                                │
└───────────────────────────┬─────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   IntrospectionHandler                              │
│  (Orchestrates the introspection flow)                              │
└───────────────┬───────────────────────────┬─────────────────────────┘
                │                           │
                ▼                           │
┌───────────────────────────────┐          │
│ IntrospectionRequestParser    │          │
│ - Parse form data             │          │
│ - Extract client credentials  │          │
│ - Build IntrospectionRequest  │          │
└───────────────┬───────────────┘          │
                │                           │
                ▼                           │
┌───────────────────────────────┐          │
│    IClientStore               │          │
│ - Load client by ID           │          │
└───────────────┬───────────────┘          │
                │                           │
                ▼                           ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   IntrospectionContext                              │
│  (Carries request state through the pipeline)                       │
│  - Request, Client, Issuer, Endpoint, HttpContext, Tags             │
└───────────────┬─────────────────────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   ClientAuthenticator                               │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │ 1. mTLS (certificate thumbprint validation)                 │   │
│  │ 2. private_key_jwt (JWT assertion validation)               │   │
│  │ 3. client_secret (BCrypt/Argon2 validation)                 │   │
│  └─────────────────────────────────────────────────────────────┘   │
└───────────────┬─────────────────────────────────────────────────────┘
                │
                ▼
        ┌───────────────┐
        │ Authenticated?│
        └───────┬───────┘
                │ Yes
                ▼
┌─────────────────────────────────────────────────────────────────────┐
│         Token Type Detection & Introspection                        │
│                                                                     │
│  Hint: refresh_token?                                               │
│         │                                                            │
│         ├─Yes──► RefreshTokenIntrospector                           │
│         │        └─► Owner-only check                               │
│         │                                                            │
│         └─No/Not Found                                              │
│                   │                                                  │
│                   ▼                                                  │
│         Try JWT validation                                          │
│         │                                                            │
│         ├─Valid──► JwtTokenIntrospector                             │
│         │          ├─► AudiencePolicy (check aud access)            │
│         │          ├─► DPoPValidator (if token bound)               │
│         │          │    ├─► Nonce validation                        │
│         │          │    ├─► JKT matching                            │
│         │          │    └─► Replay prevention                       │
│         │          └─► Build response claims                        │
│         │                                                            │
│         └─Invalid                                                   │
│                   │                                                  │
│                   ▼                                                  │
│         Try opaque token (DB lookup)                                │
│         │                                                            │
│         ├─Found──► OpaqueTokenIntrospector                          │
│         │          ├─► AudiencePolicy (check aud access)            │
│         │          ├─► Check revocation/expiry                      │
│         │          ├─► DPoPValidator (if token bound)               │
│         │          └─► Build response from DB entity                │
│         │                                                            │
│         └─Not Found                                                 │
│                   │                                                  │
│                   └─► Try RefreshTokenIntrospector (fallback)       │
│                                                                     │
└───────────────┬─────────────────────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   ResponseShaper                                    │
│  - Apply per-client field filtering                                 │
│  - Privacy policy enforcement                                       │
│  - Ensure 'active' claim always present                             │
└───────────────┬─────────────────────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────────────────────────────────┐
│              Cross-Cutting Concerns                                 │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ IntrospectionMetrics                                         │  │
│  │  - Record request count                                      │  │
│  │  - Record active/inactive results                            │  │
│  │  - Record duration                                           │  │
│  └──────────────────────────────────────────────────────────────┘  │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ IntrospectionAuditor                                         │  │
│  │  - Log client (bucketed)                                     │  │
│  │  - Log IP address                                            │  │
│  │  - Log outcome (active/inactive/forbidden)                   │  │
│  │  - Log audience                                              │  │
│  └──────────────────────────────────────────────────────────────┘  │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ IntrospectionExtensions                                      │  │
│  │  - ComputeTokenHash()                                        │  │
│  │  - BucketizeClientId()                                       │  │
│  │  - ToLongOrNull()                                            │  │
│  │  - CreateMetricTags()                                        │  │
│  └──────────────────────────────────────────────────────────────┘  │
└───────────────┬─────────────────────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    HTTP Response                                    │
│              JSON { active: true/false, ... }                       │
└─────────────────────────────────────────────────────────────────────┘
```

## Component Relationships

### Dependencies Flow
```
IntrospectionHandler
    ├─► IntrospectionRequestParser (static)
    ├─► IClientStore (from Auth core)
    ├─► ClientAuthenticator
    │   ├─► IClientStore
    │   ├─► IClientAssertionValidator
    │   └─► AuthOptions
    ├─► JwtTokenIntrospector
    │   ├─► ITokenValidator
    │   ├─► DPoPValidator
    │   ├─► AudiencePolicy
    │   └─► ResponseShaper
    ├─► OpaqueTokenIntrospector
    │   ├─► AuthDbContext
    │   ├─► DPoPValidator
    │   ├─► AudiencePolicy
    │   └─► ResponseShaper
    └─► RefreshTokenIntrospector
        ├─► AuthDbContext
        ├─► IClientStore
        ├─► ResponseShaper
        └─► AuthOptions

DPoPValidator
    ├─► IDPoPValidator (from Security)
    ├─► IDPoPReplayCache
    └─► IDPoPNonceStore
```

## Key Design Patterns

### 1. **Strategy Pattern**
Different introspection strategies for different token types:
- `JwtTokenIntrospector`
- `OpaqueTokenIntrospector`
- `RefreshTokenIntrospector`

### 2. **Chain of Responsibility**
Token type detection tries handlers in sequence:
1. Refresh token (if hinted)
2. JWT validation
3. Opaque token lookup
4. Refresh token fallback

### 3. **Decorator/Pipeline Pattern**
Response processing pipeline:
1. Introspect token → raw claims
2. Apply audience policy → filtered by access
3. Apply response shaping → filtered by privacy policy
4. Record metrics & audit → observability

### 4. **Repository Pattern**
- `IClientStore` - Client data access
- `AuthDbContext` - Token persistence

### 5. **Extension Method Pattern**
Common operations as fluent extensions:
```csharp
var hash = token.ComputeTokenHash();
var bucket = clientId.BucketizeClientId();
var tags = clientBucket.CreateMetricTags();
```

## Thread Safety
- All services registered as `Scoped` (per-request lifetime)
- No shared state between requests
- Database access through EF Core (handles concurrency)
- Replay cache is thread-safe (IMemoryCache)
