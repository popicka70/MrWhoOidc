# QR Code Login — Implementation Backlog

This document outlines the requirements, design considerations, and implementation tasks for adding QR code-based login functionality to MrWhoOidc. QR login enables users to authenticate by scanning a code displayed on a web browser using their mobile device, completing the login flow on the mobile device, and having the web browser session automatically authenticated.

Status legend
- [ ] Not started
- [~] In progress
- [x] Done

Updated: 2025-10-02

---

## Overview

### Problem statement
Users want to log in to web applications without entering credentials on the desktop browser. QR code login allows:
- Scanning a QR code displayed on the web browser using a mobile device
- Completing authentication on the mobile device (using biometrics, existing session, or credentials)
- Automatically authenticating the desktop browser session once mobile authentication succeeds

### High-level flow
1. User initiates QR login from authorization endpoint (via `AllowQrLogin` client flag)
2. OP generates unique session token and displays QR code containing authentication URL
3. User scans QR code with mobile device
4. Mobile device navigates to authentication URL, completes authentication
5. Mobile device confirms completion via API call
6. Desktop browser polls for completion status
7. Once confirmed, desktop browser receives authorization code and completes OIDC flow

### Architectural approach
- **Device Flow-inspired**: Similar to OAuth 2.0 Device Authorization Grant (RFC 8628) but adapted for OIDC authorization code flow
- **Polling-based**: Desktop browser uses long-polling or short-polling to check status
- **Session-based correlation**: QR code contains unique session ID linking desktop and mobile flows
- **Secure channel**: Mobile authentication creates binding between device session and desktop session

### Key principles
- Reuse existing OIDC authorization code flow (no new grant types)
- Minimal client changes (leverage existing `AllowQrLogin` flag)
- Security: time-limited QR codes, rate limiting, session binding, PKCE enforcement
- UX: auto-refresh QR on expiry, clear mobile confirmation UI, desktop polling feedback
- Observability: metrics for QR generation, scan events, completion rate, abandonment

---

## Architecture & Design

### Components

#### 1. QR Session Management
**Location**: `MrWhoOidc.Auth/Services/QrLoginService.cs`

Responsibilities:
- Generate unique QR session tokens (cryptographically secure, 32+ bytes, base64url)
- Store pending QR sessions with metadata (client_id, return_url, PKCE, expiry, state)
- Track session lifecycle: `pending` → `scanned` → `authenticated` → `consumed`/`expired`
- Handle session expiration and cleanup

**Storage**: New `QrLoginSession` entity in `AuthDbContext`
```csharp
public class QrLoginSession
{
    public Guid Id { get; set; }
    public string SessionToken { get; set; } // unique, indexed
    public string ClientId { get; set; }
    public string ReturnUrl { get; set; }
    public string CodeChallenge { get; set; }
    public string CodeChallengeMethod { get; set; }
    public string State { get; set; }
    public string? Nonce { get; set; }
    public string Scope { get; set; }
    public QrSessionStatus Status { get; set; } // pending/scanned/authenticated/consumed/expired
    public Guid? UserId { get; set; } // set when authenticated
    public string? AuthorizationCode { get; set; } // set when authenticated
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ScannedAt { get; set; }
    public DateTimeOffset? AuthenticatedAt { get; set; }
    public string? MobileUserAgent { get; set; }
    public string? MobileIpAddress { get; set; }
}

public enum QrSessionStatus
{
    Pending,
    Scanned,
    Authenticated,
    Consumed,
    Expired,
    Cancelled
}
```

#### 2. QR Code Generation
**Location**: `MrWhoOidc.WebAuth/Services/QrCodeGenerator.cs`

Responsibilities:
- Generate QR code image (PNG/SVG) from authentication URL
- Use QRCoder library (already in SBOM: `QRCoder 1.6.0`)
- Support configurable size and error correction level
- Return base64-encoded data URI for inline display

**Configuration** (`appsettings.json`):
```json
{
  "QrLogin": {
    "Enabled": true,
    "SessionLifetimeSeconds": 300,
    "QrCodePixelsPerModule": 10,
    "QrCodeErrorCorrectionLevel": "M",
    "PollIntervalSeconds": 2,
    "MaxPollAttempts": 150,
    "AllowMultipleScans": false
  }
}
```

#### 3. Desktop Browser Flow
**Pages**:
- `MrWhoOidc.WebAuth/Pages/Auth/Qr.cshtml` (already exists as placeholder)
- `MrWhoOidc.WebAuth/Pages/Auth/Qr.cshtml.cs` (already exists)

**Enhancements**:
- Display QR code image with authentication URL
- Client-side polling via JavaScript (fetch API)
- Progress indicators: "Waiting for scan" → "Scanned, complete on mobile" → "Authenticated"
- Auto-refresh QR on expiry
- Cancel/fallback button to return to provider selection
- Accessibility: keyboard navigation, ARIA live regions for status updates

**JavaScript polling endpoint**: `GET /api/qr/status/{sessionToken}`
Response:
```json
{
  "status": "pending|scanned|authenticated|expired|cancelled",
  "redirectUrl": "/authorize/callback?code=...",
  "message": "Please complete authentication on your mobile device"
}
```

