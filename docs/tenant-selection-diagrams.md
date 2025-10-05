# Tenant Selection Login Flow - Visual Diagrams

## Current State (Problem)

```mermaid
graph TD
    A[User wants to log in] --> B{Knows tenant slug?}
    B -->|No| C[Contacts support/searches docs]
    C --> D[Gets tenant URL: /t/acme/login]
    D --> E[Navigates to URL]
    E --> F[Enters credentials]
    F --> G[Authenticated ✓]
    
    B -->|Yes| H[Goes directly to /t/slug/login]
    H --> F
    
    style C fill:#ffcccc
    style D fill:#ffcccc
    style A fill:#e1f5ff
    style G fill:#ccffcc
```

**Problems:**
- Users don't know their tenant slug
- Support overhead
- Poor first-time experience
- Easy to make typos

---

## Proposed Solution (Email-First Flow)

```mermaid
graph TD
    A[User lands on /login] --> B[Enters email address]
    B --> C[System queries database]
    
    C --> D{How many tenants?}
    
    D -->|0 tenants| E[Show: No account found]
    E --> F[Offer registration link]
    
    D -->|1 tenant| G[Auto-redirect to /t/acme/login]
    G --> H[Email pre-filled]
    H --> I[User enters password]
    I --> J[Authenticated ✓]
    
    D -->|2+ tenants| K[Show tenant selection page]
    K --> L[Display tenant cards with logos]
    L --> M[User selects organization]
    M --> G
    
    style A fill:#e1f5ff
    style J fill:#ccffcc
    style E fill:#ffffcc
    style K fill:#fff5e1
```

**Benefits:**
- Simple, familiar UX (email-first)
- No need to remember tenant slugs
- Works for single and multi-tenant users
- Automatic tenant discovery

---

## Detailed Flow with Security

```mermaid
sequenceDiagram
    participant User
    participant Browser
    participant WebAuth
    participant TenantDiscoveryService
    participant Database
    
    User->>Browser: Navigate to /login
    Browser->>WebAuth: GET /login
    WebAuth-->>Browser: Email input page
    
    User->>Browser: Enter email + Submit
    Browser->>WebAuth: POST /login (email)
    
    WebAuth->>WebAuth: Check rate limit (5/min per IP)
    alt Rate limit exceeded
        WebAuth-->>Browser: 429 Too Many Requests
    end
    
    WebAuth->>TenantDiscoveryService: FindTenantsByEmail(email)
    TenantDiscoveryService->>Database: Query Users by email
    TenantDiscoveryService->>Database: Query AlternativeEmails
    Database-->>TenantDiscoveryService: List of tenants
    
    TenantDiscoveryService->>TenantDiscoveryService: Audit log discovery attempt
    TenantDiscoveryService-->>WebAuth: Tenant list
    
    alt 0 tenants found
        WebAuth-->>Browser: Error: No account found
        Browser-->>User: Show error + registration link
    end
    
    alt 1 tenant found
        WebAuth-->>Browser: 302 Redirect to /t/acme/login?email=...
        Browser->>WebAuth: GET /t/acme/login
        WebAuth-->>Browser: Login page with email pre-filled
        User->>Browser: Enter password
        Browser->>WebAuth: POST /t/acme/login
        WebAuth-->>Browser: Authenticated ✓
    end
    
    alt 2+ tenants found
        WebAuth-->>Browser: 302 Redirect to /select-tenant
        Browser->>WebAuth: GET /select-tenant
        WebAuth-->>Browser: Tenant selection page
        User->>Browser: Click tenant card
        Browser->>WebAuth: POST /select-tenant
        WebAuth-->>Browser: 302 Redirect to /t/acme/login?email=...
        Browser->>WebAuth: GET /t/acme/login
        WebAuth-->>Browser: Login page with email pre-filled
    end
```

---

## Database Query Flow

```mermaid
graph LR
    A[User Email: admin@example.com] --> B[Normalize Email]
    B --> C{Query Database}
    
    C --> D[Query 1: Users Table]
    D --> D1[SELECT * FROM Users<br/>WHERE NormalizedEmail = @email<br/>AND TenantId IN<br/>SELECT Id FROM Tenants<br/>WHERE Status = Active]
    
    C --> E[Query 2: Alternative Emails]
    E --> E1[SELECT * FROM UserAlternativeEmails<br/>WHERE NormalizedEmail = @email<br/>AND IsVerified = true]
    
    D1 --> F[Join with Tenants]
    E1 --> F
    
    F --> G[Distinct Tenant List]
    G --> H[Return: Slug, Name, LogoUrl]
    
    style A fill:#e1f5ff
    style H fill:#ccffcc
```

---

## UI Component Hierarchy

