# Phase 7.1: Dynamic Client Registration (RFC 7591 / RFC 7592)

**Date:** 2025-01-27  
**Status:** ✅ Complete  
**Tests:** 21 passing

## Overview

Implemented OAuth 2.0 Dynamic Client Registration Protocol (RFC 7591) and OAuth 2.0 Dynamic Client Registration Management Protocol (RFC 7592), enabling clients to programmatically register and manage their registrations.

## Implementation Details

### RFC 7591 - Dynamic Client Registration

**Endpoint:** `POST /register` (and tenant-scoped: `POST /t/{slug}/register`)

The registration endpoint was already partially implemented in `RegistrationHandler.cs`. Enhanced with:

1. **Feature Flag Protection** - Registration can be enabled/disabled via `AuthOptions.EnableDynamicClientRegistration`
2. **Initial Access Token Support** - Optional pre-authorization via `RequireInitialAccessToken` and `InitialAccessTokenHashes`
3. **Redirect URI Validation** - Configurable scheme requirements and localhost exceptions
4. **Registration Access Token Generation** - SHA-256 hashed tokens for client configuration access

### RFC 7592 - Client Configuration Management

**New Handler:** `ClientConfigurationHandler.cs` (~330 lines)

**Endpoints:**
- `GET /register/{clientId}` - Read client configuration
- `PUT /register/{clientId}` - Update client configuration
- `DELETE /register/{clientId}` - Delete client registration

Features:
- Bearer token authentication via `registration_access_token`
- Feature flag: `EnableClientConfigurationEndpoint`
- Token expiration handling
- Full CORS support for cross-origin clients

## Files Changed

### New Files
- [MrWhoOidc.Auth/Handlers/ClientConfigurationHandler.cs](../../MrWhoOidc.Auth/Handlers/ClientConfigurationHandler.cs) - RFC 7592 implementation
- [MrWhoOidc.UnitTests/DynamicClientRegistrationTests.cs](../../MrWhoOidc.UnitTests/DynamicClientRegistrationTests.cs) - 21 unit tests

### Modified Files
- [MrWhoOidc.Auth/Handlers/RegistrationHandler.cs](../../MrWhoOidc.Auth/Handlers/RegistrationHandler.cs) - Added feature flags, initial access token validation
- [MrWhoOidc.Auth/AuthOptions.cs](../../MrWhoOidc.Auth/AuthOptions.cs) - Added 7 new configuration properties
- [MrWhoOidc.Auth/Handlers/DiscoveryHandler.cs](../../MrWhoOidc.Auth/Handlers/DiscoveryHandler.cs) - Conditional registration_endpoint in discovery
- [MrWhoOidc.WebAuth/Extensions/EndpointMappingExtensions.cs](../../MrWhoOidc.WebAuth/Extensions/EndpointMappingExtensions.cs) - New RFC 7592 routes
- [MrWhoOidc.WebAuth/Extensions/PersistenceAndCoreExtensions.cs](../../MrWhoOidc.WebAuth/Extensions/PersistenceAndCoreExtensions.cs) - DI registration
- [MrWhoOidc.UnitTests/Snapshots/endpoint-manifest.snapshot.json](../../MrWhoOidc.UnitTests/Snapshots/endpoint-manifest.snapshot.json) - Updated with new endpoints

## Configuration Options

```json
{
  "Auth": {
    "EnableDynamicClientRegistration": true,
    "EnableClientConfigurationEndpoint": true,
    "RequireInitialAccessToken": false,
    "InitialAccessTokenHashes": [],
    "RegistrationAccessTokenLifetimeSeconds": 86400,
    "DynamicClientAllowedSchemes": ["https"],
    "DynamicClientAllowLocalhostHttp": true
  }
}
```

### Configuration Details

| Property | Default | Description |
|----------|---------|-------------|
| `EnableDynamicClientRegistration` | `false` | Enable RFC 7591 POST /register |
| `EnableClientConfigurationEndpoint` | `true` | Enable RFC 7592 GET/PUT/DELETE (requires 7591) |
| `RequireInitialAccessToken` | `false` | Require Bearer token for initial registration |
| `InitialAccessTokenHashes` | `[]` | SHA-256 hashes of valid initial access tokens |
| `RegistrationAccessTokenLifetimeSeconds` | `86400` | Token lifetime (0 = never expires) |
| `DynamicClientAllowedSchemes` | `["https"]` | Allowed URI schemes for redirect_uris |
| `DynamicClientAllowLocalhostHttp` | `true` | Allow http://localhost for development |

## Test Coverage

21 comprehensive tests covering:
- Feature flag checks (disabled returns 404)
- Initial access token validation
- Registration access token authentication
- Redirect URI scheme validation
- Localhost HTTP exceptions
- Client CRUD operations (GET/PUT/DELETE)
- Token expiration handling
- Error responses (invalid_token, invalid_client_metadata)

## Security Considerations

1. **Token Hashing** - All tokens (initial access and registration access) are stored as SHA-256 hashes
2. **Scheme Restrictions** - Only https allowed by default (with localhost exception for development)
3. **Feature Flags** - Both features disabled by default for defense in depth
4. **Rate Limiting** - Inherits `rl-authorize` rate limiter for POST /register
5. **CORS Support** - Full CORS for cross-origin clients

## Discovery Metadata

When enabled, the discovery document includes:
```json
{
  "registration_endpoint": "https://your-issuer/register"
}
```

## Example Usage

### Register a New Client

```http
POST /register HTTP/1.1
Content-Type: application/json
Authorization: Bearer initial-access-token  # optional, if RequireInitialAccessToken=true

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "grant_types": ["authorization_code"],
  "response_types": ["code"],
  "token_endpoint_auth_method": "client_secret_basic"
}
```

Response:
```json
{
  "client_id": "abc123",
  "client_secret": "secret456",
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "registration_access_token": "rat_xyz789",
  "registration_client_uri": "https://your-issuer/register/abc123"
}
```

### Read Client Configuration

```http
GET /register/abc123 HTTP/1.1
Authorization: Bearer rat_xyz789
```

### Update Client Configuration

```http
PUT /register/abc123 HTTP/1.1
Content-Type: application/json
Authorization: Bearer rat_xyz789

{
  "client_name": "My Renamed App",
  "redirect_uris": ["https://myapp.example.com/callback", "https://myapp.example.com/callback2"]
}
```

### Delete Client Registration

```http
DELETE /register/abc123 HTTP/1.1
Authorization: Bearer rat_xyz789
```

Response: `204 No Content`

## Compliance

- ✅ RFC 7591 - OAuth 2.0 Dynamic Client Registration Protocol
- ✅ RFC 7592 - OAuth 2.0 Dynamic Client Registration Management Protocol
- ✅ OIDC Discovery - `registration_endpoint` metadata (conditional)