#### 4. Mobile Authentication Flow
**Pages**:
- `MrWhoOidc.WebAuth/Pages/Auth/QrMobile.cshtml` — mobile landing page after scan
- `MrWhoOidc.WebAuth/Pages/Auth/QrConfirm.cshtml` — mobile confirmation page

**Flow**:
1. Mobile user scans QR code containing URL: `https://op.example.com/Auth/QrMobile?session={token}`
2. If not authenticated: redirect to login with `returnUrl=/Auth/QrConfirm?session={token}`
3. If authenticated: land on confirmation page showing desktop browser details (client name, time)
4. User taps "Confirm" → POST to `/api/qr/confirm` → session marked `authenticated` → authorization code generated
5. Desktop browser poll detects `authenticated` status and receives authorization code

**API endpoint**: `POST /api/qr/confirm`
Request:
```json
{
  "sessionToken": "abc123...",
  "userId": "guid"
}
```
Response:
```json
{
  "success": true,
  "message": "Authentication confirmed. You may close this page."
}
```

#### 5. Handlers & Routing
**Location**: `MrWhoOidc.WebAuth/Handlers/QrLoginHandler.cs` (new)

Responsibilities:
- `InitiateQrLoginAsync`: create session, generate QR, return page
- `GetStatusAsync`: return session status for polling
- `ConfirmAsync`: validate mobile authentication, generate authorization code, update session
- `CancelAsync`: cancel session from desktop

**Routing** (`MrWhoOidc.WebAuth/Program.cs`):
```csharp
app.MapGet("/Auth/Qr", (IQrLoginHandler h, HttpContext ctx) => h.InitiateAsync(ctx));
app.MapGet("/Auth/QrMobile", (IQrLoginHandler h, HttpContext ctx) => h.MobileLandingAsync(ctx));
app.MapGet("/Auth/QrConfirm", (IQrLoginHandler h, HttpContext ctx) => h.ConfirmPageAsync(ctx));
app.MapGet("/api/qr/status/{sessionToken}", (IQrLoginHandler h, HttpContext ctx, string sessionToken) => h.GetStatusAsync(ctx, sessionToken))
   .RequireRateLimiting("rl-qr-poll");
app.MapPost("/api/qr/confirm", (IQrLoginHandler h, HttpContext ctx) => h.ConfirmAsync(ctx))
   .RequireRateLimiting("rl-qr-confirm");
app.MapPost("/api/qr/cancel", (IQrLoginHandler h, HttpContext ctx) => h.CancelAsync(ctx))
   .RequireRateLimiting("rl-qr-cancel");
```

---

## Security Considerations

### 1. Session token security
- [x] Use cryptographically secure random generator (32+ bytes)
- [ ] Store hashed session tokens (SHA256) in database, compare against hash
- [ ] Enforce strict expiration (default 5 minutes, configurable)
- [ ] Rate limit session creation per client/IP
- [ ] One-time use: mark consumed after authorization code issued

### 2. QR code security
- [ ] HTTPS-only URLs in production
- [ ] Include session token in URL but not sensitive OIDC parameters
- [ ] QR code does not contain client_secret or user PII
- [ ] Session-bound PKCE: desktop generates PKCE, stored with session, verified at token endpoint

### 3. Mobile authentication
- [ ] Require authenticated session on mobile before confirmation
- [ ] Bind mobile user ID to QR session
- [ ] Validate mobile user has consent/approval for the client
- [ ] Optional: display desktop IP/user-agent for user awareness ("Logging in from Desktop Browser - Chrome on Windows")
- [ ] Anti-phishing: show client name/logo prominently on mobile confirmation page

### 4. Polling & rate limiting
- [ ] Rate limit status polling (e.g., 1 req/sec per session token)
- [ ] Rate limit confirmation endpoint (e.g., 5 req/min per session token)
- [ ] Detect and block excessive session creation attempts
- [ ] Short-circuit polling after max attempts or expiration

### 5. Authorization code generation
- [ ] Generate authorization code only after mobile confirmation
- [ ] Authorization code inherits PKCE challenge from QR session
- [ ] Authorization code is single-use, short-lived (60s)
- [ ] Link authorization code to UserId from mobile authentication

### 6. Session fixation & CSRF
- [ ] Desktop CSRF protection: include anti-CSRF token in QR page form
- [ ] Mobile confirmation requires POST with anti-CSRF token
- [ ] Prevent session hijacking: optionally fingerprint desktop browser (IP range, user-agent) and verify consistency

### 7. Audit & logging
- [ ] Log QR session creation (client_id, IP, timestamp)
- [ ] Log scan events (session token, mobile IP, user-agent)
- [ ] Log confirmation events (session token, user ID, success/failure)
- [ ] Log expiration/cancellation events
- [ ] Metric: time-to-scan, time-to-confirm, abandonment rate
- [ ] Alert on suspicious patterns (mass session creation, scan without confirmation)

---

## Epic 1: Data Model & Migrations