```
┌─────────────────────────────────────────────────────┐
│ TenantDiscovery.cshtml (Root /login)                │
│                                                     │
│  ┌──────────────────────────────────────────────┐  │
│  │ Email Input Form                             │  │
│  │  - Email address field (required)            │  │
│  │  - Continue button                           │  │
│  │  - Client-side validation                    │  │
│  └──────────────────────────────────────────────┘  │
│                                                     │
│  On Submit:                                         │
│  - POST to TenantDiscoveryService                   │
│  - Store email in TempData                          │
│  - Redirect based on tenant count                   │
└─────────────────────────────────────────────────────┘

                    ↓ (if 2+ tenants)

┌─────────────────────────────────────────────────────┐
│ SelectTenant.cshtml (/select-tenant)                │
│                                                     │
│  ┌──────────────────────────────────────────────┐  │
│  │ Tenant Selection Grid                        │  │
│  │                                              │  │
│  │  ┌────────────────────────────────────────┐ │  │
│  │  │ TenantCard Component                   │ │  │
│  │  │  - Tenant Logo (or default icon)       │ │  │
│  │  │  - Tenant Name (bold)                  │ │  │
│  │  │  - Tenant Slug (muted)                 │ │  │
│  │  │  - Last Login (if available)           │ │  │
│  │  │  - "Select" button                     │ │  │
│  │  └────────────────────────────────────────┘ │  │
│  │                                              │  │
│  │  (Repeat for each tenant)                    │  │
│  │                                              │  │
│  └──────────────────────────────────────────────┘  │
│                                                     │
│  ┌──────────────────────────────────────────────┐  │
│  │ Options                                      │  │
│  │  - [x] Remember my choice                    │  │
│  │  - ← Back to email entry                     │  │
│  └──────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────┘

                    ↓ (on selection)

┌─────────────────────────────────────────────────────┐
│ Login.cshtml (/t/acme/login?email=...)              │
│                                                     │
│  ┌──────────────────────────────────────────────┐  │
│  │ Login Form                                   │  │
│  │  - Email (pre-filled, readonly)              │  │
│  │  - Password field                            │  │
│  │  - Sign In button                            │  │
│  │  - "Not you?" link (back to discovery)       │  │
│  └──────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────┘
```

---

## State Machine

```mermaid
stateDiagram-v2
    [*] --> EmailInput: User visits /login
    
    EmailInput --> RateLimitCheck: Submit email
    
    RateLimitCheck --> TooManyRequests: Limit exceeded
    TooManyRequests --> EmailInput: Wait/CAPTCHA
    
    RateLimitCheck --> QueryDatabase: Within limit
    
    QueryDatabase --> NoTenants: 0 results
    QueryDatabase --> OneTenant: 1 result
    QueryDatabase --> MultipleTenants: 2+ results
    
    NoTenants --> EmailInput: Show error
    
    OneTenant --> TenantLogin: Auto-redirect
    
    MultipleTenants --> TenantSelection: Show cards
    TenantSelection --> TenantLogin: User selects
    TenantSelection --> EmailInput: Back button
    
    TenantLogin --> Authenticated: Valid credentials
    TenantLogin --> TenantLogin: Invalid credentials
    
    Authenticated --> [*]
```

---

## Data Flow Architecture

```
┌──────────────────────────────────────────────────────────┐
│                    Presentation Layer                     │
│                                                           │
│  ┌─────────────────┐  ┌─────────────────┐  ┌──────────┐ │
│  │ TenantDiscovery │  │ SelectTenant    │  │  Login   │ │
│  │    (Razor)      │  │    (Razor)      │  │ (Razor)  │ │
│  └────────┬────────┘  └────────┬────────┘  └─────┬────┘ │
│           │                     │                  │      │
└───────────┼─────────────────────┼──────────────────┼──────┘
            │                     │                  │
            ↓                     ↓                  ↓
┌──────────────────────────────────────────────────────────┐
│                    Business Logic Layer                   │
│                                                           │
│  ┌─────────────────────────────────────────────────────┐ │
│  │        ITenantDiscoveryService                      │ │
│  │  ┌───────────────────────────────────────────────┐ │ │
│  │  │ FindTenantsByEmailAsync(email)                │ │ │
│  │  │ GetPreferredTenantAsync(email, ip)            │ │ │
│  │  │ SaveTenantPreferenceAsync(email, tenantId)    │ │ │
│  │  └───────────────────────────────────────────────┘ │ │
│  └─────────────────────────────────────────────────────┘ │
│           │                                               │
│           ├── Rate Limiter (5 req/min per IP)            │
│           ├── Audit Logger                                │
│           └── Cache (5 min TTL)                           │
│                                                           │
└───────────┼───────────────────────────────────────────────┘
            ↓
┌──────────────────────────────────────────────────────────┐
│                      Data Access Layer                    │
│                                                           │
│  ┌─────────────────────────────────────────────────────┐ │
│  │              AuthDbContext                          │ │
│  │                                                     │ │
│  │  Users ←──────┐                                    │ │
│  │  UserAlternativeEmails                             │ │
│  │  Tenants                                           │ │
│  │                                                     │ │
│  │  Query: Users JOIN Tenants WHERE email = @email    │ │
│  │  Query: AltEmails JOIN Users JOIN Tenants          │ │
│  └─────────────────────────────────────────────────────┘ │
│                                                           │
└───────────┼───────────────────────────────────────────────┘
            ↓
     [PostgreSQL Database]
```

