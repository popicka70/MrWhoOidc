# Key & License Management Service

Standalone web application for generating cryptographic key pairs and license tokens for OIDC clients using JAR (JWT-secured Authorization Requests) and JARM (JWT-secured Authorization Response Mode).

## Purpose

This application generates key pairs and license tokens on the client side, in a separate app, so the authorization server never sees client private keys. Administrators can:

- Generate RSA and ECDSA key pairs for OIDC clients
- Generate signed license tokens with custom claims
- Track key lifecycle and download history
- Audit cryptographic material usage

## Features

### Key Generation
- **Algorithms**: RSA (2048, 3072, 4096-bit) and ECDSA (P-256, P-384, P-521)
- **Formats**: JWK (JSON Web Key) for private keys, JWKS for public keys
- **Security**: Private keys never stored server-side - one-time download only
- **Tracking**: Download history with IP address and user agent logging

### License Token Generation
- **Tiers**: Free, Developer, Pro, Enterprise
- **Claims**: Organization, features, limits, validity period
- **Signing**: ECDSA P-256 (ES256) signed JWTs
- **Metadata**: Server-side tracking without storing actual tokens

### Administration
- Web UI with Razor Pages
- Key lifecycle management (view, download, revoke)
- License token generation with custom parameters
- Filtering and pagination for large datasets
- Health check endpoint for monitoring

## Quick Start

### Prerequisites

- .NET 10 SDK
- Docker (for containerized deployment)
- ECDSA P-256 private key for license signing

### Local Development

1. **Clone the repository**
   ```bash
   git clone https://github.com/your-org/MrWhoOidc.git
   cd MrWhoOidc
   ```

2. **Generate licensing key**
   ```bash
   # Using .NET KeyGenerator tool
   dotnet run --project tools/KeyGenerator/KeyGenerator.csproj
   
   # Or using OpenSSL
   openssl ecparam -genkey -name prime256v1 -noout -out secrets/licensing-private-key.pem
   ```

3. **Configure connection string**
   
   Edit `MrWhoOidc.KeyGen/appsettings.Development.json`:
   ```json
   {
     "ConnectionStrings": {
       "KeyGenDb": "Data Source=keygen-dev.db"
     },
     "KeyGen": {
       "LicensingPrivateKeyPath": "../secrets/licensing-private-key.pem"
     }
   }
   ```

4. **Apply migrations**
   ```bash
   dotnet ef database update --project MrWhoOidc.KeyGen
   ```

5. **Run the application**
   ```bash
   dotnet run --project MrWhoOidc.KeyGen
   ```

6. **Access the UI**
   
   Navigate to `https://localhost:5001` (or the port shown in console output)

### Docker Deployment

See [DOCKER.md](./DOCKER.md) for Docker deployment instructions.

**Quick Docker start:**

```bash
# Build image
docker build -t mrwhooidc-keygen:latest -f MrWhoOidc.KeyGen/Dockerfile .

# Run container
docker run -d \
  --name keygen \
  -p 8080:8080 \
  -v keygen-data:/data \
  -v ./secrets:/secrets:ro \
  -e ASPNETCORE_ENVIRONMENT=Production \
  mrwhooidc-keygen:latest

# Verify health
curl http://localhost:8080/health
```

## Usage

### Generating RSA Key Pairs

1. Navigate to **Key Generation** → **Generate New Key**
2. Select **Algorithm**: RSA
3. Choose **Key Size**: 2048, 3072, or 4096 bits
4. Select **Algorithm**: RS256, RS384, RS512, or PS256
5. Optionally provide a **Description** for tracking
6. Click **Generate Key Pair**
7. **Download Private Key** (JWK format) - save securely!
8. **Download Public Key** (JWKS format) - register with OIDC server

**Security Warning**: Private keys are shown only once. Save them right away.

### Generating ECDSA Key Pairs

1. Navigate to **Key Generation** → **Generate New Key**
2. Select **Algorithm**: ECDSA
3. Choose **Curve**: P-256, P-384, or P-521
4. Select **Algorithm**: ES256, ES384, or ES512
5. Provide optional **Description**
6. Click **Generate Key Pair**
7. Download keys as above

### Generating License Tokens

1. Navigate to **License Generation** → **Generate License**
2. Select **Tier**: Free, Developer, Pro, or Enterprise
3. Enter **Organization Name** (2-200 characters)
4. Set **Valid From** and **Valid Until** dates
5. Add **Features** (comma-separated): e.g., `analytics,dpop,multi-tenant`
6. Define **Limits** (JSON object):
   ```json
   {
     "tenants": 50,
     "users": 1000,
     "api_calls_per_day": 100000
   }
   ```
