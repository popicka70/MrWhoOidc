# LogoutHandler Architecture

## Component Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                         ILogoutHandler                               │
│                    (Public Interface)                                │
└────────────────────────────┬────────────────────────────────────────┘
                             │
                             │ implements
                             ▼
┌─────────────────────────────────────────────────────────────────────┐
│                        LogoutHandler                                 │
│                     (Main Orchestrator)                              │
│                                                                       │
│  + LocalLogoutAsync()                                                │
│  + LogoutEntryAsync()                                                │
│  + FederatedCallbackAsync()                                          │
│  + EndSessionAsync()                                                 │
│  + FinalRedirectAsync()                                              │
└─┬────┬────────┬────────────┬─────────────────────────────────────┬──┘
  │    │        │            │                                     │
  │    │        │            │                                     │
  ▼    ▼        ▼            ▼                                     ▼
┌─────────┐  ┌─────────┐  ┌──────────┐  ┌─────────────┐  ┌─────────────┐
│ Local   │  │Federated│  │Federated │  │ EndSession  │  │   Logout    │
│ Logout  │  │ Entry   │  │ Callback │  │  Handler    │  │  Redirect   │
│ Handler │  │ Handler │  │ Handler  │  │             │  │  Resolver   │
└─────────┘  └────┬────┘  └──────────┘  └──────┬──────┘  └─────────────┘
                  │                             │
                  │ uses                        │ coordinates
                  ▼                             │
            ┌─────────┐                         ▼
            │ Local   │         ┌───────────────────────────┐
            │ Logout  │         │                           │
            │ Handler │         │   EndSession Components   │
            └─────────┘         │                           │
                                ├───────────────────────────┤
                                │ FrontChannelLogoutNotifier│
                                │ BackChannelLogoutEnqueuer │
                                │ PostLogoutRedirectValidator│
                                └─────────┬─────────────────┘
                                          │
                                          │ uses
                                          ▼
                                  ┌────────────────┐
                                  │ LogoutToken    │
                                  │    Builder     │
                                  └────────────────┘
```

## Flow Diagrams

### 1. Local Logout Flow

```
User Request
    │
    ├─> GET /logout?returnUrl=/home
    │
    ▼
LogoutHandler.LocalLogoutAsync()
    │
    ▼
LocalLogoutHandler.ExecuteAsync()
    │
    ├─> SignOutAsync() (cookie)
    │
    ▼
Results.Redirect("/home")
```

### 2. Federated Logout Entry Flow

```
User Request
    │
    ├─> GET /logout?returnUrl=/home&style=dark
    │
    ▼
LogoutHandler.LogoutEntryAsync()
    │
    ▼
FederatedLogoutEntryHandler.ExecuteAsync()
    │
    ├─> Check FederatedLogoutOptions.Enabled
    │   │
    │   ├─ Disabled ──> LocalLogoutHandler.ExecuteAsync()
    │   │
    │   └─ Enabled
    │       │
    │       ├─> IUpstreamLogoutService.CanFederateAsync()
    │       │   │
    │       │   ├─ Not capable ──> LocalLogoutHandler.ExecuteAsync()
    │       │   │
    │       │   └─ Can federate
    │       │       │
    │       │       ├─> Emit audit event
    │       │       ├─> Record metrics
    │       │       │
    │       │       ▼
    │       Results.Redirect("/Logout/Prompt?provider=Google&ret=/home&style=dark")
```

### 3. Federated Callback Flow

```
Upstream IdP Callback
    │
    ├─> GET /logout/federated-callback?state=xyz123
    │
    ▼
LogoutHandler.FederatedCallbackAsync()
    │
    ▼
FederatedCallbackHandler.ExecuteAsync()
    │
    ├─> IUpstreamLogoutService.ValidateCallbackAsync(state)
    │   │
    │   ├─ Invalid ──> Results.Redirect("/Logout/FederatedCallbackError?reason=...")
    │   │
    │   └─ Valid
    │       │
    │       ├─ Has RefId ──> Results.Redirect("/logout/final?ref=...")
    │       │
    │       ├─ Has ReturnUrl ──> Results.Redirect(returnUrl)
    │       │
    │       └─ Default ──> Results.Redirect("/Logout/FederatedSignedOut")
