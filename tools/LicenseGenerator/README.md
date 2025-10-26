# LicenseGenerator Utility

Command line helper for crafting signed MrWhoOidc license payloads. It produces an ES256 JWT using the authority private key so administrators can install or validate licenses via the Admin UI or API.

## Prerequisites

- .NET 9 SDK
- ECDSA P-256 private key in PEM format (matching the compiled public key in `MrWhoOidc.Auth/Licensing/EmbeddedLicensingKeys.cs`)

### Generate a local test key pair

The repo ignores the `secrets/` folder so you can keep the private key out of source control. From the repo root:

```powershell
mkdir -Force secrets\licensing
docker run --rm alpine/openssl ecparam -name prime256v1 -genkey -noout | Out-File secrets/licensing/licensing-private.pem -Encoding ascii
Get-Content secrets/licensing/licensing-private.pem | docker run --rm -i alpine/openssl ec -pubout | Out-File secrets/licensing/licensing-public.pem -Encoding ascii
```

Update `PrimaryPublicKeyPem` in `MrWhoOidc.Auth/Licensing/EmbeddedLicensingKeys.cs` with the newly generated public key so the application accepts licenses signed by the companion private key.

## Usage

```bash
dotnet run --project tools/LicenseGenerator -- \
  --tier <tier> \
  --private-key <path-to-private-key.pem> \
  [--organization <name>] \
  [--valid-from <ISO-8601>] \
  [--valid-until <ISO-8601>] \
  [--valid-days <positive-int>] \
  [--feature <feature-name>]... \
  [--limit <name=value>]... \
  [--issuer <issuer>] \
  [--key-id <kid>] \
  [--token-id <jti>] \
  [--output <file>]
```

Example:

```bash
dotnet run --project tools/LicenseGenerator -- \
  --tier enterprise --organization "Contoso" \
  --valid-days 365 --feature analytics --feature dpop \
  --limit tenants=50 --limit users=1000 \
  --private-key .\secrets\licensing-private.pem \
  --output .\contoso-license.jwt
```

The tool writes the token to stdout and optionally to `--output`. Limits accept `-1` for unlimited entries. Provide `--valid-days` for relative expiry or explicit `--valid-until` for fixed timestamps.
