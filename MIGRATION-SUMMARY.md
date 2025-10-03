# EF Core Migration Summary - MrWhoOidc

## Overview
Complete EF Core migration script generated successfully (0-100%).

**Generated on:** October 2, 2025  
**Database:** PostgreSQL  
**EF Core Version:** 9.0.9  
**Total SQL Lines:** 852

## Files Generated

### 1. Complete SQL Script
- **File:** `migrations-full-script.sql`
- **Type:** Idempotent migration script
- **Purpose:** Can be run multiple times safely; creates complete database schema from scratch

### 2. Migration Files
Located in: `MrWhoOidc.Auth/Persistence/Migrations/`

#### Migration History:
1. **20251002160659_AddAutoApprovalModeToClient**
   - Adds `AutoApprovalMode` column to Clients table
   
2. **20251002183251_Initial**
   - Complete initial database schema (main migration)
   - Creates all core tables, indexes, and foreign keys
   
3. **20251002195948_QRLoginAndLatestChanges**
   - Empty migration (database was already up-to-date)

## Database Schema Overview

### Core Tables Created:

#### Authentication & Authorization
- **Users** - User accounts with password hashing, email verification, TOTP
- **Clients** - OAuth/OIDC client applications with extensive configuration
- **Realms** - Multi-tenant realm support
- **Roles** - Role definitions per realm
- **Scopes** - OAuth scope catalog

#### OIDC Protocol Support
- **AuthorizationCodes** - Authorization code flow with PKCE
- **Tokens** - Access/refresh tokens with DPoP binding, delegation tracking
- **Consents** - User consent records
- **SigningKeys** - JWT signing key rotation
- **PushedAuthorizationRequests** - PAR endpoint support
- **RevocationAudits** - Token revocation history

#### User Management
- **UserAlternativeEmails** - Multiple emails per user
- **UserClientAssignments** - User-to-client mappings
- **UserRoleAssignments** - Legacy role assignments (per client + realm)
- **UserRealmRoleAssignments** - Realm-level role assignments
- **UserClientRoleAssignments** - Client-specific role assignments
- **Registrations** - Pending user registrations with approval workflow

#### Identity Provider Chaining
- **IdentityProviders** - External IdP configurations (OIDC, SAML)
- **ClientIdentityProviders** - Per-client IdP associations
- **IdentityProviderClaimMappings** - Claim transformation rules
- **IdentityProviderKeys** - IdP signing/encryption keys
- **ExternalIdentities** - External issuer+subject linkage

#### Advanced Features
- **BackchannelLogoutNotifications** - BCL outbox with retry logic
- **LogoutRedirectReferences** - Opaque post_logout_redirect_uri
- **QrLoginSessions** - QR code login flow sessions
- **ClientJwksHistory** - JWKS diagnostic history
- **ClientScopes** - Client-to-scope mappings

#### Framework
- **DataProtectionKeys** - ASP.NET Data Protection key storage

### Key Features in Schema

#### Client Configuration
- Multi-method client authentication (secret_basic, secret_post, private_key_jwt)
- M2M policy controls (audiences, lifetimes, mTLS)
- Introspection shaping and mTLS thumbprints
- Per-client PAR requirements
- Front-channel and back-channel logout URIs
- Login method toggles (local, external IdP, QR)
- External user provisioning policies
- OBO (On-Behalf-Of) policy with delegation depth tracking
- DPoP modes for OBO flows
- Auto-approval modes for consents

#### Security Features
- Password hashing with algorithm tracking (Argon2id/BCrypt)
- Email normalization with unique constraints
- Token binding with DPoP JKT confirmation
- Delegation depth tracking for token exchange
- Actor claims tracking (`act` chain)
- TOTP/2FA support

#### Multi-tenancy
- Realm-based isolation
- Realm-specific roles and assignments
- Per-realm client configurations

#### Audit & Observability
- Comprehensive timestamps (CreatedAt, UpdatedAt, ExpiresAt)
- Revocation tracking with audit trails
- Backchannel logout attempt tracking with retry metadata
- JWKS history for diagnostics

## How to Apply Migrations

### Option 1: Apply via EF Core Tools
```powershell
# Update database to latest migration
dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth
```

### Option 2: Apply SQL Script Directly
```powershell
# Execute against PostgreSQL database
psql -h localhost -U <username> -d <database> -f migrations-full-script.sql
```

### Option 3: Via Aspire/Runtime
The application automatically applies migrations on startup via:
- `MrWhoOidc.Auth/DependencyInjection.cs` - Database initialization
- Connection string from Aspire: `authdb`

## Creating New Migrations

When you modify entity models in `AuthDbContext.cs`:

```powershell
# Create new migration
dotnet ef migrations add <MigrationName> --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations

# Review generated migration files
# Then update database
dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth
```

## Migration Script Details

### Idempotent Design
The generated script uses PostgreSQL's conditional logic:
```sql
DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '<migration-id>') THEN
        -- Migration SQL here
    END IF;
END $EF$;
```

This ensures:
- ✅ Safe to run multiple times
- ✅ Only applies missing migrations
- ✅ Tracks applied migrations in `__EFMigrationsHistory`

### Transaction Boundaries
- All migrations wrapped in a single transaction
- Atomic application: all-or-nothing
- Automatic rollback on errors

## Indexes & Performance

The schema includes strategic indexes for:
- Unique constraints (username, emails, client IDs)
- Foreign key relationships (automatic in PostgreSQL)
- Lookup optimization (realm IDs, client IDs, user IDs)
- Query performance (normalized emails, token hashes, JTI lookups)

## Next Steps

1. **Review the SQL script:** `migrations-full-script.sql`
2. **Test in development:** Apply to dev PostgreSQL instance
3. **Run tests:** Ensure schema matches application expectations
   ```powershell
   dotnet test
   ```
4. **Deploy to environments:** Use CI/CD pipeline with the idempotent script

## Additional Resources

- **Persistence Models:** `MrWhoOidc.Auth/Persistence/*.cs`
- **DB Context:** `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`
- **Seeding:** `MrWhoOidc.Auth/Seeding/DbSeeder.cs`
- **Architecture Docs:** `docs/developer-guide.md`

---

**Generated by:** GitHub Copilot  
**Command used:** 
```powershell
dotnet ef migrations script --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output "migrations-full-script.sql" --idempotent
```
