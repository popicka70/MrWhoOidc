# URL Convention Migration - Complete Mapping Reference

**Migration Date**: November 1, 2025  
**Feature Branch**: `002-url-kebab-case-conversion`  
**Status**: ✅ Complete (All 472 tests passing)

## Overview

This document provides a complete reference of all URL changes from PascalCase to kebab-case convention. All URLs have been systematically converted to improve readability, consistency, and adherence to modern web conventions.

## Convention Rules

- **Segments**: Use lowercase with hyphens (`kebab-case`)
- **Parameters**: Route parameters remain unchanged (`{id:guid}`, `{clientId}`)
- **Query strings**: Parameter names unchanged
- **OIDC endpoints**: Follow RFC standards (already lowercase)

## Core Protocol Endpoints (Phase 3)

### External OIDC Integration

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/Auth/External/Start` | `/auth/external/start` | Initiate external IdP login |
| `/Auth/External/Callback` | `/auth/external/callback` | Handle external IdP callback |
| `/Auth/External/Confirm` | `/auth/external/confirm` | Account linking confirmation |
| `/Auth/External/Error` | `/auth/external/error` | External auth error display |
| `/Auth/QrMobile` | `/auth/qr-mobile` | QR code mobile landing page |

### Already Compliant OIDC Endpoints

These endpoints were already using lowercase/kebab-case:

- `/.well-known/openid-configuration` (OIDC Discovery)
- `/jwks` (JSON Web Key Set)
- `/authorize` (Authorization endpoint)
- `/token` (Token endpoint)
- `/userinfo` (UserInfo endpoint)
- `/introspect` (Token introspection)
- `/revoke` (Token revocation)
- `/par` (Pushed Authorization Request)
- `/logout` (Logout entry)
- `/logout/federated-callback` (Federated logout callback)
- `/logout/final` (Final redirect after logout)
- `/connect/endsession` (Alternative logout endpoint)

## Admin UI Routes (Phase 5)

### Tenant Admin Pages

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/Admin/Index` | `/admin` | Admin dashboard |
| `/Admin/Settings` | `/admin/settings` | Tenant settings |
| `/Admin/Branding` | `/admin/branding` | Branding configuration |

### User Management

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/Admin/Users/Index` | `/admin/users` | User list |
| `/Admin/Users/Add` | `/admin/users/add` | Create new user |
| `/Admin/Users/Edit/{id}` | `/admin/users/edit/{id}` | Edit user |
| `/Admin/Users/Emails/Index/{id}` | `/admin/users/emails/{id}` | Manage user emails |
| `/Admin/Users/Linked/Index/{id}` | `/admin/users/linked/{id}` | Linked accounts |
| `/Admin/Users/Roles/Index/{id}` | `/admin/users/roles/{id}` | User role assignments |
| `/Admin/Users/Clients/Index/{id}` | `/admin/users/clients/{id}` | User client access |

### Client Management

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/Admin/Clients/Index` | `/admin/clients` | Client applications list |
| `/Admin/Clients/Add` | `/admin/clients/add` | Register new client |
| `/Admin/Clients/Edit/{id}` | `/admin/clients/edit/{id}` | Edit client configuration |
| `/Admin/ClientKeys/Index/{clientId}` | `/admin/client-keys/{clientId}` | Manage client signing keys |

### Identity Provider Management

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/Admin/Providers/Index` | `/admin/providers` | External IdP list |
| `/Admin/Providers/Add` | `/admin/providers/add` | Add new IdP |
| `/Admin/Providers/Edit/{id}` | `/admin/providers/edit/{id}` | Edit IdP configuration |
| `/Admin/Providers/Delete/{id}` | `/admin/providers/delete/{id}` | Delete IdP |
| `/Admin/Providers/ClaimMappings/{id}` | `/admin/providers/claim-mappings/{id}` | Claim mapping rules |
| `/Admin/Providers/Details/{id}` | `/admin/providers/details/{id}` | IdP details view |
| `/Admin/ProviderMappings/Index` | `/admin/provider-mappings` | Client-provider associations |
| `/Admin/ProviderKeys/Index/{providerId}` | `/admin/provider-keys/{providerId}` | Provider signing keys |
| `/Admin/ProviderClaimMappings/Index/{providerId}` | `/admin/provider-claim-mappings/{providerId}` | Provider claim configs |
| `/Admin/ProviderClaimMappings/Edit/{id}` | `/admin/provider-claim-mappings/edit/{id}` | Edit claim mapping |

### Realm, Scope, and Role Management

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/Admin/Realms/Index` | `/admin/realms` | Realm list |
| `/Admin/Realms/Add` | `/admin/realms/add` | Create realm |
| `/Admin/Realms/Edit/{id}` | `/admin/realms/edit/{id}` | Edit realm |
| `/Admin/Scopes/Index` | `/admin/scopes` | OAuth scope list |
| `/Admin/Scopes/Add` | `/admin/scopes/add` | Create scope |
| `/Admin/Scopes/Edit/{id}` | `/admin/scopes/edit/{id}` | Edit scope |
| `/Admin/Roles/Index` | `/admin/roles` | Role list |
| `/Admin/Roles/Add` | `/admin/roles/add` | Create role |
| `/Admin/Roles/Edit/{id}` | `/admin/roles/edit/{id}` | Edit role |

