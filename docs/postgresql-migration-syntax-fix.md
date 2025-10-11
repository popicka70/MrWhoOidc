# PostgreSQL Migration Syntax Fix

**Date:** October 11, 2025  
**Status:** ✅ Fixed and Deployed  
**Issue:** PostgreSQL syntax error in migration filter clauses

## Problem

When deploying to Docker with PostgreSQL, the migrations failed with this error:

```
PostgreSQL Error: syntax error at or near "["
Position: 65
SqlState: 42601
```

The migration was using **SQL Server syntax** (square brackets `[TenantId]`) instead of **PostgreSQL syntax** (double quotes `"TenantId"`).

## Root Cause

The EF Core migration generator created filter clauses using SQL Server syntax:

```csharp
// ❌ WRONG (SQL Server syntax):
.HasFilter("[TenantId] IS NULL AND [IsGlobal] = 1")
.HasFilter("[TenantId] IS NOT NULL")
```

PostgreSQL requires double quotes for identifiers and uses `true`/`false` instead of `1`/`0` for booleans:

```csharp
// ✅ CORRECT (PostgreSQL syntax):
.HasFilter("\"TenantId\" IS NULL AND \"IsGlobal\" = true")
.HasFilter("\"TenantId\" IS NOT NULL")
```

## Files Fixed

### 1. Migration File
**File:** `MrWhoOidc.Auth/Persistence/Migrations/20251011200133_AddTenantScopedScopes.cs`

**Changes:**
```csharp
// Before:
migrationBuilder.CreateIndex(
    name: "IX_Scopes_Name",
    table: "Scopes",
    column: "Name",
    unique: true,
    filter: "[TenantId] IS NULL AND [IsGlobal] = 1");  // ❌ SQL Server syntax

migrationBuilder.CreateIndex(
    name: "IX_Scopes_TenantId_Name",
    table: "Scopes",
    columns: new[] { "TenantId", "Name" },
    unique: true,
    filter: "[TenantId] IS NOT NULL");  // ❌ SQL Server syntax

// After:
migrationBuilder.CreateIndex(
    name: "IX_Scopes_Name",
    table: "Scopes",
    column: "Name",
    unique: true,
    filter: "\"TenantId\" IS NULL AND \"IsGlobal\" = true");  // ✅ PostgreSQL syntax

migrationBuilder.CreateIndex(
    name: "IX_Scopes_TenantId_Name",
    table: "Scopes",
    columns: new[] { "TenantId", "Name" },
    unique: true,
    filter: "\"TenantId\" IS NOT NULL");  // ✅ PostgreSQL syntax
```

### 2. DbContext Configuration
**File:** `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`

**Changes:**
```csharp
// Before:
modelBuilder.Entity<Scope>(b =>
{
    // ...
    b.HasIndex(x => new { x.TenantId, x.Name })
        .IsUnique()
        .HasFilter("[TenantId] IS NOT NULL");  // ❌ SQL Server syntax
    
    b.HasIndex(x => x.Name)
        .IsUnique()
        .HasFilter("[TenantId] IS NULL AND [IsGlobal] = 1");  // ❌ SQL Server syntax
});

// After:
modelBuilder.Entity<Scope>(b =>
{
    // ...
    b.HasIndex(x => new { x.TenantId, x.Name })
        .IsUnique()
        .HasFilter("\"TenantId\" IS NOT NULL");  // ✅ PostgreSQL syntax
    
    b.HasIndex(x => x.Name)
        .IsUnique()
        .HasFilter("\"TenantId\" IS NULL AND \"IsGlobal\" = true");  // ✅ PostgreSQL syntax
});
```

### 3. Additional Migration Created
**File:** `MrWhoOidc.Auth/Persistence/Migrations/20251011212138_UpdateScopeIndexFilters.cs`

