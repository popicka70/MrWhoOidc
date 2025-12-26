# Research: Platform QR Login at DiscoverTenant

**Feature**: 014-platform-qr-login  
**Date**: 2025-12-26

## Research Questions

### RQ1: How should platform settings be stored?

**Decision**: Single-row database table (`PlatformSettings` entity)

**Rationale**:

- Runtime changes required (admin UI must update without restart)
- Consistent with tenant settings pattern (TenantSettings stored in Tenant.SettingsJson)
- Cacheable via HybridCache (same pattern as TenantSettingsService)
- Database already manages all other configuration (tenants, clients, etc.)

**Alternatives Considered**:

| Option | Pros | Cons | Rejected Because |
|--------|------|------|------------------|
| appsettings.json | Simple, no DB | Requires restart, no admin UI | Cannot be changed at runtime |
| Environment variables | Container-native | Requires restart | Cannot be changed at runtime |
| Redis-only | Fast | Volatile, not auditable | Persistence required |
| Tenant.SettingsJson on default tenant | Reuses existing | Semantically wrong (platform ≠ tenant) | Confusing model |

### RQ2: How does existing QR login work and what can be reused?

**Decision**: Reuse entire existing QR infrastructure with minimal changes

**Findings from codebase analysis**:

1. **QrLoginHandler** (`WebAuth/Handlers/QrLoginHandler.cs`):
   - `InitiateAsync()` creates QR session and returns QR code
   - `GetStatusAsync()` polls session status
   - `ConfirmAsync()` / `CancelAsync()` handle mobile responses
   - Already supports `returnUrl` parameter

2. **QR Pages**:
   - `/auth/qr` - Desktop page showing QR code
   - `/auth/qr-mobile` - Mobile landing page
   - `/auth/qr-confirm` - Mobile confirmation page

3. **QrLoginOptions** (`Auth/Services/QrLoginOptions.cs`):
   - `Enabled` flag already exists
   - Session lifetime, poll intervals configured

4. **QrLoginSession** entity already exists in AuthDbContext

**Integration approach**:

- DiscoverTenant adds "Sign in with QR Code" button
- Button links to `/auth/qr` with `returnUrl` preserved
- Existing QR flow handles authentication
- On success, user is authenticated and redirected

### RQ3: What authorization policy should protect Platform Settings?

**Decision**: Use existing `platform-admin` policy

**Rationale**:

- Already exists and used for PlatformAdmin/* pages
- `[Authorize(Policy = "platform-admin")]` on IndexModel
- Consistent with tenant export/import patterns
- No need to create new policy

**Evidence from codebase**:

```csharp
// MrWhoOidc.WebAuth/Pages/PlatformAdmin/Index.cshtml.cs
[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class IndexModel : PageModel
```

### RQ4: Where should Platform Settings link appear in admin navigation?

**Decision**: Add to PlatformAdmin section of admin sidebar, visible only to platform admins

**Rationale**:

- Consistent with existing PlatformAdmin pages (Tenants, Impersonation)
- Separate from tenant-specific Settings page (`/admin/settings`)
- Clear visual distinction between tenant and platform scope

**Implementation**:

- Add nav item in `_AdminLayout.cshtml` under PlatformAdmin section
- Use same authorization check pattern as other PlatformAdmin links

### RQ5: Should QR login at DiscoverTenant require a client context?

**Decision**: No, QR login at DiscoverTenant operates without client context

**Rationale**:

- DiscoverTenant is for tenant discovery before client selection
- User authenticates first, then can access any client they have permissions for
- Existing QR flow can work with null client_id (creates session without client binding)
- After authentication, user is redirected to DiscoverTenant's returnUrl or default

**Difference from client-initiated QR**:

| Aspect | Client-initiated | DiscoverTenant |
|--------|------------------|----------------|
| client_id | Required | Not required |
| redirect_uri | Client's | DiscoverTenant's returnUrl |
| Scope | Client-defined | Default (openid profile email) |
| Tenant context | From client | Resolved during mobile auth |

## Technical Decisions Summary

| Decision | Choice | Impact |
|----------|--------|--------|
| Storage | PlatformSettings DB entity | New migration, new service |
| QR infrastructure | Full reuse | Minimal code changes |
| Authorization | `platform-admin` policy | No policy changes |
| Navigation | PlatformAdmin sidebar | Layout update |
| Client context | Not required | Simplified flow |

## Dependencies

- Existing QR login must be enabled globally (`QrLogin:Enabled` in appsettings)
- Platform admin users must exist (seeded in default tenant)
- HybridCache for platform settings caching (already configured)

## Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| QR flow breaks without client_id | Low | High | Test InitiateAsync with null client params |
| Cache invalidation missed | Medium | Low | Explicit cache key removal on save |
| Platform settings table migration fails | Low | High | Standard EF migration, tested in dev |