### Advanced Features

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/Admin/Registrations/Index` | `/admin/registrations` | Self-service registration approvals |
| `/Admin/Backchannel/Index` | `/admin/backchannel` | Back-channel logout monitoring |
| `/Admin/License/Index` | `/admin/license` | License management |
| `/Admin/License/Install` | `/admin/license/install` | Install license key |
| `/Admin/License/History` | `/admin/license/history` | License history |

## Platform Admin Routes (Phase 5)

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/PlatformAdmin/Index` | `/platform-admin` | Platform dashboard |
| `/PlatformAdmin/Tenants/Index` | `/platform-admin/tenants` | Tenant list |
| `/PlatformAdmin/Tenants/Edit/{id}` | `/platform-admin/tenants/edit/{id}` | Edit tenant |
| `/PlatformAdmin/Tenants/Create` | `/platform-admin/tenants/create` | Create tenant |
| `/PlatformAdmin/Impersonation` | `/platform-admin/impersonation` | User impersonation |
| `/PlatformAdmin/ImpersonationHistory` | `/platform-admin/impersonation-history` | Impersonation audit log |

## User Account Pages (Phase 6)

### Account Management

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/Account/Index` | `/account` | Account overview |
| `/Account/Profile` | `/account/profile` | Edit profile |
| `/Account/Emails` | `/account/emails` | Manage email addresses |
| `/Account/LinkedAccounts` | `/account/linked-accounts` | External identity links |
| `/Account/Sessions` | `/account/sessions` | Active session management |
| `/Account/Consents` | `/account/consents` | Application consents |
| `/Account/WebAuthn` | `/account/webauthn` | WebAuthn credentials |
| `/Account/AccessDenied` | `/account/access-denied` | Authorization denied page |

### Already Compliant Account Pages

| URL | Purpose |
|-----|---------|
| `/account/confirm-email` | Email confirmation (already kebab-case) |

### Authentication Pages

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/Auth/Qr` | `/auth/qr` | QR code login initiation |
| `/Auth/QrConfirm` | `/auth/qr-confirm` | QR code confirmation |
| `/Auth/WebAuthn` | `/auth/webauthn` | WebAuthn authentication |
| `/Auth/Providers/Select` | `/auth/providers/select` | External IdP picker |

