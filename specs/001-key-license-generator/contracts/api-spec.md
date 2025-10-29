# API Contracts: Key and License Management Service

**Date**: October 28, 2025  
**Feature**: Key and License Management Service  
**Branch**: 001-key-license-generator

## Overview

This document defines the HTTP API contracts for the Key and License Management Service. The service uses Razor Pages for UI and minimal APIs for downloads.

## Base URL

```text
http://localhost:8080 (development)
https://keygen.example.com (production)
```

## Authentication

Not implemented in MVP. Service assumes deployment in secure environment (VPN, internal network, or behind reverse proxy with authentication).

## Endpoints

### 1. Key Generation (Razor Page)

**Page**: `/KeyGeneration/Generate`

**Method**: GET (form display), POST (form submission)

**Description**: Generate a new RSA or ECDSA key pair for OIDC client use.

#### POST Request (Form Data)

```text
Algorithm: string (required)     # RS256, RS384, RS512, ES256, ES384, ES512, PS256
KeyType: string (required)       # RSA or EC
KeySize: int? (optional)         # 2048, 3072, 4096 (required if KeyType=RSA)
Curve: string? (optional)        # P-256, P-384, P-521 (required if KeyType=EC)
```

#### Response (Success)

**HTTP 200 OK**

Page displays:

- Generated `kid` (key identifier)
- Algorithm and key type/size/curve
- Download links for private key (JWK) and public key (JWKS)
- Warning: "Private key will only be available once. Download immediately."

#### Response (Validation Error)

**HTTP 200 OK** (page reload with validation errors)

Validation errors displayed:

- "Algorithm is required"
- "Key type is required"
- "Key size required for RSA keys"
- "Curve required for EC keys"
- "Invalid algorithm"
- "Invalid key size (must be 2048, 3072, or 4096)"
- "Invalid curve (must be P-256, P-384, or P-521)"

### 2. Download Private Key (Minimal API)

**Endpoint**: `/api/keys/{kid}/private`

**Method**: GET

**Description**: Download the private key in JWK format (one-time operation).

#### Request

```text
GET /api/keys/7f9c8b3a-5d2e-4f1b-9c8a-3e5d7f9c8b3a/private
```

#### Response (Success)

**HTTP 200 OK**

```json
Content-Type: application/json
Content-Disposition: attachment; filename="private-key-{kid}.jwk"

{
  "kty": "RSA",
  "kid": "7f9c8b3a-5d2e-4f1b-9c8a-3e5d7f9c8b3a",
  "alg": "RS256",
  "use": "sig",
  "n": "0vx7agoebGcQSuuPiLJXZptN9...",
  "e": "AQAB",
  "d": "X4cTteJY_gn4FYPsXB8rdXix5...",
  "p": "83i-7IvMGXoMXCskv73TKr8Z...",
  "q": "3dfOR9cuYq-0S-mkFLzgItgM...",
  "dp": "G4sPXkc6Ya9y8oJW9_ILj4...",
  "dq": "s9lAH9fggBsoFR8Oac2R_E...",
  "qi": "GyM_p6JrXySiz1toFgKbWV..."
}
```

#### Response (Key Not Found)

**HTTP 404 Not Found**

```json
{
  "error": "key_not_found",
  "error_description": "Key with ID 'invalid-kid' not found"
}
```

#### Response (Key Revoked)

**HTTP 403 Forbidden**

```json
{
  "error": "key_revoked",
  "error_description": "Key has been revoked and cannot be downloaded"
}
```

### 3. Download Public Key (Minimal API)

**Endpoint**: `/api/keys/{kid}/public`

**Method**: GET

**Description**: Download the public key in JWKS format.

#### Request

```text
GET /api/keys/7f9c8b3a-5d2e-4f1b-9c8a-3e5d7f9c8b3a/public
```

#### Response (Success)

**HTTP 200 OK**

```json
Content-Type: application/json
Content-Disposition: attachment; filename="public-key-{kid}.jwks"

{
  "keys": [
    {
      "kty": "RSA",
      "kid": "7f9c8b3a-5d2e-4f1b-9c8a-3e5d7f9c8b3a",
      "alg": "RS256",
      "use": "sig",
      "n": "0vx7agoebGcQSuuPiLJXZptN9...",
      "e": "AQAB"
    }
  ]
}
```

#### Response (Key Not Found)

**HTTP 404 Not Found**

```json
{
  "error": "key_not_found",
  "error_description": "Key with ID 'invalid-kid' not found"
}
```

### 4. Key Management Dashboard (Razor Page)

**Page**: `/KeyGeneration/List`

**Method**: GET

**Description**: View list of all generated keys with metadata and status.

#### Response

**HTTP 200 OK**

Page displays table with columns:

- Kid (key identifier)
- Algorithm (RS256, ES256, etc.)
- Key Type (RSA/EC)
- Key Size/Curve
- Created At (timestamp)
- Status (Active/Revoked)
- Download Count
- Actions (View Details, Revoke, Download Public Key)

Query parameters:

- `status` (optional): Filter by status (active, revoked, all). Default: active
- `algorithm` (optional): Filter by algorithm (RS256, ES256, etc.)
- `page` (optional): Page number for pagination. Default: 1
- `pageSize` (optional): Items per page. Default: 20

