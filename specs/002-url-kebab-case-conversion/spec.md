# Feature Specification: System-Wide URL Convention Standardization to kebab-case

**Feature Branch**: `002-url-kebab-case-conversion`  
**Created**: 2025-01-18  
**Status**: Draft  
**Input**: User description: "I want to convert entire solution to kebab case url convention. I want to replace all existing pascal case urls. Keep in mind we need to redo all places where we construct URLs and URL fragments."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Core Protocol Endpoints Migration (Priority: P1)

External identity providers and relying party clients must connect to MrWhoOidc's OIDC endpoints using kebab-case URLs. The system converts all core OIDC protocol endpoints (`/authorize`, `/token`, `/userinfo`, `/revoke`, `/introspect`, `/par`, discovery, JWKS) and authentication flow endpoints (`/Auth/External/*`) to kebab-case convention while maintaining backward compatibility during migration period.

**Why this priority**: These endpoints are exposed externally to RPs and external IdPs. Changes here affect external integrations and must be coordinated. Getting these right first ensures external parties have stable endpoints to integrate with.

**Independent Test**: Can be fully tested by configuring a test RP client to perform complete OIDC authorization code flow against kebab-case endpoints (discovery → authorize → token → userinfo) and validating all responses. External IdP callback can be tested by initiating federated authentication flow and verifying redirect URI handling.

**Acceptance Scenarios**:

1. **Given** an external RP client requests discovery document, **When** client fetches `/.well-known/openid-configuration`, **Then** all endpoint URLs in response use kebab-case convention (e.g., `/auth/external/callback`, `/auth/external/start`)
2. **Given** external IdP redirects user back after authentication, **When** callback arrives at `/auth/external/callback` (kebab-case), **Then** authentication flow completes successfully and user is redirected appropriately
3. **Given** user initiates federated logout, **When** system redirects to `/logout/federated-callback`, **Then** logout completes successfully (already kebab-case, verify no regression)
4. **Given** tenant-aware routing is enabled, **When** endpoints are accessed via `/t/{slug}/auth/external/start`, **Then** tenant context is preserved and authentication flows work correctly

---

### User Story 2 - Admin UI Navigation Links Migration (Priority: P2)

Platform administrators and tenant administrators navigate through admin interfaces using consistent kebab-case URLs. The system converts all admin area page routes and navigation links (`/Admin/*`, `/PlatformAdmin/*`) to kebab-case convention, ensuring menu items, breadcrumbs, and direct links use new convention.

**Why this priority**: Admin UI is used by trusted internal users who can be notified of URL changes. These are internal-facing pages with lower external dependency risk compared to protocol endpoints.

**Independent Test**: Can be fully tested by logging in as platform admin, navigating through all menu items in admin sidebar (Tenants, Impersonation, Providers, Clients, Users, Realms, Scopes, Branding, Settings), and verifying all URLs in browser address bar and all anchor `href` attributes use kebab-case.

**Acceptance Scenarios**:

1. **Given** platform admin is logged in, **When** admin clicks "Tenants" in navigation menu, **Then** browser navigates to `/platform-admin/tenants/index` (kebab-case)
2. **Given** tenant admin views provider list, **When** admin clicks "Edit" on a provider, **Then** browser navigates to `/admin/providers/edit?id={guid}` or `/t/{slug}/admin/providers/edit?id={guid}` in multi-tenant mode
3. **Given** admin performs form submission (create/edit/delete), **When** form posts and redirect occurs, **Then** redirect target URL uses kebab-case convention
4. **Given** tenant-aware routing is enabled, **When** admin navigates admin pages, **Then** all URLs include `/t/{slug}` prefix with kebab-case page paths

---

### User Story 3 - User-Facing Account Pages Migration (Priority: P2)

End users manage their accounts and authenticate using consistent kebab-case URLs. The system converts all user-facing page routes (`/Account/*`, `/Login`, `/Registrations/*`, `/Password/*`, `/Auth/*`) to kebab-case convention, ensuring authentication forms, account management pages, and email links use new convention.

