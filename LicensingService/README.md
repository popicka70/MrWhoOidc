# Licensing Service

A standalone licensing service for issuing, validating, and managing software licenses. Built with .NET 9, this service provides a complete license lifecycle management solution with JWT-based license tokens.

## Features

- **License Issuance**: Generate cryptographically signed JWT license tokens
- **License Validation**: Verify license authenticity, expiry, and revocation status
- **License Lifecycle**: Support for renewal, revocation, upgrade, and downgrade
- **Bulk Operations**: Process multiple licenses in batch operations
- **Customer Management**: Track customers and their associated licenses
- **Product Catalog**: Define products with configurable license options
- **JWKS Endpoint**: Expose public keys for offline license verification
- **Admin UI**: Razor Pages-based administrative interface
- **OpenAPI/Swagger**: Full API documentation with JWT authentication

## Quick Start

### Prerequisites

- .NET 9 SDK
- SQLite (development) or PostgreSQL (production)

### Running Locally

```bash
# Navigate to the service directory
cd LicensingService

# Restore dependencies
dotnet restore

# Run migrations (creates SQLite database)
cd src/LicensingService.Web
dotnet ef database update --project ../LicensingService.Core

# Run the service
dotnet run
```

The service will start at:
- **HTTPS**: https://localhost:5001
- **HTTP**: http://localhost:5000
- **Swagger UI**: https://localhost:5001/swagger
- **Admin UI**: https://localhost:5001/

### Running Tests

```bash
cd LicensingService
dotnet test --verbosity normal
```

## Configuration

### appsettings.json

```json
{
  "ConnectionStrings": {
    "LicensingDb": "Data Source=licensing.db"
  },
  "UsePostgres": false,
  "Oidc": {
    "Authority": "https://your-oidc-provider.com",
    "Audience": "licensing-service"
  },
  "SigningKey": {
    "KeysDirectory": "./keys",
    "KeyId": "your-key-id"
  }
}
```

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `ConnectionStrings__LicensingDb` | Database connection string | SQLite: `Data Source=licensing.db` |
| `UsePostgres` | Use PostgreSQL instead of SQLite | `false` |
| `Oidc__Authority` | OIDC provider URL | - |
| `Oidc__Audience` | Expected audience claim | - |
| `SigningKey__KeysDirectory` | Directory for signing keys | `./keys` |

### PostgreSQL Configuration

For production, set `UsePostgres=true` and configure the connection string:

```json
{
  "ConnectionStrings": {
    "LicensingDb": "Host=localhost;Database=licensing;Username=postgres;Password=secret"
  },
  "UsePostgres": true
}
```

## API Endpoints

### Products

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/products` | List all products |
| POST | `/api/products` | Create a product |
| GET | `/api/products/{id}` | Get product details |
| PUT | `/api/products/{id}` | Update a product |
| DELETE | `/api/products/{id}` | Delete a product |
| POST | `/api/products/{id}/options` | Add option definition |

### Customers

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/customers` | List all customers |
| POST | `/api/customers` | Create a customer |
| GET | `/api/customers/{id}` | Get customer details |
| PUT | `/api/customers/{id}` | Update a customer |
| DELETE | `/api/customers/{id}` | Delete a customer |
| GET | `/api/customers/{id}/licenses` | Get customer's licenses |

### Licenses

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/licenses` | List/search licenses |
| POST | `/api/licenses` | Issue a new license |
| GET | `/api/licenses/{id}` | Get license details |
| POST | `/api/licenses/{id}/renew` | Renew a license |
| POST | `/api/licenses/{id}/revoke` | Revoke a license |
| POST | `/api/licenses/{id}/upgrade` | Upgrade license tier |
| POST | `/api/licenses/{id}/downgrade` | Downgrade license tier |
| POST | `/api/licenses/bulk-renew` | Bulk renew licenses |
| POST | `/api/licenses/bulk-revoke` | Bulk revoke licenses |

### Validation

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/licenses/validate` | Validate a license token |

### Discovery

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/.well-known/jwks.json` | Public keys for offline validation |

### Health

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/health` | Full health check with database |
| GET | `/health/live` | Liveness probe |
| GET | `/health/ready` | Readiness probe |

## License Token Format

License tokens are JWTs signed with ES256 (ECDSA P-256). Claims include:

```json
{
  "jti": "license-id",
  "iss": "licensing-service",
  "sub": "customer-identifier",
  "aud": "product-identifier",
  "nbf": 1701676800,
  "exp": 1733212800,
  "tier": "Professional",
  "scope": ["feature1", "feature2"],
  "options": {
    "max_users": 100
  }
}
```

## Offline Validation

Applications can validate licenses offline by:

1. Fetching the JWKS from `/.well-known/jwks.json`
2. Caching the public keys locally
3. Verifying the JWT signature using the cached keys
4. Checking expiry (`exp` claim) and activation (`nbf` claim)

Note: Offline validation cannot detect revoked licenses. Use the `/api/licenses/validate` endpoint for full validation.

## Architecture

```
LicensingService/
├── src/
│   ├── LicensingService.Core/       # Domain logic, entities, services
│   │   ├── Crypto/                  # Signing key management
│   │   ├── Entities/                # EF Core entities
│   │   ├── Persistence/             # DbContext and migrations
│   │   ├── Services/                # Business logic services
│   │   └── Stores/                  # Data access layer
│   └── LicensingService.Web/        # HTTP surface, minimal APIs
│       ├── Api/                     # Minimal API endpoints
│       ├── Models/                  # Request/response DTOs
│       ├── Pages/                   # Razor Pages (Admin UI)
│       └── Services/                # Web-specific services
└── tests/
    └── LicensingService.Tests/      # Unit and integration tests
        ├── Integration/             # End-to-end tests
        ├── Services/                # Service unit tests
        └── Stores/                  # Store unit tests
```

## Security

- All API endpoints require JWT Bearer authentication (except JWKS and health checks)
- License tokens are signed with ECDSA P-256 (ES256)
- Signing keys support rotation with kid-based selection
- Revocation is immediately effective via database check

## Docker Deployment

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish "src/LicensingService.Web/LicensingService.Web.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "LicensingService.Web.dll"]
```

```yaml
# docker-compose.yml
services:
  licensing-service:
    build: .
    ports:
      - "5000:8080"
    environment:
      - ConnectionStrings__LicensingDb=Host=db;Database=licensing;Username=postgres;Password=secret
      - UsePostgres=true
      - Oidc__Authority=https://your-oidc-provider.com
      - Oidc__Audience=licensing-service
    depends_on:
      - db

  db:
    image: postgres:16
    environment:
      - POSTGRES_DB=licensing
      - POSTGRES_USER=postgres
      - POSTGRES_PASSWORD=secret
    volumes:
      - postgres_data:/var/lib/postgresql/data

volumes:
  postgres_data:
```

## Integration with MrWhoOidc

This service is designed to work with MrWhoOidc as the OIDC provider. Configure the `Oidc:Authority` to point to your MrWhoOidc instance:

```json
{
  "Oidc": {
    "Authority": "https://auth.example.com",
    "Audience": "licensing-api"
  }
}
```

## KeyGen Compatibility

This service uses the same ECDSA P-256 key format as `MrWhoOidc.KeyGen`. Keys generated by KeyGen can be used directly:

```bash
# Generate a key using KeyGen
dotnet run --project ../MrWhoOidc.KeyGen -- generate --output ./keys/signing-key.json

# Configure the service to use it
{
  "SigningKey": {
    "KeysDirectory": "./keys",
    "KeyId": "signing-key"
  }
}
```

## License

MIT License - see LICENSE file for details.