### Story 1.1: QrLoginSession entity
- [ ] Create `QrLoginSession` entity with fields above
- [ ] Add migration: `dotnet ef migrations add AddQrLoginSession --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations`
- [ ] Index: `SessionToken` (unique), `Status`, `ExpiresAt`
- [ ] Add `DbSet<QrLoginSession>` to `AuthDbContext`
- [ ] Seed test data in `MrWhoOidc.UnitTests/TestDataSeeder.cs`
- **Acceptance**: Migration applies cleanly; rollback supported; test data seed works.

### Story 1.2: Configuration model
- [ ] Create `QrLoginOptions` class in `MrWhoOidc.Auth/Services/QrLoginOptions.cs`
- [ ] Bind from `appsettings.json` section `QrLogin`
- [ ] Validation: SessionLifetimeSeconds between 60-600, PollIntervalSeconds between 1-10
- [ ] Register options in DI: `services.Configure<QrLoginOptions>(config.GetSection("QrLogin"))`
- **Acceptance**: Options load correctly; validation catches invalid values.

### Story 1.3: Feature flag integration
- [ ] Respect existing `Client.AllowQrLogin` flag (already in DB, defaults to `false`)
- [ ] AuthorizeHandler checks `AllowQrLogin` when routing to QR page
- [ ] Admin UI already has checkbox for `AllowQrLogin` (verified in `Edit.cshtml`)
- **Acceptance**: Client with `AllowQrLogin=false` does not show QR option; `AllowQrLogin=true` shows QR button.

---

## Epic 2: QR Session Service

### Story 2.1: Session creation & storage
- [ ] Implement `QrLoginService.CreateSessionAsync(clientId, returnUrl, pkce, state, nonce, scope)`
- [ ] Generate secure session token (32 bytes base64url)
- [ ] Store session in DB with `Status=Pending`, `ExpiresAt=Now+SessionLifetime`
- [ ] Return session token and authentication URL
- **Acceptance**: Unit test creates session; token is unique; expiry is set correctly.

### Story 2.2: Session retrieval & status updates
- [ ] Implement `QrLoginService.GetSessionAsync(sessionToken)`
- [ ] Implement `QrLoginService.UpdateStatusAsync(sessionToken, newStatus, userId?, authCode?)`
- [ ] Implement `QrLoginService.MarkScannedAsync(sessionToken, mobileIp, mobileUserAgent)`
- [ ] Implement `QrLoginService.ExpireSessionAsync(sessionToken)`
- **Acceptance**: Status transitions are atomic; expired sessions return null or error.

### Story 2.3: Background cleanup job
- [ ] Implement `QrLoginCleanupService : BackgroundService`
- [ ] Poll every 60s (configurable), delete sessions older than `ExpiresAt + grace period (e.g., 10min)`
- [ ] Log cleanup count per run
- [ ] Metrics: count of cleaned-up sessions
- **Acceptance**: Expired sessions are deleted; metrics are emitted; logs are structured.

---

## Epic 3: QR Code Generation

### Story 3.1: QRCoder integration
- [ ] Verify `QRCoder 1.6.0` package is referenced in `MrWhoOidc.WebAuth`
- [ ] Create `QrCodeGenerator` service with method `GenerateQrCodeDataUri(url, pixelsPerModule, errorCorrectionLevel)`
- [ ] Return base64-encoded PNG as data URI: `data:image/png;base64,...`
- [ ] Unit test: verify valid PNG is generated; verify base64 decoding
- **Acceptance**: QR code image is visually scannable; data URI renders in browser.

### Story 3.2: URL construction
- [ ] Build authentication URL: `https://{host}/Auth/QrMobile?session={token}`
- [ ] Respect `Request.Scheme` and `Request.Host` from HttpContext
- [ ] Support custom domain via appsettings override (e.g., public-facing domain vs internal)
- **Acceptance**: URL is absolute, HTTPS in prod; mobile device can navigate to it.

---

## Epic 4: Desktop Browser Flow

### Story 4.1: Qr.cshtml page enhancements
- [ ] Display QR code image (inline base64 data URI or `/api/qr/image/{sessionToken}` endpoint)
- [ ] Show instructions: "Scan this QR code with your mobile device to log in"
- [ ] Display session status messages: "Waiting for scan", "Scanned", "Completing authentication...", "Authenticated!"
- [ ] Cancel button: POST to `/api/qr/cancel` then redirect to provider selection
- [ ] Accessibility: `role="img"`, `aria-live="polite"` for status updates, keyboard focus management
- **Acceptance**: QR code is visible and scannable; status updates are announced by screen reader.

### Story 4.2: Client-side polling
- [ ] JavaScript: fetch `/api/qr/status/{sessionToken}` every N seconds (from `QrLoginOptions.PollIntervalSeconds`)
- [ ] Handle responses: `pending` → continue poll, `scanned` → update status, `authenticated` → redirect to `redirectUrl`
- [ ] Handle errors: `expired` → show "QR code expired, generating new one..." and reload page
- [ ] Stop polling after `MaxPollAttempts` or on `authenticated`/`expired`/`cancelled` status
- [ ] Exponential backoff on consecutive errors (e.g., network timeout)
- **Acceptance**: Polling works in Chrome/Firefox/Safari/Edge; transitions are smooth; no console errors.

