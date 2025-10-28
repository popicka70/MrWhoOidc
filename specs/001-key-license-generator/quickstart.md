# Quickstart Guide: Key and License Management Service

**Date**: October 28, 2025  
**Feature**: Key and License Management Service  
**Branch**: 001-key-license-generator

## Overview

This guide provides quickstart instructions for developers implementing the Key and License Management Service. Follow these steps to set up your development environment, understand the architecture, and begin implementation.

## Prerequisites

- .NET 9 SDK installed
- Docker Desktop (for containerization)
- Visual Studio Code or Visual Studio 2022
- SQLite tools (optional, for database inspection)
- Git

## Project Setup

### 1. Create the Project

```bash
# Navigate to repository root
cd MrWhoOidc

# Create new ASP.NET Core Web App (Razor Pages)
dotnet new webapp -n MrWhoOidc.KeyGen -o MrWhoOidc.KeyGen -f net9.0

# Create test project
dotnet new mstest -n MrWhoOidc.KeyGen.Tests -o MrWhoOidc.KeyGen.Tests -f net9.0

# Add test reference to main project
cd MrWhoOidc.KeyGen.Tests
dotnet add reference ../MrWhoOidc.KeyGen/MrWhoOidc.KeyGen.csproj

# Add projects to solution
cd ..
dotnet sln MrWhoOidc.slnx add MrWhoOidc.KeyGen/MrWhoOidc.KeyGen.csproj
dotnet sln MrWhoOidc.slnx add MrWhoOidc.KeyGen.Tests/MrWhoOidc.KeyGen.Tests.csproj
```

### 2. Install Dependencies

```bash
cd MrWhoOidc.KeyGen

# EF Core SQLite
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
dotnet add package Microsoft.EntityFrameworkCore.Design

# JWT support
dotnet add package System.IdentityModel.Tokens.Jwt
dotnet add package Microsoft.IdentityModel.Tokens

# Cryptography (included in .NET 9, no package needed)
# System.Security.Cryptography

# Health checks
dotnet add package Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore

cd ../MrWhoOidc.KeyGen.Tests

# Test dependencies
dotnet add package Microsoft.AspNetCore.Mvc.Testing
dotnet add package Microsoft.EntityFrameworkCore.InMemory
```

### 3. Copy GuidHelper (UUIDv7)

```bash
# Copy UUIDv7 implementation from MrWhoOidc.Auth
cp MrWhoOidc.Auth/Persistence/GuidHelper.cs MrWhoOidc.KeyGen/Persistence/GuidHelper.cs
```

## Architecture Overview

### Layer Separation

```text
┌─────────────────────────────────────────┐
│         Razor Pages (Pages/)            │  ← HTTP/UI Layer
├─────────────────────────────────────────┤
│       Minimal APIs (Api/)               │  ← HTTP/API Layer
├─────────────────────────────────────────┤
│    Domain Services (Domain/Services/)   │  ← Business Logic
├─────────────────────────────────────────┤
│  Cryptography (Domain/Cryptography/)    │  ← Key Generation
├─────────────────────────────────────────┤
│       Persistence (Persistence/)        │  ← EF Core + SQLite
└─────────────────────────────────────────┘
```

### Key Classes

| Class | Responsibility | Layer |
|-------|----------------|-------|
| `KeyGenerationService` | Generate RSA/ECDSA key pairs | Domain |
| `LicenseGenerationService` | Generate license JWTs | Domain |
| `RsaKeyGenerator` | RSA key generation + JWK export | Domain |
| `EcdsaKeyGenerator` | ECDSA key generation + JWK export | Domain |
| `KeyGenDbContext` | EF Core DbContext | Persistence |
| `Generate.cshtml.cs` (KeyGeneration) | Key generation page model | UI |
| `Generate.cshtml.cs` (LicenseGeneration) | License generation page model | UI |
| `KeyDownloadEndpoints` | Minimal API for key downloads | API |

## Implementation Checklist

