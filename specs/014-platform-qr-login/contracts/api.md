# API Contracts: Platform QR Login

**Feature**: 014-platform-qr-login  
**Date**: 2025-12-26

## Overview

This feature primarily uses Razor Pages for UI, not REST APIs. The contracts below document:

1. The new Platform Settings page endpoints (form-based)
2. Reused QR login API endpoints (existing)

## Platform Settings Page

### GET /platform-admin/settings

**Description**: Display platform settings form  
**Authorization**: `platform-admin` policy  
**Handler**: `OnGetAsync()`

**Response**: Razor Page HTML with form containing:

- QR Login at Discovery toggle (checkbox)
- Save button

### POST /platform-admin/settings

**Description**: Update platform settings  
**Authorization**: `platform-admin` policy  
**Handler**: `OnPostAsync()`  
**CSRF**: Antiforgery token required

**Request Form Fields**:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `QrLoginAtDiscoveryEnabled` | bool | No | Enable QR login on DiscoverTenant page |

**Response**:

- Success: Redirect to same page with success message (TempData)
- Validation error: Page with validation errors displayed

## Reused QR Login Endpoints (Existing)

These endpoints are already implemented and will be reused for DiscoverTenant QR flow:

### POST /api/qr/initiate

**Description**: Create new QR login session  
**Authorization**: None (public endpoint)  
**Note**: For DiscoverTenant, `client_id` will be omitted

**Request Query Parameters**:

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `client_id` | string | No | Client ID (null for DiscoverTenant) |
| `returnUrl` | string | No | URL to redirect after auth |
| `state` | string | No | OAuth state parameter |
| `nonce` | string | No | OAuth nonce parameter |
| `scope` | string | No | Requested scopes |

**Response** (200 OK):

```json
{
  "sessionToken": "abc123...",
  "qrCodeDataUri": "data:image/png;base64,...",
  "expiresAt": "2025-12-26T12:05:00Z",
  "pollUrl": "/api/qr/status/abc123..."
}
```

### GET /api/qr/status/{sessionToken}

**Description**: Poll QR session status  
**Authorization**: None (public endpoint)

**Response** (200 OK):

```json
{
  "status": "pending|scanned|authenticated|expired|cancelled",
  "message": "Waiting for mobile device...",
  "redirectUrl": "/dashboard"  // Only when status=authenticated
}
```

### POST /api/qr/cancel

**Description**: Cancel QR login session  
**Authorization**: None (public endpoint)

**Request Form Fields**:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `sessionToken` | string | Yes | Session to cancel |

**Response** (200 OK): Empty

## Service Interfaces

### IPlatformSettingsService

```csharp
public interface IPlatformSettingsService
{
    /// <summary>
    /// Gets platform settings, creating default if not exists.
    /// </summary>
    Task<PlatformSettings> GetSettingsAsync();

    /// <summary>
    /// Updates platform settings.
    /// </summary>
    /// <param name="settings">Settings to save</param>
    /// <param name="updatedBy">User making the change</param>
    Task UpdateSettingsAsync(PlatformSettings settings, string? updatedBy);

    /// <summary>
    /// Checks if QR login at discovery is enabled.
    /// </summary>
    Task<bool> IsQrLoginAtDiscoveryEnabledAsync();
}
```

## DiscoverTenant Page Changes

### GET /DiscoverTenant

**Existing behavior** (unchanged):

- Display email input form
- Display external IdP list (if configured)
- Handle returnUrl parameter

**New behavior** (conditional):

- If `IPlatformSettingsService.IsQrLoginAtDiscoveryEnabledAsync()` returns true:
  - Display "Sign in with QR Code" button
  - Button links to `/auth/qr?returnUrl={currentReturnUrl}`

### Page Model Changes

```csharp
public class DiscoverTenantModel : PageModel
{
    // Existing dependencies...
    private readonly IPlatformSettingsService _platformSettings;

    // New property
    public bool ShowQrLogin { get; set; }

    public async Task OnGetAsync()
    {
        // Existing logic...
        
        // New: Check platform setting
        ShowQrLogin = await _platformSettings.IsQrLoginAtDiscoveryEnabledAsync();
    }
}
```

## Error Responses

### Platform Settings Page Errors

| Scenario | HTTP Status | User Feedback |
|----------|-------------|---------------|
| Not authorized | 403 | Redirect to access denied |
| Save failed | 200 | Page with error message |
| Database error | 500 | Generic error page |

### QR API Errors (Existing)

| Scenario | HTTP Status | Response |
|----------|-------------|----------|
| Session not found | 404 | `{"error": "Session not found"}` |
| QR disabled | 400 | `{"error": "QR login is not enabled"}` |
| Invalid parameters | 400 | `{"error": "Missing required parameters"}` |