### Story 4.3: Auto-refresh expired QR
- [ ] If session expires while page is open, display "QR code expired" message
- [ ] Automatically generate new session and reload QR code
- [ ] Preserve original `returnUrl`, `state`, `client_id` from query params
- **Acceptance**: User does not need to manually refresh; new QR appears seamlessly.

### Story 4.4: Fallback/cancel flow
- [ ] Cancel button: POST `/api/qr/cancel` with session token
- [ ] Server marks session `Status=Cancelled`
- [ ] Redirect to `/Auth/Providers/Select?client_id={cid}&returnUrl={url}`
- **Acceptance**: User can escape QR flow and choose different login method.

---

## Epic 5: Mobile Authentication Flow

### Story 5.1: QrMobile landing page
- [ ] Implement `MrWhoOidc.WebAuth/Pages/Auth/QrMobile.cshtml`
- [ ] Parse `?session={token}` from query string
- [ ] Call `QrLoginService.GetSessionAsync(token)` and validate not expired/consumed
- [ ] Mark session as `Scanned` via `QrLoginService.MarkScannedAsync`
- [ ] If mobile user not authenticated: redirect to `/login?returnUrl=/Auth/QrConfirm?session={token}`
- [ ] If mobile user authenticated: redirect to `/Auth/QrConfirm?session={token}`
- **Acceptance**: Scan detection is logged; unauthenticated users are prompted to log in; authenticated users proceed to confirmation.

### Story 5.2: QrConfirm confirmation page
- [ ] Implement `MrWhoOidc.WebAuth/Pages/Auth/QrConfirm.cshtml`
- [ ] Display client name, logo, timestamp
- [ ] Show desktop context: "You are logging in to {ClientName} on a desktop browser"
- [ ] "Confirm" button: POST to `/api/qr/confirm` with session token
- [ ] "Cancel" button: POST to `/api/qr/cancel` with session token
- [ ] Mobile-optimized UI: large buttons, touch-friendly
- **Acceptance**: Confirmation page is usable on mobile (tested on iOS Safari, Android Chrome); client info is accurate.

### Story 5.3: Confirmation API endpoint
- [ ] Implement `POST /api/qr/confirm` handler
- [ ] Validate session token exists and status is `Scanned`
- [ ] Validate authenticated mobile user ID matches session's expected user (or allow any authenticated user if client policy permits)
- [ ] Generate authorization code via `IAuthorizationCodeService.CreateAsync`
- [ ] Link authorization code to session's PKCE challenge
- [ ] Update session: `Status=Authenticated`, `UserId=mobileUserId`, `AuthorizationCode=code`, `AuthenticatedAt=Now`
- [ ] Return JSON: `{ "success": true, "message": "..." }`
- [ ] Audit log: `qr.confirm` event with session token, user ID, client ID
- **Acceptance**: Authorization code is valid; PKCE is preserved; desktop polling detects authentication.

### Story 5.4: Mobile error handling
- [ ] Expired session: show "This QR code has expired. Please scan a new code."
- [ ] Already consumed: show "This QR code has already been used."
- [ ] Cancelled session: show "Login cancelled. You may close this page."
- [ ] Network errors: retry logic, user-friendly message
- **Acceptance**: Error messages are clear; no crashes; mobile user is not stuck.

---

## Epic 6: Handlers & Routing

### Story 6.1: QrLoginHandler implementation
- [ ] Create `QrLoginHandler : IQrLoginHandler` in `MrWhoOidc.WebAuth/Handlers/QrLoginHandler.cs`
- [ ] Implement `InitiateAsync`: create session, generate QR, return Qr.cshtml page
- [ ] Implement `GetStatusAsync`: fetch session, return JSON status
- [ ] Implement `ConfirmAsync`: validate, generate auth code, update session
- [ ] Implement `CancelAsync`: mark session cancelled
- [ ] Implement `MobileLandingAsync`: mark scanned, redirect to login or confirm
- [ ] Implement `ConfirmPageAsync`: render QrConfirm.cshtml with client info
- **Acceptance**: All handler methods have unit tests; happy path and error cases covered.

### Story 6.2: Routing registration
- [ ] Register routes in `MrWhoOidc.WebAuth/Program.cs` as documented above
- [ ] Apply rate limiting policies: `rl-qr-poll`, `rl-qr-confirm`, `rl-qr-cancel`
- [ ] Ensure endpoints are accessible without authentication (except confirmation requires authenticated mobile session)
- **Acceptance**: Routes are reachable; rate limits are enforced; unauthorized requests are rejected appropriately.

### Story 6.3: AuthorizeHandler integration
- [ ] Update `AuthorizeHandler.HandleAsync` to detect QR flow
- [ ] When `allowQr && http.Request.Query.ContainsKey("qr")`: redirect to `/Auth/Qr?...`
- [ ] Preserve `client_id`, `redirect_uri`, `state`, `nonce`, `scope`, `code_challenge`, `code_challenge_method` in QR session
- [ ] Generate PKCE on desktop if not present
- **Acceptance**: Existing placeholder code (line 350-354 of `AuthorizeHandler.cs`) is replaced; QR flow is triggered correctly.

---

## Epic 7: Rate Limiting & Security