### Phase 1: Database & Models (P1)

- [ ] Create `KeyGenDbContext` with `KeyPairMetadata`, `KeyDownloadRecord`, `LicenseTokenMetadata` entities
- [ ] Configure SQLite connection string in `appsettings.json`
- [ ] Generate initial migration: `dotnet ef migrations add InitialCreate`
- [ ] Apply migration: `dotnet ef database update`
- [ ] Test: Verify SQLite database created at configured path

### Phase 2: Cryptography (P1)

- [ ] Implement `RsaKeyGenerator.Generate(keySize)` → returns RSA key pair
- [ ] Implement `EcdsaKeyGenerator.Generate(curve)` → returns ECDSA key pair
- [ ] Implement `JwkSerializer.SerializePrivateKey(key, kid)` → JWK JSON
- [ ] Implement `JwkSerializer.SerializePublicKey(key, kid)` → JWKS JSON
- [ ] Test: Generate RSA 2048, verify JWK structure
- [ ] Test: Generate ECDSA P-256, verify JWK structure
- [ ] Test: Verify exported public key validates signature from private key

### Phase 3: Key Generation Service (P1)

- [ ] Implement `KeyGenerationService.GenerateKeyPairAsync(algorithm, keySize, curve)`
- [ ] Generate unique `kid` using `GuidHelper.NewId()`
- [ ] Create `KeyPairMetadata` entity and save to database
- [ ] Return private key (JWK), public key (JWKS), and metadata
- [ ] Test: Generate key pair, verify database record created
- [ ] Test: Verify private key disposal after generation
- [ ] Test: Concurrent key generation (unique kids)

### Phase 4: Key Generation UI (P1)

- [ ] Create `/KeyGeneration/Generate.cshtml` Razor Page
- [ ] Add form with algorithm, key type, size/curve dropdowns
- [ ] Implement `OnPostAsync()` to call `KeyGenerationService`
- [ ] Display generated `kid` and download links on success
- [ ] Add antiforgery token validation
- [ ] Test: Submit form, verify key generated
- [ ] Test: Invalid inputs display validation errors

### Phase 5: Key Download API (P1)

- [ ] Create `/api/keys/{kid}/private` endpoint
- [ ] Fetch key from database, generate private JWK on-the-fly
- [ ] Record download in `KeyDownloadRecord`
- [ ] Return private JWK with `Content-Disposition: attachment`
- [ ] Create `/api/keys/{kid}/public` endpoint (return stored JWKS)
- [ ] Test: Download private key, verify JWK format
- [ ] Test: Revoked key returns 403 Forbidden
- [ ] Test: Invalid kid returns 404 Not Found

### Phase 6: License Generation Service (P2)

- [ ] Implement `LicenseGenerationService.GenerateLicenseAsync(tier, organization, validFrom, validUntil, features, limits)`
- [ ] Load licensing private key from configuration (PEM file path)
- [ ] Build JWT payload with required claims
- [ ] Sign JWT with ECDSA P-256
- [ ] Create `LicenseTokenMetadata` entity and save to database
- [ ] Return signed JWT string
- [ ] Test: Generate license, verify JWT structure
- [ ] Test: Validate JWT signature with public key
- [ ] Test: Missing licensing key fails fast on startup

### Phase 7: License Generation UI (P2)

- [ ] Create `/LicenseGeneration/Generate.cshtml` Razor Page
- [ ] Add form with tier, organization, validity, features, limits fields
- [ ] Implement `OnPostAsync()` to call `LicenseGenerationService`
- [ ] Display generated JWT with copy-to-clipboard button
- [ ] Add download link for JWT file
- [ ] Test: Submit form, verify license generated
- [ ] Test: Invalid inputs display validation errors
- [ ] Test: Decode JWT, verify claims match input

### Phase 8: Key Management Dashboard (P3)