### 5. Revoke Key (Razor Page POST)

**Page**: `/KeyGeneration/Revoke`

**Method**: POST

**Description**: Mark a key as revoked (prevents private key downloads).

#### Request (Form Data)

```text
Kid: string (required)    # Key identifier to revoke
```

#### Response (Success)

**HTTP 302 Found** (redirect to `/KeyGeneration/List`)

Flash message: "Key {kid} has been revoked successfully"

#### Response (Key Not Found)

**HTTP 404 Not Found**

Error message: "Key not found"

### 6. License Generation (Razor Page)

**Page**: `/LicenseGeneration/Generate`

**Method**: GET (form display), POST (form submission)

**Description**: Generate a license token (JWT) signed with licensing private key.

#### POST Request (Form Data)

```text
Tier: string (required)               # community, professional, enterprise
Organization: string? (optional)      # Organization name
ValidFrom: DateTime? (optional)       # Validity start (default: now)
ValidUntil: DateTime? (optional)      # Validity end (required if ValidDays not set)
ValidDays: int? (optional)            # Days from now (alternative to ValidUntil)
Features: string? (optional)          # Comma-separated features (e.g., "analytics,dpop,multi-tenant")
Limits: string? (optional)            # JSON object string (e.g., {"tenants":50,"users":1000})
```

#### Response (Success)

**HTTP 200 OK**

Page displays:

- Generated license token (JWT)
- Token details (tier, organization, validity period, features, limits)
- Download link for JWT file
- Copy-to-clipboard button

#### Response (Validation Error)

**HTTP 200 OK** (page reload with validation errors)

Validation errors displayed:

- "Tier is required"
- "Invalid tier (must be community, professional, or enterprise)"
- "ValidUntil or ValidDays is required"
- "ValidFrom must be before ValidUntil"
- "Features must be comma-separated values"
- "Limits must be valid JSON object"
- "Licensing private key not configured"

### 7. Download License Token (Minimal API)

**Endpoint**: `/api/licenses/{tokenId}/download`

**Method**: GET

**Description**: Download the license token JWT file.

#### Request

```text
GET /api/licenses/7f9c8b3a5d2e4f1b9c8a3e5d7f9c8b3a/download
```

#### Response (Success)

**HTTP 200 OK**

```text
Content-Type: text/plain
Content-Disposition: attachment; filename="license-{organization}-{tokenId}.jwt"

eyJhbGciOiJFUzI1NiIsImtpZCI6ImxpY2Vuc2luZy1wcml2YXRlLWtleSJ9.eyJpc3MiOiJNcldob09pZGMtTGljZW5zZS1BdXRob3JpdHkiLCJuYmYiOjE3MzAxMjM0NTYsImlhdCI6MTczMDEyMzQ1NiwiZXhwIjoxNzYxNjU5NDU2LCJqdGkiOiI3ZjljOGIzYTVkMmU0ZjFiOWM4YTNlNWQ3ZjljOGIzYSIsInRpZXIiOiJlbnRlcnByaXNlIiwib3JnYW5pemF0aW9uIjoiQ29udG9zbyIsImZlYXR1cmVzIjpbImFuYWx5dGljcyIsImRwb3AiLCJtdWx0aS10ZW5hbnQiXSwibGltaXRzIjp7InRlbmFudHMiOjUwLCJ1c2VycyI6MTAwMH19.dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk
```

#### Response (Token Not Found)

**HTTP 404 Not Found**

```json
{
  "error": "license_not_found",
  "error_description": "License token not found"
}
```

### 8. Health Check (Minimal API)

**Endpoint**: `/health`

**Method**: GET

**Description**: Health check endpoint for container orchestration.

#### Response (Healthy)

**HTTP 200 OK**

```json
{
  "status": "Healthy",
  "checks": {
    "database": "Healthy",
    "licensingKey": "Healthy"
  },
  "timestamp": "2025-10-28T10:30:00Z"
}
```

#### Response (Unhealthy)

**HTTP 503 Service Unavailable**

```json
{
  "status": "Unhealthy",
  "checks": {
    "database": "Healthy",
    "licensingKey": "Unhealthy"
  },
  "errors": [
    "Licensing private key not found or invalid"
  ],
  "timestamp": "2025-10-28T10:30:00Z"
}
```

## Error Responses

All error responses follow this format:

```json
{
  "error": "error_code",
  "error_description": "Human-readable error description"
}
```

Common error codes:

- `invalid_request` - Missing or invalid parameters
- `key_not_found` - Requested key does not exist
- `key_revoked` - Key has been revoked
- `license_not_found` - Requested license token does not exist
- `internal_error` - Server error

## Rate Limiting

Not implemented in MVP. Service assumes internal deployment with low traffic.

## CORS

Not configured (service not designed for browser-based API consumption).

## Content Types

- Form submissions: `application/x-www-form-urlencoded`
- JSON responses: `application/json`
- JWT downloads: `text/plain`
- JWK downloads: `application/json`

## Security Headers

- `X-Frame-Options: DENY` (prevent clickjacking)
- `X-Content-Type-Options: nosniff` (prevent MIME sniffing)
- `Content-Security-Policy` (restrict inline scripts)
- `Strict-Transport-Security` (enforce HTTPS in production)