7. Click **Generate License**
8. **Download License Token** (JWT format)

### Managing Keys

- **View All Keys**: Navigate to **Key Generation** → **List**
- **Filter**: By status (Active/Revoked) or algorithm
- **View Details**: Click on a key ID to see full metadata and download history
- **Revoke Key**: Use the revoke button (requires confirmation)
- **Download Public Key**: Available even after initial generation

### Viewing License History

- Navigate to **License Generation** → **List**
- Filter by tier, organization, or expiry status
- View token ID, organization, features, validity period, and generation metadata

## Configuration

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_ENVIRONMENT` | Production | Environment (Development/Staging/Production) |
| `ASPNETCORE_URLS` | `http://+:8080` | Listening URLs |
| `ConnectionStrings__KeyGenDb` | `Data Source=/data/keygen.db` | SQLite connection string |
| `KeyGen__LicensingPrivateKeyPath` | `/secrets/licensing-private-key.pem` | Path to ECDSA P-256 private key |

### appsettings.json

```json
{
  "ConnectionStrings": {
    "KeyGenDb": "Data Source=/data/keygen.db"
  },
  "KeyGen": {
    "LicensingPrivateKeyPath": "/secrets/licensing-private-key.pem"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "MrWhoOidc.KeyGen": "Information"
    }
  }
}
```

### Database

- **Engine**: SQLite with WAL (Write-Ahead Logging) mode
- **Location**: `/data/keygen.db` (configurable)
- **Migrations**: Applied automatically on startup
- **Tables**: 
  - `KeyPairMetadata`: Key pair records with kid, algorithm, status
  - `KeyDownloadRecords`: Audit trail of all key downloads
  - `LicenseTokenMetadata`: License token metadata (not the tokens themselves)

## Security Considerations

### Private Key Security

- **Never stored server-side**: Private keys exist only in memory during generation
- **One-time download**: Download link works once, then returns 410 Gone
- **Client-side only**: Downloaded as a JavaScript Blob, never sent to the server again
- **Audit trail**: All download events logged with IP, timestamp, user agent

### License Token Security

- **Signed JWTs**: All tokens signed with ECDSA P-256 (ES256)
- **Secure key storage**: Licensing private key stored outside web root
- **No token storage**: Server stores metadata only, not actual JWTs
- **Expiry enforced**: Tokens include `nbf`, `iat`, and `exp` claims

### Application Security

- **Security headers**: X-Frame-Options, CSP, X-Content-Type-Options, HSTS
- **CSRF protection**: Antiforgery tokens on all forms
- **Correlation IDs**: Request tracing for debugging and security audits
- **Structured logging**: Sensitive data never logged (keys, tokens, secrets)
- **Non-root container**: Docker runs as user `app` (UID 1654)
- **Health checks**: `/health` endpoint for monitoring and orchestration
- **HTTPS**: Use HTTPS in production
- **Key rotation**: Rotate keys regularly
- **Dependencies**: Keep dependencies updated
- **Reverse proxy**: Run behind a reverse proxy (nginx, Traefik)
- **Secret management**: Use Docker secrets or vault for sensitive configuration

### Sensitive Data Handling

**Never logged:**
- Private keys (JWK format)
- License tokens (JWT strings)
- Client secrets
- Licensing private key

**Logged with redaction:**
- Key IDs (`kid`) - safe to log
- Algorithms - safe to log
- IP addresses - hashed in production logs
- User agents - truncated

## Monitoring & Health Checks

### Health Endpoint

```bash
GET /health
```

**Response:**
- `200 OK` - Application healthy, database accessible
- `503 Service Unavailable` - Application unhealthy

**Docker health check:**
```dockerfile
HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1
```

### Logging

Structured JSON logging with correlation IDs:

```json
{
  "timestamp": "2025-10-28T14:30:00.123Z",
  "level": "Information",
  "category": "MrWhoOidc.KeyGen.Domain.Services.KeyGenerationService",
  "message": "Generated key pair",
  "correlationId": "550e8400-e29b-41d4-a716-446655440000",
  "kid": "rsa-2048-20251028",
  "algorithm": "RS256",
  "keySize": 2048
}
```

Metrics are not implemented yet; planned: key generation count by algorithm, license generation count by tier, download counts per key, error rates and types, request latency percentiles.

## Development