---

## Caching Strategy

```mermaid
graph TD
    A[Request: FindTenantsByEmail] --> B{Cache Hit?}
    
    B -->|Yes| C[Return from Cache]
    C --> D[Log: Cache Hit]
    
    B -->|No| E[Query Database]
    E --> F[Process Results]
    F --> G[Store in Cache<br/>TTL: 5 minutes]
    G --> H[Return Results]
    H --> I[Log: Cache Miss]
    
    J[User Changes Tenant] --> K[Invalidate Cache<br/>Key: email_hash]
    
    style C fill:#ccffcc
    style E fill:#ffffcc
```

**Cache Keys:**
- `tenant_discovery:{email_hash}` → List of TenantInfo
- `tenant_preference:{email_hash}` → Preferred tenant slug
- TTL: 5 minutes (balance between freshness and performance)

**Invalidation Triggers:**
- User created in new tenant
- User deleted from tenant
- Tenant suspended/deleted
- Alternative email verified

---

## Security Layers

```
┌────────────────────────────────────────────────┐
│          Layer 1: Rate Limiting                │
│  - 5 requests per minute per IP                │
│  - Redis-backed distributed counter            │
│  - 429 Too Many Requests response              │
└────────────────────────────────────────────────┘
                    ↓
┌────────────────────────────────────────────────┐
│        Layer 2: Input Validation               │
│  - Email format validation                     │
│  - Email normalization (lowercase, trim)       │
│  - Reject invalid/malicious input              │
└────────────────────────────────────────────────┘
                    ↓
┌────────────────────────────────────────────────┐
│       Layer 3: Generic Responses               │
│  - Same response time for all queries          │
│  - Don't distinguish "no email" vs "no tenant" │
│  - Artificial delay if query < threshold       │
└────────────────────────────────────────────────┘
                    ↓
┌────────────────────────────────────────────────┐
│         Layer 4: Audit Logging                 │
│  - Log all discovery attempts                  │
│  - Include: IP, email (hashed), timestamp      │
│  - Alert on suspicious patterns                │
└────────────────────────────────────────────────┘
                    ↓
┌────────────────────────────────────────────────┐
│     Layer 5: CAPTCHA (Optional)                │
│  - After 3 failed attempts                     │
│  - Proof-of-work challenge                     │
│  - Google reCAPTCHA or hCaptcha                │
└────────────────────────────────────────────────┘
```

---

## Performance Considerations

### Query Optimization

```sql
-- Optimized query with proper indexes
-- Index: Users(TenantId, NormalizedEmail)
-- Index: UserAlternativeEmails(NormalizedEmail, IsVerified)
-- Index: Tenants(Status, Id)

WITH tenant_from_primary AS (
    SELECT DISTINCT t."Id", t."Slug", t."Name", t."LogoUrl"
    FROM "Tenants" t
    INNER JOIN "Users" u ON u."TenantId" = t."Id"
    WHERE u."NormalizedEmail" = @email
      AND t."Status" = 1
),
tenant_from_alternative AS (
    SELECT DISTINCT t."Id", t."Slug", t."Name", t."LogoUrl"
    FROM "Tenants" t
    INNER JOIN "Users" u ON u."TenantId" = t."Id"
    INNER JOIN "UserAlternativeEmails" uae ON uae."UserId" = u."Id"
    WHERE uae."NormalizedEmail" = @email
      AND uae."IsVerified" = true
      AND t."Status" = 1
)
SELECT * FROM tenant_from_primary
UNION
SELECT * FROM tenant_from_alternative
ORDER BY "Name";
```

**Expected Performance:**
- Query time: < 10ms (with proper indexes)
- Cache hit rate: 80%+ (5-minute TTL)
- Total latency: < 200ms p95 (including cache, rate limit, audit)

---

## Mobile Responsive Design