```

### 4. End Session Flow (OIDC RP-Initiated Logout)

```
RP Request
    │
    ├─> GET /connect/endsession?id_token_hint=...&post_logout_redirect_uri=...&client_id=...
    │
    ▼
LogoutHandler.EndSessionAsync()
    │
    ├─> Parse LogoutRequest
    │
    ▼
EndSessionHandler.ExecuteAsync()
    │
    ├─> SignOutAsync() (local session)
    │
    ├─> FrontChannelLogoutNotifier.GetFrontChannelIframeUrlsAsync()
    │   │
    │   ├─> Query clients with FrontChannelLogoutUri
    │   │
    │   └─> Build iframe URLs with iss + sid
    │
    ├─> BackChannelLogoutEnqueuer.EnqueueNotificationsAsync()
    │   │
    │   ├─> Check BackchannelFeatureOptions.Enabled
    │   │   │
    │   │   └─ Enabled
    │   │       │
    │   │       ├─> Query clients with BackChannelLogoutUri
    │   │       │
    │   │       ├─> LogoutTokenBuilder.CreateLogoutToken()
    │   │       │   │
    │   │       │   ├─> Extract sub/sid from id_token_hint
    │   │       │   ├─> Build JWT payload
    │   │       │   └─> Sign with IKeyStore key
    │   │       │
    │   │       ├─> Apply allow/block list filtering
    │   │       │
    │   │       └─> Insert BackchannelLogoutNotification entities
    │
    ├─> PostLogoutRedirectValidator.ValidateAndCreateReferenceAsync()
    │   │
    │   ├─ Missing client_id ──> Log rejection
    │   │
    │   └─ Valid client_id
    │       │
    │       ├─> Lookup client
    │       │
    │       ├─> Parse AllowedLogoutRedirectUrisJson
    │       │
    │       ├─> UrlComparison.IsAllowed()
    │       │   │
    │       │   ├─ Not allowed ──> Log rejection
    │       │   │
    │       │   └─ Allowed
    │       │       │
    │       │       ├─> Generate opaque reference ID
    │       │       │
    │       │       └─> Insert LogoutRedirectReference entity
    │
    └─> FrontChannelPageBuilder.BuildPage(iframes, refId, state)
        │
        ├─> Generate HTML with hidden iframes
        │
        └─> Add auto-redirect script if refId present
```

### 5. Final Redirect Flow

```
Auto-Redirect from End Session Page
    │
    ├─> GET /logout/final?ref=abc123xyz
    │
    ▼
LogoutHandler.FinalRedirectAsync()
    │
    ▼
LogoutRedirectResolver.ResolveAndRedirectAsync(refId)
    │
    ├─> Lookup LogoutRedirectReference by ID
    │   │
    │   ├─ Not found ──> Results.BadRequest()
    │   │
    │   ├─ Expired or Used ──> Results.BadRequest()
    │   │
    │   └─ Valid
    │       │
    │       ├─> Mark as Used
    │       │
    │       ├─> Append state parameter if present
    │       │
    │       └─> Results.Redirect(validated_uri)
```

## Dependency Graph

```
LogoutHandler (orchestrator)
    ├── LocalLogoutHandler
    │       └── (no dependencies)
    │
    ├── FederatedLogoutEntryHandler
    │       ├── IUpstreamLogoutService
    │       ├── IOptions<FederatedLogoutOptions>
    │       ├── LocalLogoutHandler
    │       ├── IAuditSink
    │       └── OidcMetrics
    │
    ├── FederatedCallbackHandler
    │       ├── IUpstreamLogoutService
    │       ├── IAuditSink
    │       └── OidcMetrics
    │
    ├── EndSessionHandler
    │       ├── FrontChannelLogoutNotifier
    │       │       └── AuthDbContext
    │       │
    │       ├── BackChannelLogoutEnqueuer
    │       │       ├── AuthDbContext
    │       │       ├── LogoutTokenBuilder
    │       │       │       └── IKeyStore
    │       │       ├── IOptionsMonitor<BackchannelFeatureOptions>
    │       │       ├── IConfiguration
    │       │       ├── IAuditSink
    │       │       └── OidcMetrics
    │       │
    │       ├── PostLogoutRedirectValidator
    │       │       ├── AuthDbContext
    │       │       ├── IAuditSink
    │       │       └── OidcMetrics
    │       │
    │       ├── IAuditSink
    │       └── OidcMetrics
    │
    └── LogoutRedirectResolver
            ├── AuthDbContext
            └── IAuditSink