### Story 7.1: Rate limit policies
- [ ] Define `rl-qr-poll`: 60 req/min per session token (sliding window)
- [ ] Define `rl-qr-confirm`: 5 req/min per session token (fixed window)
- [ ] Define `rl-qr-cancel`: 10 req/min per session token
- [ ] Define `rl-qr-create`: 10 QR sessions per IP per hour
- [ ] Configure in `Program.cs` using ASP.NET Core rate limiting middleware
- **Acceptance**: Excessive polling returns HTTP 429; retry-after header is present; legit usage is not throttled.

### Story 7.2: Session token hashing
- [ ] Store SHA256(sessionToken) in `QrLoginSession.SessionTokenHash` field
- [ ] Lookup sessions by hash instead of plaintext token
- [ ] Acceptance: DB does not contain plaintext session tokens; lookups are performant (indexed hash column).

### Story 7.3: Anti-CSRF for confirmation
- [ ] Include anti-forgery token in mobile confirmation form
- [ ] Validate token in `POST /api/qr/confirm` handler
- [ ] Return 400 if token missing or invalid
- **Acceptance**: CSRF attacks fail; legitimate requests succeed.

### Story 7.4: IP & user-agent validation (optional)
- [ ] Optionally fingerprint desktop IP/user-agent and store with session
- [ ] On confirmation, check mobile IP is different (expected) but within allowed range if configured
- [ ] Log suspicious patterns (e.g., same IP for desktop and mobile, rapid session creation)
- **Acceptance**: Feature flag controlled; logs are structured; no false positives in test scenarios.

---

## Epic 8: Observability & Monitoring

### Story 8.1: Metrics
- [ ] Add metrics to `OidcMetrics` class:
  - `qr_sessions_created_total` (counter, labels: client_id)
  - `qr_sessions_scanned_total` (counter, labels: client_id)
  - `qr_sessions_authenticated_total` (counter, labels: client_id)
  - `qr_sessions_expired_total` (counter, labels: client_id)
  - `qr_sessions_cancelled_total` (counter, labels: client_id)
  - `qr_time_to_scan_seconds` (histogram, labels: client_id)
  - `qr_time_to_authenticate_seconds` (histogram, labels: client_id)
  - `qr_poll_requests_total` (counter, labels: status)
- **Acceptance**: Metrics are exported via OpenTelemetry; dashboards can be built on Prometheus/Grafana.

### Story 8.2: Structured logging
- [ ] Log QR session creation: `qr.session.created` (session_token_hash, client_id, ip, expiry)
- [ ] Log scan: `qr.session.scanned` (session_token_hash, mobile_ip, mobile_user_agent)
- [ ] Log confirmation: `qr.session.authenticated` (session_token_hash, user_id, duration_ms)
- [ ] Log expiration: `qr.session.expired` (session_token_hash, reason)
- [ ] Log cancellation: `qr.session.cancelled` (session_token_hash, source: desktop|mobile)
- [ ] Use `ILogger` with structured data (not string interpolation)
- **Acceptance**: Logs are JSON-formatted in production; log levels are appropriate; PII is hashed.

### Story 8.3: Audit sink integration
- [ ] Emit audit events via existing `IAuditSink` for security-relevant actions:
  - `qr.confirm` (user_id, client_id, session_token_hash, success)
  - `qr.cancel` (session_token_hash, source)
  - `qr.expired` (session_token_hash, unused)
- **Acceptance**: Audit events are stored in audit log (if configured); retention policy is respected.

### Story 8.4: Health check endpoint
- [ ] Add health check: `/health/qr` returns 200 if QR login is enabled and operational
- [ ] Check: database connectivity, QRCoder library availability, recent session creation success rate
- **Acceptance**: Health check fails if DB is down or QR session creation consistently errors.

---

## Epic 9: Testing

### Story 9.1: Unit tests
- [ ] Test `QrLoginService`: session creation, status updates, expiration logic
- [ ] Test `QrCodeGenerator`: valid data URI, PNG format, error correction levels
- [ ] Test `QrLoginHandler`: initiate, status, confirm, cancel flows
- [ ] Test rate limiting: mock middleware, verify 429 responses
- [ ] Test PKCE preservation: verify code_challenge flows from QR session to auth code to token endpoint
- **Acceptance**: >80% code coverage for new QR components; all edge cases covered.

### Story 9.2: Integration tests
- [ ] Test full happy path: desktop initiates QR → mobile scans → mobile authenticates → desktop polls → authorization code issued → token exchange succeeds
- [ ] Test expiration: desktop waits beyond SessionLifetimeSeconds → status returns `expired` → new session generated
- [ ] Test cancellation: desktop cancels → mobile cannot confirm → status returns `cancelled`
- [ ] Test rate limiting: exceed poll limit → receive 429 → retry-after is respected
- [ ] Test concurrent scans: same QR scanned by multiple devices → first confirmation wins, second fails
- **Acceptance**: Tests run in CI; use in-memory DB; no flakiness; execution time <30s.

