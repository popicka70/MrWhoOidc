# MrWhoOidc Production Setup Guide

**Version**: 1.0  
**Last Updated**: 2025-12-26  
**Target Audience**: DevOps engineers, System administrators deploying to cloud platforms

## Table of Contents

1. [Overview](#overview)
2. [Environment Variables Reference](#environment-variables-reference)
3. [Production Bootstrap Process](#production-bootstrap-process)
4. [Cloud Platform Deployment](#cloud-platform-deployment)
5. [Role and Authorization Architecture](#role-and-authorization-architecture)
6. [Troubleshooting](#troubleshooting)

---

## Overview

MrWhoOidc is designed with a **secure-by-default** approach for production deployments:

- **No automatic seeding** in production - database starts empty
- **Explicit bootstrap** required via protected endpoint
- **Separation of concerns** between platform admins and tenant admins

### Development vs Production Behavior

| Feature | Development | Production |
|---------|-------------|------------|
| Auto-seed on startup | ✅ Yes | ❌ No |
| Bootstrap endpoint | Available | Protected by token |
| Default credentials | Created automatically | Must be specified |
| Multi-tenancy state | From config | From config |

For the seeded local stack, use [for-developers/quickstart-15-min.md](for-developers/quickstart-15-min.md). This guide is for empty-database, production-style environments.

---

## Environment Variables Reference

### Required Variables

| Variable | Description | Example |
|----------|-------------|---------|
| `ConnectionStrings__authdb` | PostgreSQL connection string | `Host=db.example.com;Database=authdb;Username=oidc;Password=secret` |
| `Oidc__Issuer` | OIDC issuer URL | `https://auth.example.com` |
| `Oidc__PublicBaseUrl` | Public base URL for the service | `https://auth.example.com` |

### Bootstrap Variables

| Variable | Description | Example |
|----------|-------------|---------|
| `Bootstrap__Token` | Secret token to authorize bootstrap (remove after use) | `super-secret-random-token-12345` |

> ⚠️ **Note**: Cloud platforms like Render don't allow `:` in variable names. Use double underscore `__` instead.

### Optional Features

| Variable | Description | Default |
|----------|-------------|---------|
| `Redis__Enabled` | Enable Redis caching | `false` |
| `Redis__ConnectionString` | Redis connection | `redis:6379` |
| `Seeder__AutoSeedEnabled` | Auto-seed database (development/testing only) | `false` |
| `ForwardedHeaders__UnsafeTrustAll` | Trust proxy headers | `false` |

> Multi-tenancy mode is controlled by `MultiTenancy` configuration, not by licensing state.

### Security Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `DataProtection__ApplicationName` | Unique app name for key isolation | `MrWhoOidc` |
| `DataProtection__CertificatePath` | Path to X.509 PFX to encrypt the key-ring at rest (required in production unless opt-in below) | *(empty)* |
| `DataProtection__CertificatePassword` | Password for the DataProtection PFX | *(empty)* |
| `DataProtection__AllowUnencryptedKeyRingInProduction` | Explicit opt-in to store the key-ring unencrypted in the DB | `false` |
| `Hsts__Enabled` | Enable HSTS headers | `true` |
| `Hsts__MaxAge` | HSTS max age in seconds | `31536000` |
| `Auth__TokenValidationClockSkewSeconds` | Clock skew for JWT lifetime validation | `60` |
| `KeyRotation__RsaKeySizeBits` | RSA size for newly generated signing and encryption keys | `3072` |

> ⚠️ **Production requirement**: The application **refuses to start** in a non-development/non-staging environment unless either `DataProtection__CertificatePath` is set to a valid PFX **or** `DataProtection__AllowUnencryptedKeyRingInProduction=true` is explicitly set. This prevents a single DB compromise from exposing both the wrapped signing keys and the means to unwrap them. See [Troubleshooting](#dataprotection-key-ring-error-on-startup) below.

When you increase `KeyRotation__RsaKeySizeBits` above the current active RSA signing key size, the next rotation check creates a replacement signing key immediately instead of waiting for the normal rotation interval.

### Email/SMTP Variables

| Variable | Description | Example |
|----------|-------------|---------|
| `Mail__Enabled` | Enable email sending | `true` |
| `Mail__SmtpHost` | SMTP server hostname | `smtp.sendgrid.net` |
| `Mail__SmtpPort` | SMTP port | `587` |
| `Mail__Username` | SMTP username | `apikey` |
| `Mail__Password` | SMTP password/API key | `SG.xxxxx` |
| `Mail__FromAddress` | Sender email address | `noreply@example.com` |
| `Mail__FromName` | Sender display name | `MrWho Auth` |

When configuring the ASP.NET application directly, use the `Mail__...` keys above.
When configuring Docker Compose via `.env`, use the shell variables `MAIL_SMTP_USERNAME` and `MAIL_SMTP_PASSWORD`, which the compose file maps to `Mail__Username` and `Mail__Password`.

---

## Production Bootstrap Process

When deploying to production with a fresh database, follow these steps:

### Step 1: Deploy Application

Deploy your application to the cloud platform with all required environment variables configured.

### Step 2: Configure Bootstrap Token

Add the bootstrap token environment variable:

```
Bootstrap__Token=your-secure-random-token-here
```

Generate a secure token:

```powershell
# PowerShell
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

```bash
# Bash/Linux
openssl rand -base64 32
```

### Step 3: Wait for Application Startup

Wait for the application to start. You'll see logs like:

```
No migrations were applied. The database is already up to date.
Default tenant 'default' not found. Signing key initialization skipped.
```

This is **expected** - the database schema exists but no data has been seeded.

### Step 4: Call Bootstrap Endpoint

**PowerShell:**

```powershell
$token = "your-secure-random-token-here"
$body = '{"tenantSlug":"default","tenantName":"Default Tenant","adminEmail":"admin@yourdomain.com","adminPassword":"YourSecurePassword123!","adminName":"Administrator"}'
Invoke-RestMethod -Uri "https://your-app.onrender.com/bootstrap" -Method Post -ContentType "application/json" -Headers @{"X-Bootstrap-Token"=$token} -Body $body
```

**cURL (single line):**

```bash
curl -X POST https://your-app.onrender.com/bootstrap -H "Content-Type: application/json" -H "X-Bootstrap-Token: your-secure-random-token-here" -d '{"tenantSlug":"default","tenantName":"Default Tenant","adminEmail":"admin@yourdomain.com","adminPassword":"YourSecurePassword123!","adminName":"Administrator"}'
```

### Step 5: Verify Bootstrap Success

A successful response looks like:

```json
{
  "tenantId": "01234567-89ab-cdef-0123-456789abcdef",
  "slug": "default",
  "issuer": "https://your-app.onrender.com/t/default"
}
```

### Step 6: Remove Bootstrap Token

**Important**: Remove the `Bootstrap__Token` environment variable from your cloud platform after successful bootstrap. This prevents unauthorized access to the bootstrap endpoint.

### Step 7: Verify Application

1. Access the application URL - you should see the login page
2. Log in with the admin credentials you specified
3. Access `https://your-app.onrender.com/admin/clients` to verify tenant admin access
4. Access the Platform Admin section to manage tenants

---

## Cloud Platform Deployment

### Render.com

#### Environment Variables Setup

In Render Dashboard → Your Service → Environment:

| Key | Value |
|-----|-------|
| `ConnectionStrings__authdb` | `Host=your-db.render.com;Database=authdb;...` |
| `Oidc__Issuer` | `https://your-app.onrender.com` |
| `Oidc__PublicBaseUrl` | `https://your-app.onrender.com` |
| `Bootstrap__Token` | `your-secure-token` (remove after bootstrap) |
| `ForwardedHeaders__UnsafeTrustAll` | `true` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `DataProtection__CertificatePath` | Path to a PFX accessible in the container (e.g., `/etc/secrets/dataprotection.pfx`) |
| `DataProtection__CertificatePassword` | Password for the DataProtection PFX |

> If you cannot mount a certificate file on your platform, set `DataProtection__AllowUnencryptedKeyRingInProduction=true` to unblock startup, but understand the security tradeoff (see above).

#### Render-Specific Notes

- Render terminates TLS at the edge, so your app receives HTTP
- Set `ForwardedHeaders__UnsafeTrustAll=true` to trust proxy headers
- Database connection strings use Render's internal DNS

### Azure App Service

```
ConnectionStrings__authdb=Host=your-db.postgres.database.azure.com;Database=authdb;Username=admin@your-db;Password=xxx;SSL Mode=Require
Oidc__Issuer=https://your-app.azurewebsites.net
Oidc__PublicBaseUrl=https://your-app.azurewebsites.net
Bootstrap__Token=your-secure-token
DataProtection__CertificatePath=/home/site/wwwroot/certs/dataprotection.pfx
DataProtection__CertificatePassword=your-cert-password
```

### AWS ECS / Fargate

Use AWS Secrets Manager for sensitive values and reference them in task definitions:

```json
{
  "secrets": [
    {
      "name": "ConnectionStrings__authdb",
      "valueFrom": "arn:aws:secretsmanager:region:account:secret:mrwhooidc/db-connection"
    },
    {
      "name": "Bootstrap__Token",
      "valueFrom": "arn:aws:secretsmanager:region:account:secret:mrwhooidc/bootstrap-token"
    },
    {
      "name": "DataProtection__CertificatePath",
      "value": "/etc/secrets/dataprotection.pfx"
    },
    {
      "name": "DataProtection__CertificatePassword",
      "valueFrom": "arn:aws:secretsmanager:region:account:secret:mrwhooidc/dataprotection-cert-password"
    }
  ]
}
```

---

## Role and Authorization Architecture

MrWhoOidc uses a **realm-scoped role assignment** model for administrative access.

### Role Types

| Role | Realm | Purpose |
|------|-------|---------|
| `admin` | `admin` | Legacy admin role (backward compatibility) |
| `platform-admin` | `platform` | Platform-level administration (manage tenants, licenses) |
| `tenant-admin` | `default` | Tenant-level administration (manage users, clients, settings) |

### Role Assignment Tables

| Table | Purpose |
|-------|---------|
| `UserRealmRoleAssignments` | Realm-scoped roles (admin access) |
| `UserClientRoleAssignments` | Client-scoped roles (application permissions, `roles` claim) |

### Access Control

- **Platform Admin** (`/platform-admin/*`): Requires `platform-admin` role in `platform` realm
- **Tenant Admin** (`/admin/*`): Requires `tenant-admin` role in `default` realm (or current tenant's realm)
- **Platform admins do NOT automatically have tenant admin access** - use impersonation for cross-tenant operations

### Initial Admin User

The bootstrap process creates an admin user with:
- `admin` role in `admin` realm
- `tenant-admin` role in `default` realm
- `platform-admin` role in `platform` realm

This user has full access to both Platform Admin and Tenant Admin interfaces.

---

## Troubleshooting

### "Default tenant not found" Errors

**Symptom:**
```
Default tenant 'default' not found. Signing key initialization skipped.
Default tenant resolution failed for path: /. Slug: default
```

**Cause:** Fresh database without seed data.

**Solution:** Follow the [Production Bootstrap Process](#production-bootstrap-process).

### "Key not found in the key ring" Session Errors

**Symptom:**
```
System.Security.Cryptography.CryptographicException: The key {guid} was not found in the key ring.
```

**Cause:** Old session cookies from previous deployment (data protection keys changed).

**Solution:** This is expected after database reset. Users need to clear cookies or wait for them to expire. No action required - new sessions will work correctly.

### Bootstrap Returns 404

**Symptom:** POST to `/bootstrap` returns 404 Not Found.

**Cause:** `Bootstrap__Token` not configured.

**Solution:** Set the `Bootstrap__Token` environment variable and restart the application.

### Bootstrap Returns 401 Unauthorized

**Symptom:** POST to `/bootstrap` returns 401.

**Cause:** Token mismatch between header and environment variable.

**Solution:** Verify `X-Bootstrap-Token` header matches `Bootstrap__Token` environment variable exactly.

### Bootstrap Returns 409 Conflict

**Symptom:** POST to `/bootstrap` returns `{"error":"already_bootstrapped"}`.

**Cause:** Database already has tenant data.

**Solution:** Bootstrap only works on empty databases. If you need to re-bootstrap, drop and recreate the database.

### HTTPS Redirect Issues

**Symptom:** Redirect loops or "Failed to determine the https port for redirect" warnings.

**Cause:** Application behind a reverse proxy that terminates TLS.

**Solution:** 
1. Set `ForwardedHeaders__UnsafeTrustAll=true`
2. Ensure your proxy sends `X-Forwarded-Proto: https` header

### DataProtection Key-Ring Error on Startup

**Symptom:**
```
System.InvalidOperationException: DataProtection key-ring would be stored UNENCRYPTED at rest in production.
Set DataProtection:CertificatePath (and DataProtection:CertificatePassword) to encrypt the key-ring
with an X.509 certificate, or, if you explicitly accept the risk of storing the key-ring unencrypted
in the same database as the signing keys it protects, set
DataProtection:AllowUnencryptedKeyRingInProduction=true.
```

**Cause:** Running in `Production` (or any non-development/non-staging) environment without a DataProtection certificate configured.

**Solution (recommended):** Provide an X.509 certificate to encrypt the key-ring at rest:
1. Generate or obtain a PFX certificate:
   ```bash
   openssl req -x509 -newkey rsa:2048 -keyout /tmp/dp-key.pem -out /tmp/dp-cert.pem \
     -days 3650 -nodes -subj "/CN=MrWhoOidc-DataProtection"
   openssl pkcs12 -export -in /tmp/dp-cert.pem -inkey /tmp/dp-key.pem \
     -out certs/dataprotection.pfx -password pass:YourStrongPassword
   rm /tmp/dp-key.pem /tmp/dp-cert.pem
   ```
2. Mount the PFX into the container and set:
   - `DataProtection__CertificatePath` → path inside the container (e.g., `/https/dataprotection.pfx`)
   - `DataProtection__CertificatePassword` → the PFX password
3. Restart the application.

**Solution (quick unblock, less secure):** If you accept the risk (a single DB compromise exposes both the wrapped signing keys and the means to unwrap them):
```
DataProtection__AllowUnencryptedKeyRingInProduction=true
```

### Health Check Failures

**Symptom:** Platform reports unhealthy service.

**Cause:** Tenant resolution failing for health check path.

**Solution:** Health endpoints (`/health`, `/healthz`, `/ready`, `/live`) bypass tenant resolution. Check database connectivity and ensure migrations applied.

---

## Quick Reference

### Bootstrap Request Format

```json
{
  "tenantSlug": "default",
  "tenantName": "My Organization",
  "adminEmail": "admin@example.com",
  "adminPassword": "SecurePassword123!",
  "adminName": "Administrator"
}
```

### PowerShell Bootstrap Command

```powershell
$token = "your-token"; $body = '{"tenantSlug":"default","tenantName":"Default Tenant","adminEmail":"admin@example.com","adminPassword":"SecurePass123!","adminName":"Admin"}'; Invoke-RestMethod -Uri "https://your-app.example.com/bootstrap" -Method Post -ContentType "application/json" -Headers @{"X-Bootstrap-Token"=$token} -Body $body
```

### Verify Deployment

```powershell
# Check OIDC discovery
Invoke-RestMethod -Uri "https://your-app.example.com/t/default/.well-known/openid-configuration"

# Check health
Invoke-RestMethod -Uri "https://your-app.example.com/health"
```
