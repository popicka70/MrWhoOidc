# Docker Database Reset Instructions

The Docker database needs to be recreated to apply the multi-tenancy migration that adds the `TenantId` column to tables including `PushedAuthorizationRequests`.

## Quick Reset (Recommended for Development)

Stop containers and remove volumes, then restart:

```powershell
# Stop and remove containers, networks, and volumes
docker compose down -v

# Rebuild and start fresh
docker compose up --build -d

# View logs to confirm successful migration
docker compose logs -f webauth
```

## Alternative: Manual Migration

If you want to keep existing data, you can manually apply the migration:

```powershell
# Connect to the running postgres container
docker compose exec postgres psql -U oidc -d authdb

# Or use connection string from your local machine:
dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --connection "Host=localhost;Port=5432;Database=authdb;Username=oidc;Password=oidcPass!;Include Error Detail=true"
```

## What Changed

The `AddMultiTenancySupport` migration adds:
- `Tenants` table for multi-tenant support
- `TenantId` column to all major tables (Clients, Users, PushedAuthorizationRequests, etc.)
- Foreign key constraints to `Tenants` table
- Indexes on `TenantId` columns
- Default tenant seeding

## Verification

After reset, verify the migration was applied:

```powershell
# Check logs for successful migration
docker compose logs webauth | Select-String "migration"

# Or connect to database and verify schema
docker compose exec postgres psql -U oidc -d authdb -c "\d PushedAuthorizationRequests"
```

You should see the `TenantId` column in the table schema.