```
Desktop (> 768px):
┌─────────────────────────────────────┐
│         Sign in to Acme Corp        │
│                                     │
│  Email: admin@example.com (readonly)│
│  Password: ••••••••                 │
│                                     │
│          [Sign In →]                │
│                                     │
│  Not you? Switch organization       │
└─────────────────────────────────────┘

Mobile (< 768px):
┌───────────────┐
│  Acme Corp    │
│               │
│ Email:        │
│ admin@ex...   │
│               │
│ Password:     │
│ •••••••••     │
│               │
│  [Sign In]    │
│               │
│ Not you?      │
│ Switch org    │
└───────────────┘
```

---

## Error Handling

```mermaid
graph TD
    A[Error Occurs] --> B{Error Type}
    
    B -->|No tenants found| C[Show: No account found<br/>Offer: Register link]
    
    B -->|Rate limit exceeded| D[Show: Too many attempts<br/>Offer: Try again in X minutes]
    
    B -->|Database error| E[Show: Service temporarily unavailable<br/>Log: Error details<br/>Alert: Ops team]
    
    B -->|Invalid email format| F[Show: Please enter valid email<br/>Highlight: Email field]
    
    B -->|Network error| G[Show: Connection problem<br/>Offer: Retry button]
    
    style C fill:#ffffcc
    style D fill:#ffcccc
    style E fill:#ffcccc
    style F fill:#ffffcc
    style G fill:#ffffcc
```

---

## Accessibility (WCAG 2.1 AA)

**Email Input Page:**
- ✅ Label associated with input: `<label for="email">`
- ✅ Required field marked: `aria-required="true"`
- ✅ Error messages: `aria-describedby="email-error"`
- ✅ Keyboard navigation: Tab → Enter to submit
- ✅ Screen reader: "Email address, required field"

**Tenant Selection:**
- ✅ Heading hierarchy: H1 → H2 → H3
- ✅ Tenant cards: `role="button"` with `aria-label`
- ✅ Keyboard: Arrow keys to navigate, Enter to select
- ✅ Focus visible: Clear outline on focused card
- ✅ Screen reader: "Select Acme Corporation, button"

**Color Contrast:**
- Text: 4.5:1 minimum ratio
- Large text: 3:1 minimum ratio
- Interactive elements: Clear focus indicators

---

## Internationalization (i18n)

```csharp
// Resource keys for localization
"Login.EmailLabel" → "Email address" (en-US)
                  → "Adresse e-mail" (fr-FR)
                  → "E-Mail-Adresse" (de-DE)

"Login.Continue" → "Continue" (en-US)
              → "Continuer" (fr-FR)
              → "Weiter" (de-DE)

"SelectTenant.Title" → "Select your organization" (en-US)
                    → "Sélectionnez votre organisation" (fr-FR)
                    → "Wählen Sie Ihre Organisation" (de-DE)
```

---

## Analytics & Monitoring

```mermaid
graph LR
    A[User Action] --> B[Frontend Event]
    B --> C{Event Type}
    
    C --> D[Page View: /login]
    C --> E[Email Submitted]
    C --> F[Tenant Selected]
    C --> G[Login Successful]
    C --> H[Error Occurred]
    
    D --> I[Application Insights]
    E --> I
    F --> I
    G --> I
    H --> I
    
    I --> J[Dashboards]
    I --> K[Alerts]
    I --> L[Custom Queries]
```

**Key Metrics:**
- Conversion funnel: Email → Tenant → Login
- Drop-off rates at each step
- Error rates by type
- Average time-to-login
- Cache hit rate
- Rate limit triggers

---

## Future Enhancements Roadmap

```mermaid
gantt
    title Tenant Selection Feature Roadmap
    dateFormat  YYYY-MM-DD
    section Phase 1
    Email Discovery Service     :done, p1a, 2025-10-07, 7d
    Root Login Page            :done, p1b, after p1a, 7d
    section Phase 2
    Tenant Selection UI        :active, p2a, 2025-10-21, 7d
    Remember Preference        :p2b, after p2a, 7d
    section Phase 3
    Federated Discovery        :p3a, 2025-11-04, 14d
    Tenant Switching           :p3b, after p3a, 14d
    section Phase 4
    Cross-Tenant SSO           :p4a, 2025-12-02, 21d
    Mobile Deep Links          :p4b, after p4a, 14d
```

---

## Summary

This visual guide provides:
- ✅ Flow diagrams for user journeys
- ✅ Sequence diagrams for technical implementation
- ✅ State machines for business logic
- ✅ Architecture diagrams for system design
- ✅ UI mockups for development reference
- ✅ Security layers visualization
- ✅ Performance optimization strategies

Use these diagrams during:
- Design reviews
- Implementation planning
- Code reviews
- Documentation
- Training sessions