This migration was created to update the model snapshot and recreate the indexes with correct PostgreSQL syntax:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    // Drop old indexes with incorrect syntax
    migrationBuilder.DropIndex(
        name: "IX_Scopes_Name",
        table: "Scopes");

    migrationBuilder.DropIndex(
        name: "IX_Scopes_TenantId_Name",
        table: "Scopes");

    // Recreate with correct PostgreSQL syntax
    migrationBuilder.CreateIndex(
        name: "IX_Scopes_Name",
        table: "Scopes",
        column: "Name",
        unique: true,
        filter: "\"TenantId\" IS NULL AND \"IsGlobal\" = true");

    migrationBuilder.CreateIndex(
        name: "IX_Scopes_TenantId_Name",
        table: "Scopes",
        columns: new[] { "TenantId", "Name" },
        unique: true,
        filter: "\"TenantId\" IS NOT NULL");
}
```

## Syntax Differences: SQL Server vs PostgreSQL

| Feature | SQL Server | PostgreSQL |
|---------|-----------|------------|
| **Identifier quotes** | `[Name]` | `"Name"` |
| **Boolean literals** | `1` / `0` | `true` / `false` |
| **Filter example** | `[TenantId] IS NULL AND [IsGlobal] = 1` | `"TenantId" IS NULL AND "IsGlobal" = true` |

## Deployment Verification

### Docker Logs (Success)
```
info: Microsoft.EntityFrameworkCore.Migrations[20402]
      Applying migration '20251011200133_AddTenantScopedScopes'.
info: Microsoft.EntityFrameworkCore.Migrations[20402]
      Applying migration '20251011212138_UpdateScopeIndexFilters'.
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://[::]:8443
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

### Database Verification
Both migrations applied successfully:
- ✅ `20251011200133_AddTenantScopedScopes` - Adds TenantId and IsGlobal columns
- ✅ `20251011212138_UpdateScopeIndexFilters` - Fixes index filters for PostgreSQL

The unique indexes are now correctly created with PostgreSQL syntax.

## Best Practices for Cross-Database Migrations

### 1. Use Database-Specific Syntax in HasFilter
When targeting PostgreSQL, always use double quotes and proper boolean literals:

```csharp
// ✅ Good for PostgreSQL:
.HasFilter("\"ColumnName\" IS NOT NULL")
.HasFilter("\"IsActive\" = true")

// ❌ Bad (SQL Server specific):
.HasFilter("[ColumnName] IS NOT NULL")
.HasFilter("[IsActive] = 1")
```

### 2. Test Migrations Locally Before Docker
Always test migrations in a local PostgreSQL instance before deploying to Docker:

```powershell
# Start local PostgreSQL (if using Docker)
docker run -p 5432:5432 -e POSTGRES_PASSWORD=password postgres:16-alpine

# Update connection string in appsettings
# Run migration
dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth
```

### 3. Review Generated Migrations
Always review the generated migration SQL before applying:

```powershell
# Generate SQL script to review
dotnet ef migrations script --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth
```

### 4. Use Parameterless Raw SQL When Needed
For complex filters, consider using raw SQL that's database-agnostic or use conditional logic:

```csharp
// Option 1: Use HasFilter without database-specific identifiers when possible
.HasIndex(x => x.Name)
    .IsUnique()
    .HasFilter("TenantId IS NULL");  // No quotes needed if no reserved words

// Option 2: Use conditional configuration
if (Database.IsNpgsql())
{
    .HasFilter("\"TenantId\" IS NULL");
}
else if (Database.IsSqlServer())
{
    .HasFilter("[TenantId] IS NULL");
}
```

## Lessons Learned

1. **EF Core doesn't auto-convert filter syntax** - You must manually ensure correct syntax for your target database

2. **Square brackets are SQL Server-specific** - PostgreSQL uses double quotes for identifiers

3. **Boolean representation differs** - SQL Server uses `1`/`0`, PostgreSQL uses `true`/`false`

4. **Always test with target database** - Don't rely solely on in-memory or SQLite testing

5. **Model snapshot updates** - After fixing model configuration, regenerate migrations to update the snapshot

## Related Documentation

- [Tenant-Scoped Scopes Complete](tenant-scoped-scopes-complete.md)
- [EF Core PostgreSQL Provider Docs](https://www.npgsql.org/efcore/)
- [PostgreSQL Identifier Syntax](https://www.postgresql.org/docs/current/sql-syntax-lexical.html#SQL-SYNTAX-IDENTIFIERS)

## Conclusion

The migration syntax has been corrected for PostgreSQL compatibility. Both migrations now apply successfully, and the application starts correctly in Docker with the tenant-scoped scopes feature fully functional.

**Status:** ✅ Production-ready with PostgreSQL