### Story 9.3: E2E tests
- [ ] Playwright/Selenium: desktop browser navigates to `/authorize?qr=true` → QR displayed → simulate mobile scan (direct navigation to QrMobile URL) → mobile login → mobile confirm → desktop redirects with auth code
- [ ] Test on real mobile browser (iOS Safari, Android Chrome) manually or via BrowserStack
- **Acceptance**: QR is scannable by real devices; mobile UI is touch-friendly; desktop polling works reliably.

### Story 9.4: Performance tests
- [ ] Load test: 1000 concurrent QR sessions → measure DB load, polling overhead, cleanup job performance
- [ ] Verify rate limits hold under load
- [ ] Measure QR code generation time (should be <100ms)
- **Acceptance**: System handles expected load; no memory leaks; cleanup job completes within SLA.

---

## Epic 10: UX & Accessibility

### Story 10.1: Desktop UX polish
- [ ] QR code is centered, reasonably sized (e.g., 300x300px)
- [ ] Loading spinner while QR is generating
- [ ] Status messages are clear and updated in real-time (via polling)
- [ ] Visual feedback on status change: icon change (clock → checkmark → redirect)
- [ ] "Having trouble?" link to help page or fallback
- **Acceptance**: UX review passes; no confusion reported in user testing.

### Story 10.2: Mobile UX polish
- [ ] Confirmation page loads quickly on mobile (optimized assets)
- [ ] Large touch targets (44x44px minimum for buttons)
- [ ] Client logo/name is prominent
- [ ] Success message after confirmation: "Success! You may close this page."
- [ ] Dark mode support (if rest of app has dark mode)
- **Acceptance**: Mobile UX review passes; tested on variety of devices/screen sizes.

### Story 10.3: Accessibility audit
- [ ] Qr.cshtml passes WCAG 2.1 AA: keyboard navigation, screen reader announcements, color contrast
- [ ] QrMobile.cshtml passes WCAG 2.1 AA
- [ ] QrConfirm.cshtml passes WCAG 2.1 AA
- [ ] ARIA labels: `aria-live="polite"` for status updates, `role="img"` for QR, `aria-describedby` for instructions
- [ ] Axe DevTools scan: 0 critical violations
- **Acceptance**: Accessibility scan passes; manual testing with screen reader (NVDA/JAWS) succeeds.

### Story 10.4: Internationalization (i18n)
- [ ] Extract UI strings to resource files: `Resources/QrLogin.resx`
- [ ] Support multiple languages (if rest of app is i18n-enabled)
- [ ] Mobile confirmation page respects `Accept-Language` header or user preference
- **Acceptance**: QR login works in configured languages; translations are accurate.

---

## Epic 11: Admin & Configuration

### Story 11.1: Admin UI for QR settings
- [ ] Add section to global settings page: QR Login Configuration
- [ ] Fields: Enabled (toggle), SessionLifetimeSeconds, PollIntervalSeconds, MaxPollAttempts, AllowMultipleScans
- [ ] Validate: SessionLifetimeSeconds between 60-600, PollIntervalSeconds between 1-10
- [ ] Save to appsettings or database (prefer appsettings for global config)
- **Acceptance**: Admin can enable/disable QR login globally; changes take effect immediately (or after app restart if appsettings).

### Story 11.2: Per-client QR toggle
- [ ] Already implemented: `Client.AllowQrLogin` flag in DB, checkbox in Admin > Clients > Edit
- [ ] Verify behavior: if `AllowQrLogin=false`, QR option is hidden in provider selection
- [ ] Acceptance: Admin can control QR availability per client; toggling works end-to-end.

### Story 11.3: Monitoring dashboard
- [ ] Document recommended Grafana dashboard panels:
  - QR sessions created over time (line chart)
  - Scan rate (percentage of sessions scanned)
  - Completion rate (percentage of scanned sessions authenticated)
  - Abandonment rate (percentage of sessions expired without authentication)
  - Average time-to-scan, time-to-authenticate (histograms)
- [ ] Provide sample dashboard JSON in `/docs/monitoring/qr-login-dashboard.json`
- **Acceptance**: Ops team can import dashboard; metrics are populated; alerting rules can be defined.

---

## Epic 12: Documentation

### Story 12.1: Developer guide
- [ ] Update `/docs/developer-guide.md` with QR login section:
  - Architecture overview
  - Flow diagrams (desktop, mobile, sequence diagram)
  - API endpoints reference
  - Configuration options
  - Code examples
- **Acceptance**: New developers can understand QR flow from documentation; code examples are runnable.

### Story 12.2: Admin guide
- [ ] Update `/docs/admin-guide.md` with QR login section:
  - How to enable QR login globally
  - How to enable QR login per client
  - How to configure session lifetime
  - How to monitor QR login usage
  - Troubleshooting: common issues (expired sessions, mobile not scanning, polling timeout)
- **Acceptance**: Admins can configure and troubleshoot QR login using guide.

### Story 12.3: Security guide
- [ ] Create `/docs/security/qr-login-security.md`:
  - Threat model: QR phishing, session hijacking, MITM
  - Mitigations: HTTPS, PKCE, session expiration, rate limiting, CSRF tokens
  - Best practices: user education (verify client name on mobile), network security
  - Incident response: what to do if QR session is compromised
- **Acceptance**: Security team approves; penetration testing checklist is comprehensive.