**Why this priority**: These pages are user-facing but primarily accessed through redirects and email links that can be updated atomically. Users rarely bookmark these pages, reducing risk of broken bookmarks.

**Independent Test**: Can be fully tested by performing complete user journey: discover tenant → login → view account profile → manage WebAuthn credentials → manage sessions → logout. Verify all URLs in browser address bar, form actions, and email confirmation links use kebab-case.

**Acceptance Scenarios**:

1. **Given** user accesses login page, **When** user navigates to login, **Then** browser URL is `/login` or `/t/{slug}/login` in multi-tenant mode (already kebab-case, verify no regression)
2. **Given** user clicks "Forgot Password" link, **When** user navigates to password reset flow, **Then** browser navigates to `/password/index` (kebab-case)
3. **Given** user registers WebAuthn credential, **When** user accesses WebAuthn management page, **Then** browser navigates to `/account/webauthn` or `/auth/webauthn` (kebab-case)
4. **Given** user receives email confirmation email, **When** user clicks confirmation link in email, **Then** link URL uses kebab-case convention (e.g., `/account/confirm-email?token={token}`)

---

### User Story 4 - Programmatic URL Construction Migration (Priority: P1)

Application code constructs URLs dynamically using `TenantAwareUrlBuilder` and `RedirectToPage()` methods. The system updates all URL construction call sites to use kebab-case paths, ensuring programmatic redirects, email links, and API responses contain kebab-case URLs.

**Why this priority**: URL construction code is scattered throughout application and feeds into all user-facing scenarios. Getting this right ensures consistency across all code paths and prevents mixed convention bugs.

**Independent Test**: Can be fully tested by grepping codebase for `TenantAwareUrlBuilder.BuildTenantPath(` and `RedirectToPage(` calls, verifying all path arguments use kebab-case, then running full integration test suite to catch any missed construction sites.

**Acceptance Scenarios**:

1. **Given** code calls `TenantAwareUrlBuilder.BuildTenantPath("/Admin/Providers", ...)`, **When** code is updated to use kebab-case, **Then** call becomes `BuildTenantPath("/admin/providers", ...)`
2. **Given** Razor page model calls `RedirectToPage("/Admin/Index")`, **When** code is updated, **Then** call becomes `RedirectToPage("/admin/index")`
3. **Given** email service constructs confirmation URL, **When** `EmailConfirmationWorkflow` builds URL, **Then** resulting URL uses kebab-case paths
4. **Given** API endpoint constructs redirect URI for external IdP configuration, **When** `Edit.cshtml.cs` computes `RedirectUris`, **Then** resulting URIs use `/auth/external/callback` (kebab-case)

---

### User Story 5 - API Endpoint Routes Migration (Priority: P3)

External API consumers and frontend JavaScript call admin and public API endpoints. The system converts remaining PascalCase API routes to kebab-case convention, ensuring all REST endpoints (`/api/*`, `/admin/api/*`) consistently use kebab-case for resource segments.

**Why this priority**: Many `/admin/api/*` endpoints already use kebab-case. Remaining conversions are lower priority as API consumers are typically under our control and can be updated in coordination with API changes.

**Independent Test**: Can be fully tested by reviewing all `MapGet/MapPost/MapPut/MapDelete` calls in `AdminApiEndpointMappingExtensions.cs` and `EndpointMappingExtensions.cs`, verifying kebab-case, then running API integration tests.

**Acceptance Scenarios**:

1. **Given** frontend calls WebAuthn API, **When** frontend posts to `/api/webauthn/registration/challenge`, **Then** endpoint responds successfully (already kebab-case, verify no regression)
2. **Given** admin UI calls provider management API, **When** admin UI makes GET request to `/admin/api/providers`, **Then** API returns provider list (already kebab-case, verify no regression)
3. **Given** QR login flow uses status polling API, **When** frontend polls `/api/qr/status/{sessionToken}`, **Then** API returns status successfully (already kebab-case, verify no regression)

