# pgAdmin Integration Guide

## Overview

pgAdmin has been added to the MrWhoOidc Aspire AppHost, providing a web-based PostgreSQL database management interface.

## Configuration

### AppHost Setup
```csharp
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent)
    .WithPgAdmin();  // ✅ Adds pgAdmin container
```

## Accessing pgAdmin

### 1. Start the AppHost
```powershell
dotnet run --project MrWhoOidc.AppHost
```

### 2. Open Aspire Dashboard
- The Aspire dashboard will open automatically (typically at `https://localhost:17003` or similar)
- Look for the **pgadmin** resource in the dashboard

### 3. Access pgAdmin Web Interface
- Click on the endpoint URL for pgAdmin in the Aspire dashboard
- Typically: `http://localhost:<random-port>`
- The Aspire dashboard will show the exact URL

### 4. Login to pgAdmin
**Default Credentials (managed by Aspire):**
- **Email:** `admin@admin.com`
- **Password:** `admin`

> ℹ️ These credentials are auto-configured by Aspire's `WithPgAdmin()` extension.

## Connecting to PostgreSQL Database

Once logged into pgAdmin:

### 1. Add Server Connection
1. Right-click **Servers** → **Register** → **Server**

### 2. General Tab
- **Name:** `MrWhoOidc (Local)`

### 3. Connection Tab
Use the connection details from your Aspire dashboard for the `postgres` resource:

- **Host:** `localhost` (or the container name if within Docker network)
- **Port:** Check Aspire dashboard for the mapped port (usually `5432` or random high port)
- **Maintenance database:** `postgres`
- **Username:** `postgres`
- **Password:** Check Aspire dashboard or secrets (usually a generated password)

> 💡 **Tip:** The Aspire dashboard shows the exact connection string. Look for the `postgres` resource endpoint.

### 4. Quick Connection String
From Aspire dashboard, find the PostgreSQL connection string which looks like:
```
Host=localhost;Port=<port>;Username=postgres;Password=<generated>;
```

## Accessing the `authdb` Database

After connecting to the PostgreSQL server in pgAdmin:

1. Expand **Servers** → **MrWhoOidc (Local)**
2. Expand **Databases**
3. Find **authdb** database
4. Explore:
   - **Schemas** → **public** → **Tables** to see all tables
   - Use **Query Tool** (right-click on `authdb`) to run SQL queries

## Common Tasks

### View All Tables
```sql
SELECT table_name 
FROM information_schema.tables 
WHERE table_schema = 'public' 
ORDER BY table_name;
```

### Check Migration History
```sql
SELECT * FROM "__EFMigrationsHistory" 
ORDER BY "MigrationId";
```

### View Users
```sql
SELECT "Id", "Username", "Email", "EmailVerified", "CreatedAt" 
FROM "Users";
```

### View Clients
```sql
SELECT "Id", "ClientId", "ClientName", "RealmId", "RequirePkce", "RequireConsent"
FROM "Clients";
```

### View Active Tokens
```sql
SELECT "Id", "Type", "UserId", "ClientId", "Audience", "CreatedAt", "ExpiresAt"
FROM "Tokens"
WHERE "RevokedAt" IS NULL 
  AND "ExpiresAt" > NOW()
ORDER BY "CreatedAt" DESC;
```

### View Backchannel Logout Queue
```sql
SELECT "Id", "ClientId", "BackchannelLogoutUri", "State", "AttemptCount", 
       "CreatedAt", "LastAttemptAt", "NextAttemptAt"
FROM "BackchannelLogoutNotifications"
WHERE "State" IN ('Pending', 'Retrying')
ORDER BY "NextAttemptAt";
```

## Benefits of pgAdmin

✅ **Visual Database Browser** - Explore tables, views, indexes, and relationships  
✅ **Query Tool** - Write and execute SQL queries with syntax highlighting  
✅ **Data Editor** - View and edit table data in a grid  
✅ **Schema Visualization** - ER diagrams and dependency graphs  
✅ **Performance Analysis** - Query execution plans and statistics  
✅ **Backup/Restore** - Database backup and restore operations  
✅ **User Management** - Manage PostgreSQL users and permissions  

## Development Workflow

### 1. Verify Migration Applied
After running migrations:
```sql
SELECT * FROM "__EFMigrationsHistory";
```

### 2. Inspect Seed Data
Check if test data was seeded:
```sql
SELECT COUNT(*) FROM "Users";
SELECT COUNT(*) FROM "Clients";
SELECT COUNT(*) FROM "Realms";
```

### 3. Debug OIDC Flows
During development, check authorization codes, tokens, and consents:
```sql
-- Recent auth codes
SELECT * FROM "AuthorizationCodes" 
WHERE "ExpiresAt" > NOW() 
ORDER BY "Id" DESC LIMIT 10;

-- Active tokens
SELECT "Type", COUNT(*) as count 
FROM "Tokens" 
WHERE "RevokedAt" IS NULL 
GROUP BY "Type";
```

### 4. Monitor Backchannel Logout
```sql
-- Failed BCL notifications
SELECT * FROM "BackchannelLogoutNotifications"
WHERE "State" = 'Failed'
ORDER BY "CreatedAt" DESC;

-- Retry queue
SELECT * FROM "BackchannelLogoutNotifications"
WHERE "State" = 'Retrying'
ORDER BY "NextAttemptAt";
```

## Troubleshooting

### Can't Connect to PostgreSQL
1. Check Aspire dashboard for actual port number
2. Verify PostgreSQL container is running
3. Check connection string in dashboard
4. Use `localhost` not `127.0.0.1` if on Windows

### Permission Denied
- Use the `postgres` superuser account
- Password is auto-generated by Aspire
- Find password in Aspire dashboard environment variables

### Database Not Showing
- Ensure AppHost is running
- Check that `authdb` was created (look in Aspire logs)
- EF migrations should auto-create the database

### pgAdmin Container Not Starting
```powershell
# Check logs in Aspire dashboard
# Or rebuild AppHost
dotnet clean
dotnet build
dotnet run --project MrWhoOidc.AppHost
```

## Security Notes

⚠️ **Development Only**  
The default pgAdmin credentials (`admin@admin.com` / `admin`) are for local development only.

⚠️ **Don't Expose Publicly**  
pgAdmin in Aspire is bound to localhost by default. Don't expose it to the internet.

✅ **Production**  
For production environments, use managed PostgreSQL services with their own admin tools (Azure Database, AWS RDS, etc.)

## Additional Resources

- **pgAdmin Documentation:** https://www.pgadmin.org/docs/
- **PostgreSQL Docs:** https://www.postgresql.org/docs/
- **Aspire PostgreSQL Hosting:** https://learn.microsoft.com/dotnet/aspire/database/postgresql-component

---

**Updated:** October 2, 2025  
**Aspire Version:** 9.5.0
