# Data Model

**Feature**: Docker Deployment Package  
**Date**: 2025-11-01  
**Status**: N/A

## Overview

This feature does not introduce new data entities or modify the existing data model. It is purely an infrastructure/DevOps feature focused on Docker packaging, CI/CD automation, and deployment configuration.

## Existing Data Model (No Changes)

The MrWhoOidc data model remains unchanged. All existing entities continue to function as designed:

- **OidcClient**: OAuth/OIDC client registrations
- **User**: User accounts and credentials
- **Session**: Authentication sessions
- **Consent**: User consent records
- **SigningKey**: Cryptographic keys for token signing
- **IdentityProvider**: External identity provider configurations
- **Tenant**: Multi-tenant isolation entities
- **BackchannelLogoutOutbox**: Durable outbox for logout notifications

For complete data model documentation, see:

- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`
- `MrWhoOidc.Auth/Persistence/Entities/`
- `docs/developer-guide.md`

## Docker-Specific Considerations

### Volume Data

Docker volumes persist database and cache data:

- **postgres-data**: Contains all PostgreSQL data files (PGDATA)
  - Stores all entity data listed above
  - Persists across container restarts
  - Requires backup strategy (see quickstart.md)

- **redis-data**: Contains Redis RDB snapshots (if enabled)
  - Stores cache data only (transient)
  - Loss of Redis data does not affect data integrity
  - Automatic rebuild from PostgreSQL on cache miss

### No Schema Changes Required

This feature does not require EF Core migrations or database schema changes. The existing schema supports Docker deployment without modification.