### Story 12.4: User help article
- [ ] Create user-facing help article: "How to log in with QR code"
  - What is QR login?
  - Step-by-step with screenshots
  - Troubleshooting: "QR code expired", "Mobile browser not working", "Scan not detected"
  - FAQ: Is it secure? Do I need an app? Can I use any device?
- **Acceptance**: Help article is linked from QR login page; user comprehension is high.

---

## Epic 13: Migration & Rollout

### Story 13.1: Feature flag rollout
- [ ] QR login is opt-in per client (via `AllowQrLogin` flag)
- [ ] Global feature flag in `appsettings.json`: `QrLogin:Enabled`
- [ ] Default: `Enabled=false` initially; admins enable explicitly
- [ ] Phased rollout: enable for test client → internal clients → external clients
- **Acceptance**: Rollout is controlled; no unintended exposure; rollback is trivial (flip flag).

### Story 13.2: Backward compatibility
- [ ] Existing clients without `AllowQrLogin` continue to work (QR option hidden)
- [ ] Existing login flows (local, external IdP) are unaffected
- [ ] No breaking changes to OIDC protocol endpoints
- **Acceptance**: Regression tests pass; existing clients have no disruption.

### Story 13.3: Database migration strategy
- [ ] EF migration: add `QrLoginSessions` table
- [ ] Idempotent migration: can be re-run safely
- [ ] Rollback migration: drop table, no data loss for other entities
- [ ] Test on staging: apply migration, seed data, verify queries, rollback, verify rollback
- **Acceptance**: Migration is production-ready; DBA approves; rollback plan is documented.

### Story 13.4: Monitoring & alerting setup
- [ ] Configure alerts:
  - High abandonment rate (>50% sessions expired without authentication)
  - Low scan rate (>80% sessions never scanned)
  - High error rate on confirmation endpoint (>5% 4xx/5xx)
  - QR cleanup job failure
- [ ] Alert channels: Slack, email, PagerDuty (depending on org setup)
- **Acceptance**: Alerts fire correctly in test scenarios; no false positives.

---

## Open Questions & Risks

### Open questions
1. **Multi-device QR**: Should one QR be scannable by multiple devices? (Mitigation: `AllowMultipleScans=false` flag, first confirmation wins)
2. **QR code expiration UI**: Show countdown timer on desktop? (Nice-to-have; defer to UX feedback)
3. **Session cleanup grace period**: How long to keep expired sessions for audit? (Proposal: 10 minutes, configurable)
4. **Mobile app vs browser**: Support native mobile app for QR scanning (deeplink)? (Defer; start with mobile web)
5. **Offline QR**: Can QR work offline on mobile (e.g., Bluetooth)? (No; requires network for OIDC flow)
6. **Consent during QR flow**: Should mobile show consent screen? (Yes, if `RequireConsent=true` for client)

### Risks
1. **QR phishing**: Attacker displays fake QR leading to phishing site
   - Mitigation: User education, display client name/logo prominently on mobile
2. **Session hijacking**: Attacker intercepts session token from QR code
   - Mitigation: HTTPS, short expiration, one-time use
3. **Polling overhead**: Excessive polling causes DB/API load
   - Mitigation: Rate limiting, exponential backoff, configurable poll interval
4. **Mobile browser compatibility**: Some mobile browsers may not handle redirects correctly
   - Mitigation: Test on iOS Safari, Android Chrome; provide fallback instructions
5. **Usability confusion**: Users don't understand QR flow
   - Mitigation: Clear instructions, help links, progressive disclosure
6. **Performance at scale**: 10,000 concurrent QR sessions may overwhelm DB
   - Mitigation: Load testing, consider Redis cache for session state, database read replicas for polling

---

## Success Metrics

### Adoption metrics
- **QR login usage rate**: % of authorization flows using QR (target: 10% after 3 months)
- **Client enablement**: % of clients with `AllowQrLogin=true` (target: 25% after 6 months)

### Performance metrics
- **Time-to-scan**: P50/P95 time from QR display to scan (target: P95 < 30s)
- **Time-to-authenticate**: P50/P95 time from scan to authentication (target: P95 < 45s)
- **Completion rate**: % of scanned sessions that complete authentication (target: >70%)
- **Abandonment rate**: % of created sessions that expire without scan (target: <30%)

### Quality metrics
- **Error rate**: % of QR confirmation requests resulting in 4xx/5xx (target: <1%)
- **Polling efficiency**: Average number of poll requests per session (target: <15)
- **Session cleanup lag**: Time between expiration and deletion (target: <5 minutes)

### User satisfaction
- **NPS**: Net Promoter Score for QR login (target: >30)
- **Support tickets**: Number of QR-related support tickets per 1000 logins (target: <5)

---

## Dependencies

### External
- **QRCoder library**: Already in SBOM (v1.6.0); verify no breaking changes if upgrading

### Internal
- **MrWhoOidc.Auth**: Entity model, migration, services
- **MrWhoOidc.WebAuth**: Handlers, Razor Pages, APIs, background services
- **MrWhoOidc.ServiceDefaults**: Logging, OpenTelemetry, health checks
- **Rate limiting middleware**: ASP.NET Core 9 built-in (already configured in `Program.cs`)
- **Admin UI**: Existing `/Admin/Clients` pages for `AllowQrLogin` toggle