---

## Functional Requirements *(mandatory)*

### FR-001: Core OIDC Protocol Endpoint URL Conversion

Convert all OIDC protocol endpoints to kebab-case in endpoint mapping configuration:

- `/Auth/External/Start` → `/auth/external/start`
- `/Auth/External/Callback` → `/auth/external/callback`
- `/Auth/External/Confirm` → `/auth/external/confirm`
- `/Auth/QrMobile` → `/auth/qr-mobile`
- `/Auth/WebAuthn` → `/auth/webauthn`

Verify discovery document returns kebab-case endpoint URLs. Ensure tenant-aware routing correctly prefixes kebab-case paths with `/t/{slug}`.

### FR-002: Admin Page Route Conversion

Convert all admin area Razor page routes to kebab-case file paths and folder names:

- `/Admin/Providers/*` → `/admin/providers/*`
- `/Admin/Clients/*` → `/admin/clients/*`
- `/Admin/Users/*` → `/admin/users/*`
- `/Admin/Realms/*` → `/admin/realms/*`
- `/Admin/Scopes/*` → `/admin/scopes/*`
- `/Admin/Branding` → `/admin/branding`
- `/Admin/Settings` → `/admin/settings`
- `/Admin/Index` → `/admin/index`

Convert PlatformAdmin routes:

- `/PlatformAdmin/Index` → `/platform-admin/index`
- `/PlatformAdmin/Tenants/*` → `/platform-admin/tenants/*`
- `/PlatformAdmin/Impersonation` → `/platform-admin/impersonation`
- `/PlatformAdmin/ImpersonationHistory/*` → `/platform-admin/impersonation-history/*`

### FR-003: User-Facing Page Route Conversion

Convert all user-facing Razor page routes to kebab-case file paths and folder names:

- `/Account/Profile` → `/account/profile`
- `/Account/Sessions` → `/account/sessions`
- `/Account/WebAuthn` → `/account/webauthn`
- `/Password/Index` → `/password/index`
- `/Registrations/Index` → `/registrations/index`

Pages already in kebab-case (verify no regression):

- `/login` (remains `/login`)
- `/logout/federated-callback` (remains `/logout/federated-callback`)
- `/account/confirm-email` (remains `/account/confirm-email`)

### FR-004: Navigation Link Updates

Update all navigation links in layout files to reference kebab-case routes:

- `_Layout.cshtml`: Update all `asp-page` attributes in admin sidebar and user menu
- `_AuthLayout.cshtml`: Update any page references
- `_TenantContextBanner.cshtml`: Update tenant admin links
- `_ImpersonationBanner.cshtml`: Update impersonation control links
- Page-specific partial views: Update any cross-page navigation links

### FR-005: Programmatic URL Construction Updates

Update all URL construction call sites:

- `TenantAwareUrlBuilder.BuildTenantPath()` calls: Replace PascalCase path arguments with kebab-case
- `RedirectToPage()` calls in page models: Replace PascalCase page paths with kebab-case
- Email service URL construction: Update `EmailConfirmationWorkflow` and other email services
- External IdP redirect URI computation: Update `Edit.cshtml.cs` and similar pages

### FR-006: API Endpoint Route Review

Review all API endpoint routes and ensure consistent kebab-case:

- Verify all `/api/*` routes use kebab-case (most already do)
- Verify all `/admin/api/*` routes use kebab-case (most already do)
- Identify and convert any remaining PascalCase API segments

### FR-007: Tenant-Aware URL Construction Compatibility

Ensure `TenantAwareUrlBuilder` correctly handles kebab-case paths:

- Verify path normalization (leading slash) works with kebab-case
- Verify tenant prefix construction (`/t/{slug}`) works with kebab-case
- Test query string parameter handling with kebab-case base paths