- [ ] Create `/KeyGeneration/List.cshtml` Razor Page
- [ ] Fetch all keys from database with filtering (status, algorithm)
- [ ] Display table with kid, algorithm, created date, status, download count
- [ ] Add "Revoke" button for active keys
- [ ] Add "Download Public Key" link
- [ ] Implement pagination (20 items per page)
- [ ] Test: List all keys, verify display
- [ ] Test: Filter by status (active/revoked)
- [ ] Test: Revoke key, verify status updated

### Phase 9: Docker Containerization

- [ ] Create `Dockerfile` with multi-stage build
- [ ] Configure volume mount for SQLite database (`/data`)
- [ ] Configure volume/secret mount for licensing key (`/secrets`)
- [ ] Add health check endpoint (`/health`)
- [ ] Build image: `docker build -t mrwhooidc-keygen:latest .`
- [ ] Test: Run container, verify service starts
- [ ] Test: Persist database across container restarts
- [ ] Test: Health check returns 200 OK

### Phase 10: Integration Tests

- [ ] Set up `TestServer` with in-memory database
- [ ] Test: E2E key generation flow (form → download)
- [ ] Test: E2E license generation flow (form → download)
- [ ] Test: Key revocation flow
- [ ] Test: Download after revocation fails
- [ ] Test: Concurrent key generation (no collisions)

## Configuration

### appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=/data/keygen.db"
  },
  "KeyGen": {
    "LicensingPrivateKeyPath": "/secrets/licensing-private.pem"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

### appsettings.Development.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=keygen-dev.db"
  },
  "KeyGen": {
    "LicensingPrivateKeyPath": "./secrets/licensing/licensing-private.pem"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Information"
    }
  }
}
```

## Running Locally

### Development

```bash
# Run database migrations
dotnet ef database update --project MrWhoOidc.KeyGen

# Run the application
dotnet run --project MrWhoOidc.KeyGen

# Open browser
http://localhost:5000
```

### Docker

```bash
# Build image
docker build -t mrwhooidc-keygen:latest -f MrWhoOidc.KeyGen/Dockerfile .

# Run container
docker run -d \
  -p 8080:8080 \
  -v keygen-data:/data \
  -v $(pwd)/secrets/licensing:/secrets:ro \
  --name keygen \
  mrwhooidc-keygen:latest

# View logs
docker logs -f keygen

# Stop container
docker stop keygen
```

## Testing

### Run All Tests

```bash
dotnet test MrWhoOidc.KeyGen.Tests
```

### Run Specific Test

```bash
dotnet test MrWhoOidc.KeyGen.Tests --filter "FullyQualifiedName~KeyGenerationServiceTests"
```

### Test Coverage

```bash
dotnet test MrWhoOidc.KeyGen.Tests --collect:"XPlat Code Coverage"
```

## Next Steps

1. Review [data-model.md](./data-model.md) for database schema details
2. Review [contracts/api-spec.md](./contracts/api-spec.md) for API contracts
3. Start implementation with Phase 1 (Database & Models)
4. Follow constitution build quality gates (zero warnings)
5. Write tests alongside implementation (not after)

## Common Pitfalls

- **Private key storage**: Never store private keys in database or logs
- **GuidHelper usage**: Always use `GuidHelper.NewId()`, not `Guid.NewGuid()`
- **Key disposal**: Dispose of RSA/ECDSA objects with `using` statements
- **Licensing key path**: Ensure path is configured correctly in appsettings
- **SQLite path**: Use absolute paths or ensure relative paths resolve correctly
- **Migrations**: Always use `dotnet ef migrations add`, never hand-write migrations

## Resources

- [ASP.NET Core Razor Pages Documentation](https://learn.microsoft.com/en-us/aspnet/core/razor-pages/)
- [EF Core SQLite Provider](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/)
- [System.Security.Cryptography Documentation](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography)
- [RFC 7517 - JSON Web Key (JWK)](https://datatracker.ietf.org/doc/html/rfc7517)
- [RFC 9562 - UUIDv7](https://datatracker.ietf.org/doc/html/rfc9562)