### Integration points
- **AuthorizeHandler**: Redirect to QR page when `qr` query param present
- **IAuthorizationCodeService**: Generate auth code from QR session
- **OidcMetrics**: Emit QR-specific metrics
- **IAuditSink**: Log QR security events

---

## References

### Specifications
- OAuth 2.0 Device Authorization Grant (RFC 8628): https://datatracker.ietf.org/doc/html/rfc8628
  - Inspiration for polling and user code flows
- OpenID Connect Core: https://openid.net/specs/openid-connect-core-1_0.html
  - Authorization code flow, PKCE, nonce handling

### Best practices
- OWASP Authentication Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html
- QR Code Security: https://www.ncsc.gov.uk/guidance/qr-codes-risks-and-security-considerations

### Similar implementations
- Azure AD Authenticator app: QR-based passwordless login
- Google Authenticator: TOTP QR enrollment
- WhatsApp Web: QR-based session pairing

---

## Appendix A: Flow Diagrams

### Desktop browser sequence
```
User -> Browser: Navigate to /authorize?qr=true
Browser -> OP: GET /authorize?qr=true&client_id=...
OP -> OP: Create QR session (PKCE, state, nonce)
OP -> Browser: Render /Auth/Qr with QR code
Browser -> User: Display QR code
Browser -> OP: Poll GET /api/qr/status/{token} every 2s
OP -> Browser: { "status": "pending" }
... [Mobile flow happens] ...
OP -> Browser: { "status": "authenticated", "redirectUrl": "/authorize/callback?code=..." }
Browser -> OP: GET /authorize/callback?code=...
OP -> Browser: Redirect to client with authorization code
Browser -> Client: GET /callback?code=...
```

### Mobile device sequence
```
User -> Mobile: Scan QR code
Mobile -> OP: GET /Auth/QrMobile?session={token}
OP -> OP: Mark session as "scanned"
OP -> Mobile: Redirect to /login?returnUrl=/Auth/QrConfirm?session={token}
Mobile -> OP: POST /login (credentials)
OP -> Mobile: Redirect to /Auth/QrConfirm?session={token}
OP -> Mobile: Render confirmation page
Mobile -> User: "Confirm login to {ClientName}?"
User -> Mobile: Tap "Confirm"
Mobile -> OP: POST /api/qr/confirm { "sessionToken": "{token}" }
OP -> OP: Generate authorization code, update session
OP -> Mobile: { "success": true }
Mobile -> User: "Success! You may close this page."
```

---

## Appendix B: Sample Configuration

### appsettings.json
```json
{
  "QrLogin": {
    "Enabled": true,
    "SessionLifetimeSeconds": 300,
    "QrCodePixelsPerModule": 10,
    "QrCodeErrorCorrectionLevel": "M",
    "PollIntervalSeconds": 2,
    "MaxPollAttempts": 150,
    "AllowMultipleScans": false,
    "CleanupIntervalSeconds": 60,
    "CleanupGracePeriodSeconds": 600,
    "BaseUrl": "https://auth.example.com"
  }
}
```

### Rate limiting policies
```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddSlidingWindowLimiter("rl-qr-poll", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 60;
        opt.SegmentsPerWindow = 6;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("rl-qr-confirm", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 5;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("rl-qr-cancel", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 10;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("rl-qr-create", opt =>
    {
        opt.Window = TimeSpan.FromHours(1);
        opt.PermitLimit = 10;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });
});
```

---

## Appendix C: Test Scenarios

### Happy path
1. Desktop: Navigate to `/authorize?qr=true&client_id=test_client`
2. Desktop: QR code displayed, status = "pending"
3. Mobile: Scan QR, navigate to `/Auth/QrMobile?session={token}`
4. Mobile: Redirect to login (if not authenticated)
5. Mobile: Complete login, redirect to `/Auth/QrConfirm?session={token}`
6. Mobile: Tap "Confirm", POST `/api/qr/confirm`
7. Desktop: Poll detects "authenticated" status, redirects to `/authorize/callback?code=...`
8. Desktop: Exchange code for tokens, login complete

### Expiration
1. Desktop: Generate QR code
2. Desktop: Wait 5+ minutes (SessionLifetimeSeconds)
3. Desktop: Poll returns "expired"
4. Desktop: Auto-refresh page, new QR generated
5. Mobile: Scan new QR, proceed normally

### Cancellation from desktop
1. Desktop: Generate QR code
2. Desktop: Click "Cancel" button
3. Desktop: POST `/api/qr/cancel`, redirect to provider selection
4. Mobile: Scan QR (expired), see "Login cancelled" message

### Concurrent scans
1. Desktop: Generate QR code
2. Mobile A: Scan QR, confirm login
3. Mobile B: Scan same QR, attempt confirm → receive "already used" error
4. Desktop: Receives code from Mobile A only

### Rate limit exceeded
1. Desktop: Generate QR code
2. Desktop: Poll 61 times in 1 minute
3. Desktop: Receive HTTP 429 with Retry-After header
4. Desktop: Wait, resume polling

---

**End of backlog document**