### FR-008: Documentation and Configuration Updates

Update all documentation and configuration referencing old URL patterns:

- Developer guide: Update example URLs and code snippets
- Admin guide: Update screenshots and URL references
- Identity provider configuration documentation: Update redirect URI examples
- Spec documents and backlog items: Update URL references
- Test documentation: Update expected URL patterns

---

## Success Criteria *(mandatory)*

### SC-001: Zero PascalCase URLs in Routing Configuration

**Measurable outcome**: Grep search for `MapGet\(|MapPost\(|MapPut\(|MapDelete\(` in `*.cs` files returns zero matches containing PascalCase path segments (e.g., `/Admin/`, `/Account/`, `/Auth/External/`). All endpoint mappings use kebab-case paths.

### SC-002: Zero PascalCase URLs in Navigation Links

**Measurable outcome**: Grep search for `asp-page=` in `*.cshtml` files returns zero matches containing PascalCase page paths. All Razor tag helper page references use kebab-case paths (e.g., `asp-page="/admin/providers/edit"`).

### SC-003: Zero PascalCase URLs in Programmatic Construction

**Measurable outcome**: Grep search for `TenantAwareUrlBuilder\.BuildTenantPath\(|RedirectToPage\(` returns zero matches with PascalCase path string literals. All programmatic URL construction uses kebab-case paths.

### SC-004: Discovery Document Contains Only kebab-case URLs

**Measurable outcome**: HTTP GET request to `/.well-known/openid-configuration` returns JSON document where all endpoint URL values (authorization_endpoint, token_endpoint, userinfo_endpoint, etc.) contain only kebab-case path segments. No PascalCase segments remain.

### SC-005: Complete OIDC Flow Works with kebab-case Endpoints

**Measurable outcome**: Integration test performs full authorization code flow (discovery → authorize → token → userinfo) and federated login flow (start → external IdP → callback → confirm) using only kebab-case URLs. All redirects and callbacks succeed without manual URL intervention.

### SC-006: Admin UI Navigation Works with kebab-case Routes

**Measurable outcome**: Manual test navigating through all admin sidebar menu items (15+ pages) shows browser address bar contains only kebab-case URLs. All form submissions redirect to kebab-case URLs. All breadcrumbs and in-page links use kebab-case.

### SC-007: User Account Management Works with kebab-case Routes

**Measurable outcome**: Manual test of user journey (login → profile → WebAuthn → sessions → logout) shows browser address bar contains only kebab-case URLs throughout flow. Email confirmation link uses kebab-case URL.

### SC-008: Tenant-Aware Routing Works with kebab-case

**Measurable outcome**: With `MultiTenancyOptions.Enabled = true`, integration test performs admin and user flows via tenant-prefixed URLs (e.g., `/t/acme/admin/providers/edit`). All URLs maintain `/t/{slug}` prefix with kebab-case page paths. No mixed-case URLs appear.

### SC-009: All Unit and Integration Tests Pass

**Measurable outcome**: `dotnet test` command exits with code 0 (success) after all URL conversions. No tests fail due to hardcoded PascalCase URL expectations. Test output shows 0 failed, 0 skipped tests (excluding pre-existing skipped tests).

---

## Edge Cases

### EC-001: Mixed-Case URL Request Handling

**Scenario**: External client or user manually types URL with PascalCase segments (e.g., `/Admin/Providers`) or bookmarks contain old URLs.

**Expected behavior**: System returns 404 with helpful error page suggesting kebab-case alternatives. No case-insensitive routing middleware. This is a clean break - URLs are case-sensitive and must use lowercase kebab-case.

**Rationale**: Clean architectural decision with no backward compatibility complexity. Users and external integrations must update to new convention. Documentation and migration guide will clearly communicate the change.

---

### EC-002: External IdP and RP Client Redirect URI Updates