### Project Structure

```
MrWhoOidc.KeyGen/
├── Api/                      # HTTP API endpoints
│   ├── KeyDownloadEndpoints.cs
│   └── LicenseDownloadEndpoints.cs
├── Configuration/            # Options and settings
│   └── KeyGenOptions.cs
├── Cryptography/            # Key generation logic
│   ├── EcdsaKeyGenerator.cs
│   ├── JwkSerializer.cs
│   └── RsaKeyGenerator.cs
├── Domain/                  # Business logic
│   ├── Models/              # Entities
│   │   ├── KeyPairMetadata.cs
│   │   ├── KeyDownloadRecord.cs
│   │   └── LicenseTokenMetadata.cs
│   └── Services/            # Domain services
│       ├── IKeyGenerationService.cs
│       ├── KeyGenerationService.cs
│       ├── ILicenseGenerationService.cs
│       └── LicenseGenerationService.cs
├── Middleware/              # Custom middleware
│   └── CorrelationIdMiddleware.cs
├── Pages/                   # Razor Pages UI
│   ├── KeyGeneration/
│   │   ├── Generate.cshtml(.cs)
│   │   ├── List.cshtml(.cs)
│   │   └── Details.cshtml(.cs)
│   ├── LicenseGeneration/
│   │   ├── Generate.cshtml(.cs)
│   │   └── List.cshtml(.cs)
│   ├── Shared/
│   │   └── _Layout.cshtml
│   ├── Index.cshtml(.cs)
│   ├── Privacy.cshtml(.cs)
│   └── Error.cshtml(.cs)
├── Persistence/             # Data access
│   ├── AuthDbContext.cs
│   └── Migrations/
├── wwwroot/                 # Static files
│   ├── css/
│   ├── js/
│   └── lib/
├── appsettings.json
├── appsettings.Development.json
├── Program.cs               # Application entry point
└── Dockerfile               # Docker build definition
```

### Adding a New Feature

1. **Domain Model**: Add entity to `Domain/Models/`
2. **Migration**: `dotnet ef migrations add <Name> --project MrWhoOidc.KeyGen`
3. **Service**: Add interface and implementation in `Domain/Services/`
4. **UI**: Create Razor Page in `Pages/`
5. **API**: Add endpoint in `Api/` if needed
6. **Tests**: Add unit tests (future)

### Running Migrations

```bash
# Add migration
dotnet ef migrations add <MigrationName> \
  --project MrWhoOidc.KeyGen \
  --output-dir Persistence/Migrations

# Apply migrations (local)
dotnet ef database update --project MrWhoOidc.KeyGen

# Generate SQL script
dotnet ef migrations script \
  --project MrWhoOidc.KeyGen \
  --output migration.sql
```

### Testing

```bash
# Build
dotnet build MrWhoOidc.KeyGen --configuration Release

# Run (development)
dotnet run --project MrWhoOidc.KeyGen --launch-profile https

# Run (production-like)
ASPNETCORE_ENVIRONMENT=Production dotnet run --project MrWhoOidc.KeyGen
```

## Docker

See [DOCKER.md](./DOCKER.md) for Docker deployment instructions including:
- Multi-stage build process
- Volume management
- Security configuration
- Production deployment
- Troubleshooting

## Related Documentation

- [DOCKER.md](./DOCKER.md) - Docker deployment guide
- [../docs/key-license-generator-deployment.md](../docs/key-license-generator-deployment.md) - Full deployment documentation
- [../specs/001-key-license-generator/spec.md](../specs/001-key-license-generator/spec.md) - Feature specification
- [../specs/001-key-license-generator/tasks.md](../specs/001-key-license-generator/tasks.md) - Implementation task breakdown

## Contributing

This project follows the MrWhoOidc architecture conventions:

- Keep domain logic in `Domain/`
- UI in `Pages/` (Razor Pages pattern)
- API endpoints in `Api/`
- Middleware in `Middleware/`
- Never log sensitive data (keys, tokens, secrets)
- Use structured logging with correlation IDs
- Add health checks for new external dependencies
- Apply migrations automatically on startup
- Use SQLite for development, PostgreSQL for production (future)

## License

Apache 2.0 — see the repository [LICENSE](../LICENSE)

## Support

For issues or questions:
- Check [DOCKER.md](./DOCKER.md) for deployment issues
- Review logs: `docker logs keygen` or local `dotnet run` output
- Health check: `curl http://localhost:8080/health`
- Open an issue on GitHub (if applicable)