### Password & MFA

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/Password/Index` | `/password` | Password management |
| `/Mfa/Index` | `/mfa` | MFA enrollment |

### Logout Pages

| Before (PascalCase) | After (kebab-case) | Purpose |
|---------------------|-------------------|---------|
| `/Logout/Prompt/Index` | `/logout/prompt` | Logout confirmation |
| `/Logout/FederatedSignedOut` | `/logout/federated-signed-out` | Federated logout success |
| `/Logout/FederatedCallbackError` | `/logout/federated-callback-error` | Federated logout error |

## API Endpoints (Phase 7)

All API endpoints were already using kebab-case. Verified during Phase 7.

### WebAuthn API

| URL | Method | Purpose |
|-----|--------|---------|
| `/api/webauthn/registration/challenge` | POST | Get registration challenge |
| `/api/webauthn/registration/complete` | POST | Complete registration |
| `/api/webauthn/authentication/challenge` | POST | Get authentication challenge |
| `/api/webauthn/authentication/complete` | POST | Complete authentication |
| `/api/webauthn/credentials` | GET | List user credentials |
| `/api/webauthn/credentials/{credentialId}` | DELETE | Remove credential |

### QR Login API

| URL | Method | Purpose |
|-----|--------|---------|
| `/api/qr/status/{sessionToken}` | GET | Poll QR session status |
| `/api/qr/confirm` | POST | Confirm QR login |
| `/api/qr/cancel` | POST | Cancel QR session |

### Icon API

| URL | Method | Purpose |
|-----|--------|---------|
| `/api/icon/{iconId:guid}` | GET | Retrieve provider icon |

### Admin API Endpoints

| URL | Method | Purpose |
|-----|--------|---------|
| `/admin/api/providers` | GET | List providers |
| `/admin/api/providers/{id}` | GET/PUT/DELETE | Manage provider |
| `/admin/api/clients/{clientId}/providers` | GET | Client provider assignments |
| `/admin/api/providers/{providerId}/claim-mappings` | GET/POST | Claim mappings |
| `/admin/api/providers/{providerId}/keys` | GET | Provider JWKS |
| `/admin/api/clients/{clientId}/keys` | GET | Client JWKS |
| `/admin/api/bcl/alerts/snapshot` | GET | Backchannel alerts |
| `/admin/api/bcl/outbox` | GET | Backchannel outbox |
| `/health/backchannel` | GET | Backchannel health |

## Tenant-Aware URL Pattern

All admin and user pages support tenant-aware routing:

### Pattern

```
/t/{tenant-slug}/{route}
```

### Examples

| Global Route | Tenant-Aware Route | Context |
|--------------|-------------------|---------|
| `/admin/users` | `/t/acme-corp/admin/users` | ACME Corp tenant |
| `/account/profile` | `/t/acme-corp/account/profile` | User profile in ACME Corp |
| `/admin/clients` | `/t/default/admin/clients` | Default tenant clients |

## Migration Strategy

- **Approach**: Clean break (no backward compatibility)
- **Notice Period**: 30 days advance notice to external parties
- **Cutover**: Single deployment (November 1, 2025)
- **Custom 404 Handler**: Detects PascalCase URLs, suggests kebab-case equivalent
- **Test Coverage**: 472 tests updated and passing

## Impact on External Parties

### Relying Parties (RPs)

**No impact** - Standard OIDC endpoints already compliant:
- Discovery URL: `/.well-known/openid-configuration`
- Callback URIs: `/auth/external/callback` (updated from `/Auth/External/Callback`)

### External Identity Providers

**Action Required**:
- Update registered callback URLs from `/Auth/External/Callback` to `/auth/external/callback`
- Verify using discovery document post-migration

## Documentation Updates

All documentation has been updated to reflect kebab-case URLs (November 2025). Legacy PascalCase URLs in docs are historical references only.

## Custom 404 Handler

The custom 404 handler (implemented in Phase 2) provides intelligent suggestions:

### Example

**Request**: `GET /Account/Profile` (PascalCase)  
**Response**: 404 with suggestion banner:

> **Did you mean:** [/account/profile](/account/profile)?
> 
> URLs now use kebab-case. Please update your bookmarks.

## Related Documents

- **Deployment Checklist**: `specs/002-url-kebab-case-conversion/deployment-checklist.md`
- **Rollback Procedure**: `specs/002-url-kebab-case-conversion/rollback.md`
- **Notification Templates**: `specs/002-url-kebab-case-conversion/notification-template.md`
- **Tasks**: `specs/002-url-kebab-case-conversion/tasks.md` (148 tasks - 100% complete)

## Technical Implementation

- **@page Directives**: All 54 Razor Pages updated with explicit kebab-case routes
- **Endpoint Mapping**: 4 minimal API endpoints updated in `EndpointMappingExtensions.cs`
- **Helper Utility**: `UrlConversionHelper.cs` provides ToKebabCase() conversion
- **Test Updates**: 10 test files updated with kebab-case assertions
- **Snapshot Regeneration**: `endpoint-manifest.snapshot.json` regenerated

## Verification

✅ **Code**: Zero PascalCase URLs in production code  
✅ **Tests**: All 472 tests passing  
✅ **API**: All API endpoints verified kebab-case  
✅ **Build**: Clean compilation (0 errors)  
✅ **Documentation**: URL mappings documented

---

**Document Status**: ✅ Complete  
**Last Updated**: November 1, 2025  
**Migration Status**: Deployed to branch `002-url-kebab-case-conversion`