**Scenario**: External identity providers have registered callback URIs containing PascalCase paths (e.g., `https://mrwhooicd.example.com/Auth/External/Callback`). Relying party clients have registered `redirect_uri` values containing old URLs.

**Expected behavior**: Immediate breaking change with 30-day advance notice via email and documentation. External IdPs and RP clients must update their registered redirect URIs to use kebab-case paths. No dual endpoint support. Old PascalCase endpoints will return 404 after migration.

**Rationale**: Clean break allows faster migration without maintaining duplicate endpoints. 30-day notice period provides reasonable time for external parties to update configurations. Discovery document will immediately advertise only kebab-case URLs.

---

### EC-003: Deep Links and Email Confirmation URLs

**Scenario**: Existing email confirmation emails in user inboxes contain PascalCase confirmation URLs (e.g., `/Account/ConfirmEmail?token=...`). Bookmarked URLs in browser history contain PascalCase paths.

**Expected behavior**: All existing confirmation and password reset tokens are invalidated. Users with pending email confirmations or password resets must re-request new emails with kebab-case URLs. Old PascalCase confirmation links return 404 with helpful error message instructing users to request new confirmation email.

**Rationale**: Clean break ensures consistency. Email confirmation tokens are typically short-lived (24-48 hours), so impact is minimal. Users can easily re-request confirmation emails. Password reset tokens are also short-lived and can be re-requested.

---

## Key Entities

No new entities required. Existing entities remain unchanged:

- `Tenant` (unchanged)
- `IdentityProvider` (unchanged)
- `Client` (unchanged)
- `User` (unchanged)
- All OIDC entities (grant, token, consent, etc.) remain unchanged

Configuration entities:

- `OidcOptions` (unchanged, still provides `PublicBaseUrl`)
- `MultiTenancyOptions` (unchanged, still controls tenant-aware routing)

Utility classes:

- `TenantAwareUrlBuilder` (interface unchanged, handles kebab-case paths without modification)

---

## Assumptions

1. **ASP.NET Core Razor Pages routing**: Framework supports lowercase and hyphenated route segments natively. Physical file and folder names can remain PascalCase; `@page` directive and route templates control URL convention.

2. **Backward compatibility not required for internal admin pages**: Admin users can be notified of URL changes via email/docs. Bookmarked admin URLs breaking is acceptable with proper communication.

3. **External-facing endpoints require migration period**: Protocol endpoints (`/authorize`, `/token`, external auth callbacks) need dual support period to allow external IdPs and RP clients time to update configurations.

4. **Tenant-aware URL builder is path-agnostic**: `TenantAwareUrlBuilder` utility does not parse or validate path segments; it only handles tenant prefix insertion. Kebab-case paths work without code changes to builder.

5. **No database schema changes required**: URL convention is presentation-layer concern. No stored URLs in database requiring migration (except potentially stored redirect URIs in Client entity, which are already flexible strings).

6. **Discovery document is generated dynamically**: OIDC discovery endpoint builds JSON response from current routing configuration. When endpoint routes change to kebab-case, discovery document automatically reflects new URLs without separate configuration.

7. **Case-sensitive routing by default**: ASP.NET Core routing is case-sensitive by default. Implementing case-insensitive routing requires explicit middleware or route configuration changes.

8. **Tests contain hardcoded URL expectations**: Integration tests likely assert on specific URL patterns. These assertions need updating to expect kebab-case URLs.

9. **Email templates reference page URLs**: Email confirmation and password reset workflows construct URLs programmatically using `IUrlHelper` or similar. Updating page routes automatically updates email links.

10. **API endpoints already mostly kebab-case**: Review of codebase shows `/api/*` and `/admin/api/*` routes largely already use kebab-case. Minimal conversion needed for API layer.

---

## Open Questions / Needs Clarification

**All clarifications resolved** - Clean break approach confirmed by user:

- No backward compatibility
- No dual routes
- No case-insensitive routing
- 30-day advance notice for external parties
- Token invalidation for existing email links

