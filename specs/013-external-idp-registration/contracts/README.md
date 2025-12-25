# API Contracts: External IdP Registration

**Feature**: 013-external-idp-registration  
**Date**: 2025-12-25

## Overview

This feature primarily extends existing UI (Razor Pages) rather than creating new REST APIs. The contracts below document the page interactions and any API extensions.

---

## Page Contracts

### Registration Page - GET /Registrations

**Purpose**: Display registration form with external IdP options.

**Query Parameters**:

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `returnUrl` | string | No | URL to redirect after successful registration |
| `mode` | string | No | Flow mode: `idp_callback` when returning from external IdP |

**Response**: HTML page with:

- Manual registration form (email, name, password)
- External IdP buttons (when `AllowRegistration` IdPs exist)
- Tenant creation option (manual registration only in P1)

**Model Properties**:

```csharp
public class IndexModel
{
    // Existing properties
    public RegistrationInput Input { get; set; }
    public string? ReturnUrl { get; set; }
    public string? SuccessMessage { get; set; }
    public string? InfoMessage { get; set; }
    
    // NEW: IdP options for registration
    public List<RegistrationIdpOption> RegistrationIdps { get; set; }
}

public sealed record RegistrationIdpOption
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? LogoUrl { get; init; }
}
```

---

### External Start - GET /Auth/External/Start

**Purpose**: Initiate external IdP authentication (existing endpoint, reused).

**Query Parameters**:

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `provider` | string | Yes | IdP name (e.g., "google", "microsoft") |
| `clientId` | string | Yes | OAuth client ID for context |
| `returnUrl` | string | Yes | Return URL after authentication |

**Registration-Specific Usage**:

```text
/Auth/External/Start
  ?provider=google
  &clientId={defaultClientId}
  &returnUrl=/Registrations?mode=idp_callback&originalReturn={encoded}
```

**Response**: 302 Redirect to external IdP authorization endpoint.

---

### External Callback - GET /Auth/External/Callback

**Purpose**: Handle external IdP authentication callback (existing endpoint).

**Behavior for Registration Flow**:

1. Validate callback (code, state)
2. Exchange code for tokens
3. Extract user claims (email, name)
4. Call `ExternalOidcUserProvisioner.ProvisionOrLinkUserAsync()`
5. If new user created: Redirect to returnUrl with success
6. If user exists: Redirect with message to sign in instead

---

## Admin API Extension

### Update Identity Provider - PUT /admin/api/providers/{id}

**Extended Request Body**:

```json
{
  "name": "string",
  "displayName": "string",
  "enabled": true,
  "isDefault": false,
  "allowRegistration": true,  // NEW
  "sortOrder": 0,
  "logoUrl": "string",
  "type": "Oidc",
  "config": { }
}
```

**New Field**:

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `allowRegistration` | boolean | No | `false` | Show IdP on registration page |

---

## Admin Page Contract

### Provider Edit - GET/POST /Admin/Providers/Edit/{id}

**Extended Input Model**:

```csharp
public class InputModel
{
    // Existing properties
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; }
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
    public string? LogoUrl { get; set; }
    
    // NEW
    public bool AllowRegistration { get; set; }
}
```

---

## Error Responses

### Registration Page Errors

| Scenario | Display |
|----------|---------|
| User already exists | "An account with this email already exists. [Sign in instead]" |
| Missing required claims | "Unable to complete registration. The identity provider did not provide required information (email). Please try manual registration." |
| IdP authentication failed | "Authentication failed. Please try again or use manual registration." |
| IdP configuration error | "This identity provider is temporarily unavailable." |

---

## Sequence Diagram

```text
User                   Registration Page       External Start       External IdP       Callback           Provisioner
 │                          │                       │                    │                │                    │
 │─── GET /Registrations ──►│                       │                    │                │                    │
 │                          │                       │                    │                │                    │
 │◄── Page with IdP btns ───│                       │                    │                │                    │
 │                          │                       │                    │                │                    │
 │─── Click "Sign up with  ─┼──► GET /External/Start│                    │                │                    │
 │    Google" button        │                       │                    │                │                    │
 │                          │                       │─── Redirect ──────►│                │                    │
 │                          │                       │                    │                │                    │
 │◄───────────────────────────────────── User authenticates ─────────────│                │                    │
 │                          │                       │                    │                │                    │
 │                          │                       │◄── Code callback ──│                │                    │
 │                          │                       │                    │                │                    │
 │                          │                       │────────────────────┼───► Callback ──┼──► Provision user  │
 │                          │                       │                    │                │                    │
 │◄───────────────────────────────────── Redirect to /Registrations?mode=idp_callback ────│                    │
 │                          │                       │                    │                │                    │
 │─── GET /Registrations?mode=idp_callback ────────►│                    │                │                    │
 │                          │                       │                    │                │                    │
 │◄── Success message ──────│                       │                    │                │                    │
```