```

## Layer Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     Presentation Layer                          │
│  (HTTP Endpoints, Request/Response Handling)                    │
│                                                                  │
│  • ILogoutHandler interface                                     │
│  • LogoutRequest record                                         │
│  • FrontChannelPageBuilder (HTML generation)                    │
└────────────────────────────┬────────────────────────────────────┘
                             │
┌────────────────────────────▼────────────────────────────────────┐
│                     Orchestration Layer                         │
│  (Coordination of logout flows)                                 │
│                                                                  │
│  • LogoutHandler (main orchestrator)                            │
│  • LocalLogoutHandler                                           │
│  • FederatedLogoutEntryHandler                                  │
│  • FederatedCallbackHandler                                     │
│  • EndSessionHandler                                            │
└────────────────────────────┬────────────────────────────────────┘
                             │
┌────────────────────────────▼────────────────────────────────────┐
│                       Business Logic Layer                      │
│  (Validation, Token Creation, Notifications)                    │
│                                                                  │
│  • PostLogoutRedirectValidator                                  │
│  • LogoutTokenBuilder                                           │
│  • FrontChannelLogoutNotifier                                   │
│  • BackChannelLogoutEnqueuer                                    │
└────────────────────────────┬────────────────────────────────────┘
                             │
┌────────────────────────────▼────────────────────────────────────┐
│                       Data Access Layer                         │
│  (Persistence, External Services)                               │
│                                                                  │
│  • AuthDbContext (EF Core)                                      │
│  • IKeyStore                                                    │
│  • IUpstreamLogoutService                                       │
│  • LogoutRedirectResolver                                       │
└─────────────────────────────────────────────────────────────────┘
```

## Security Boundaries

### 1. Input Validation
- `LogoutRequest.FromQuery()` - Parses and validates query parameters
- `PostLogoutRedirectValidator` - Validates post_logout_redirect_uri against allow-list

### 2. Authentication/Authorization
- All handlers assume authentication is handled by ASP.NET Core middleware
- No additional authorization checks within handlers (policy-based elsewhere)

### 3. Token Security
- `LogoutTokenBuilder` - Creates signed JWTs with IKeyStore
- Logout tokens include `typ=logout+jwt` header per RFC 8225
- Tokens expire after 5 minutes

### 4. URL Security
- `FrontChannelPageBuilder` - HTML-encodes all URLs in iframes
- JavaScript-encodes redirect URLs
- `PostLogoutRedirectValidator` - Uses UrlComparison.IsAllowed for strict matching

### 5. Opaque References
- `PostLogoutRedirectValidator` - Creates cryptographically random 128-bit IDs
- `LogoutRedirectResolver` - Single-use references with 5-minute expiry
- Prevents URL parameter injection attacks

## Observability

### Audit Events
All handlers emit structured audit events via `IAuditSink`:

```
logout.federated.prompt.skip_disabled
logout.federated.prompt.skip_no_capability
logout.federated.prompt
logout.federated.callback.page.fail
logout.federated.callback.page.ok
logout.redirect.rejected_missing_client
logout.redirect.rejected_client_not_found
logout.redirect.rejected_missing_allowlist
logout.redirect.rejected_not_allowed
logout.redirect.rejected_invalid_allowlist
logout.redirect.ref.created
logout.redirect.ref.used
bcl.enqueue
```

