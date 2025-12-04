# Quickstart: Standalone Licensing Service

**Feature**: 007-licensing-service-standalone  
**Date**: 2025-12-04

## Overview

This guide covers setting up and running the Licensing Service locally for development.

## Prerequisites

- .NET 9.0 SDK
- An OIDC provider for authentication (can use MrWhoOidc.WebAuth or any OIDC-compliant IdP)
- Git
- (Optional) Docker for PostgreSQL

## Project Structure

```text
LicensingService/
├── src/
│   ├── LicensingService.Core/      # Domain logic
│   └── LicensingService.Web/       # HTTP layer
└── tests/
    ├── LicensingService.Core.Tests/
    └── LicensingService.Web.Tests/
```

## Quick Setup

### 1. Clone and Navigate

```bash
cd MrWhoOidc
git checkout 007-licensing-service-standalone
```

### 2. Generate Signing Key

The service needs an ECDSA P-256 private key for signing license tokens:

```bash
# Create secrets directory
mkdir -p LicensingService/secrets

# Generate key using OpenSSL
openssl ecparam -genkey -name prime256v1 -noout -out LicensingService/secrets/signing-key.pem

# Or use the existing KeyGen tool
dotnet run --project tools/KeyGenerator -- --output LicensingService/secrets/signing-key.pem
```

### 3. Configure OIDC Provider

Edit `LicensingService/src/LicensingService.Web/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "LicensingDb": "Data Source=licensing-dev.db"
  },
  "Licensing": {
    "SigningKeyPath": "../secrets/signing-key.pem"
  },
  "Authentication": {
    "Authority": "https://localhost:5001",
    "Audience": "licensing-service",
    "RequireHttpsMetadata": true
  }
}
```

For local development with MrWhoOidc.WebAuth:
- Register a client with `client_id: licensing-service`
- Set redirect URI: `https://localhost:5002/signin-oidc`
- Grant scopes: `openid`, `profile`

### 4. Apply Database Migrations

```bash
cd LicensingService

# Create initial migration (first time only)
dotnet ef migrations add InitialCreate \
  --project src/LicensingService.Core \
  --startup-project src/LicensingService.Web \
  --output-dir Persistence/Migrations

# Apply migrations
dotnet ef database update \
  --project src/LicensingService.Core \
  --startup-project src/LicensingService.Web
```

### 5. Run the Service

```bash
dotnet run --project src/LicensingService.Web
```

The service will be available at:
- **Admin UI**: https://localhost:5002
- **API**: https://localhost:5002/api/v1
- **JWKS**: https://localhost:5002/.well-known/jwks.json

## First Steps

### Register a Product

```bash
curl -X POST https://localhost:5002/api/v1/products \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{
    "identifier": "my-app",
    "displayName": "My Application"
  }'
```

### Add Product Options

```bash
curl -X POST https://localhost:5002/api/v1/products/{productId}/options \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{
    "optionKey": "max_users",
    "displayName": "Maximum Users",
    "dataType": "number",
    "defaultValue": "10"
  }'
```

### Create a Customer

```bash
curl -X POST https://localhost:5002/api/v1/customers \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{
    "identifier": "ACME-001",
    "displayName": "Acme Corporation",
    "contactEmail": "licensing@acme.com"
  }'
```

### Issue a License

```bash
curl -X POST https://localhost:5002/api/v1/licenses \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{
    "customerId": "<customer-uuid>",
    "productId": "<product-uuid>",
    "tier": "Professional",
    "validUntil": "2026-12-04T00:00:00Z",
    "options": {
      "max_users": 100,
      "region": "US"
    }
  }'
```

Response includes the signed JWT token:
```json
{
  "id": "...",
  "tokenId": "jti-value",
  "token": "eyJhbGciOiJFUzI1NiIsInR5cCI6IkxJQ0VOU0UiLCJraWQiOiIuLi4ifQ...",
  ...
}
```

### Validate a License

```bash
curl -X POST https://localhost:5002/api/v1/licenses/validate \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{
    "token": "eyJhbGciOiJFUzI1NiIsInR5cCI6IkxJQ0VOU0UiLCJraWQiOiIuLi4ifQ..."
  }'
```

### Renew a License

```bash
curl -X POST https://localhost:5002/api/v1/licenses/{licenseId}/renew \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{
    "extensionDays": 365
  }'
```

## Running Tests

```bash
cd LicensingService

# Run all tests
dotnet test

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test project
dotnet test tests/LicensingService.Core.Tests
```

## Configuration Reference

| Setting | Description | Default |
|---------|-------------|---------|
| `ConnectionStrings:LicensingDb` | Database connection string | SQLite (dev) |
| `Licensing:SigningKeyPath` | Path to ECDSA private key | Required |
| `Authentication:Authority` | OIDC provider URL | Required |
| `Authentication:Audience` | Expected audience claim | Required |

## Troubleshooting

### "Signing key not found"

Ensure the path in `Licensing:SigningKeyPath` points to a valid PEM file with an ECDSA P-256 key.

### "401 Unauthorized"

- Verify OIDC provider is running
- Check that the access token has not expired
- Ensure the `audience` claim matches configuration

### Database Migration Errors

```bash
# Reset database (development only)
rm licensing-dev.db
dotnet ef database update --project src/LicensingService.Core --startup-project src/LicensingService.Web
```

## Next Steps

1. Register your products and define their licensable options
2. Set up customers
3. Issue licenses via API or Admin UI
4. Integrate license validation into your products
5. Configure production PostgreSQL connection

## API Documentation

Full OpenAPI specification: `specs/007-licensing-service-standalone/contracts/openapi.yaml`

Import into Swagger UI or Postman for interactive exploration.