### Metrics
Recorded via `OidcMetrics`:

```
LogoutRequests (Counter)
  - mode: prompt, local, end_session

LogoutFailures (Counter)
  - reason: invalid_state, client_not_found, post_logout_not_allowed, etc.

LogoutDuration (Histogram)
  - mode: prompt, federated_callback, federated_callback_fail

BclEmitted (Counter)
  - client_id: {client}
```

### Logging
All handlers use `ILogger<T>` for structured logging:
- Information: Normal flow events
- Warning: Validation failures, blocked requests
- Error: Unexpected exceptions

## Extension Points

### 1. Custom Validators
Implement additional validation by creating new classes:

```csharp
public class CustomPostLogoutValidator
{
    public async Task<bool> ValidateAsync(string uri, string clientId)
    {
        // Custom validation logic
    }
}
```

### 2. Custom Notifiers
Extend notification mechanisms:

```csharp
public class CustomLogoutNotifier
{
    public async Task NotifyAsync(string clientId, string logoutToken)
    {
        // Custom notification logic (e.g., message queue)
    }
}
```

### 3. Custom Token Builders
Alternative token formats:

```csharp
public class SamlLogoutTokenBuilder
{
    public string CreateSamlLogoutToken(...)
    {
        // SAML logout request
    }
}
```

### 4. Custom HTML Builders
Alternative page rendering:

```csharp
public class RazorLogoutPageBuilder
{
    public async Task<string> BuildPageAsync(...)
    {
        // Render Razor view
    }
}
```

## Performance Considerations

### Database Queries
- `FrontChannelLogoutNotifier`: Single query with `AsNoTracking()` for read-only client list
- `BackChannelLogoutEnqueuer`: Single query + bulk insert of notification entities
- `PostLogoutRedirectValidator`: Single client lookup with `FirstOrDefaultAsync()`
- `LogoutRedirectResolver`: Single reference lookup with `FirstOrDefaultAsync()`

### Caching Opportunities
- Client configurations could be cached (not implemented yet)
- Signing keys already cached via `IKeyStore`
- Allow/block lists could be cached

### Async Operations
All handlers use async/await throughout:
- Database operations: `await db.SaveChangesAsync()`
- External service calls: `await upstreamLogoutSvc.CanFederateAsync()`
- All methods return `Task<IResult>`

## Testing Strategy

### Unit Tests
Each handler can be unit tested independently:
- Mock `AuthDbContext` with in-memory database
- Mock `IUpstreamLogoutService`, `IKeyStore`, etc.
- Verify correct behavior for different scenarios

### Integration Tests
Test complete flows:
- `LogoutPromptFlowTests.LogoutEntry_RedirectsToPrompt_WithStyle()` - Example existing test
- Additional tests can verify end-to-end scenarios

### Test Helpers
- `TestOptionsMonitor<T>` - Mocks `IOptionsMonitor` for testing
- `TestHttpClientFactory` - Mocks HTTP client for external calls
- In-memory `AuthDbContext` for database operations

## Migration Path

For codebases currently using the old `LogoutHandler`:

1. **Update using statements**:
   ```csharp
   using MrWhoOidc.WebAuth.Handlers.Logout;
   ```

2. **Update DI registration** (already done in `PersistenceAndCoreExtensions.cs`)

3. **Update tests** to construct new dependencies (see `LogoutPromptFlowTests.cs`)

4. **No changes required** to:
   - Endpoint mappings
   - HTTP clients calling logout endpoints
   - Configuration files
   - Database schema

## Conclusion

The refactored `LogoutHandler` architecture provides:
- **Clear separation of concerns** across 14 focused classes
- **Well-defined dependencies** via constructor injection
- **Comprehensive observability** via audit events, metrics, and logging
- **Strong security boundaries** with validation and opaque references
- **Extensibility** through clear extension points
- **Testability** with isolated, mockable components
- **Maintainability** with single-responsibility classes
- **Backward compatibility** with existing code

The architecture follows industry best practices and SOLID principles while maintaining the same external API and behavior as the original monolithic handler.
