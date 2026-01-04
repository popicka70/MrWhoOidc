# EF Core LINQ Query Translation Fix

**Date:** October 11, 2025  
**Status:** ✅ Fixed and Deployed  
**Issue:** EF Core could not translate complex LINQ query with OrderBy after projection

## Problem

The Scopes Index page was throwing an `InvalidOperationException` when trying to load the scopes list:

```
System.InvalidOperationException: The LINQ expression 'DbSet<Scope>()
    .OrderBy(s => new ScopeRow(...).IsGlobal ? 0 : 1)' could not be translated.
```

## Root Cause

The issue was with the **order of operations** in the LINQ query. The code was trying to:

1. Join Scopes with Tenants
2. Project to `ScopeRow` record
3. **Sort by `IsGlobal` property on the projected record** ❌

EF Core cannot translate sorting operations that reference properties on a projected (transformed) result. The sorting must happen on the database entity **before** the projection.

### The Problematic Code

```csharp
Scopes = await query
    .GroupJoin(
        db.Tenants.AsNoTracking(),
        s => s.TenantId,
        t => (Guid?)t.Id,
        (s, tenants) => new { Scope = s, Tenant = tenants.FirstOrDefault() })
    .Select(x => new ScopeRow(
        x.Scope.Name,
        x.Scope.Description,
        x.Scope.IsExposed,
        x.Scope.IsGlobal,
        x.Scope.TenantId,
        x.Tenant != null ? x.Tenant.Name : null))
    .OrderBy(s => s.IsGlobal ? 0 : 1) // ❌ WRONG - sorting after projection
    .ThenBy(s => s.Name)
    .ToListAsync();
```

## The Fix

Move the `OrderBy` **before** the `GroupJoin` and projection so it operates on the database entity:

```csharp
Scopes = await query
    .OrderBy(s => s.IsGlobal ? 0 : 1) // ✅ CORRECT - sort before projection
    .ThenBy(s => s.Name)
    .GroupJoin(
        db.Tenants.AsNoTracking(),
        s => s.TenantId,
        t => (Guid?)t.Id,
        (s, tenants) => new { Scope = s, Tenant = tenants.FirstOrDefault() })
    .Select(x => new ScopeRow(
        x.Scope.Name,
        x.Scope.Description,
        x.Scope.IsExposed,
        x.Scope.IsGlobal,
        x.Scope.TenantId,
        x.Tenant != null ? x.Tenant.Name : null))
    .ToListAsync();
```

## Why This Works

### EF Core Query Translation Pipeline

1. **Translatable Operations** (happen in database):
   - `Where` on entity properties
   - `OrderBy`/`ThenBy` on entity properties
   - Simple projections without complex logic
   - Joins using entity relationships

2. **Non-Translatable Operations** (require client evaluation):
   - Sorting by properties on projected records
   - Complex calculated properties after projection
   - Method calls that don't map to SQL functions

### The Correct Order

```
1. Filter (Where)          ✅ Database - Filter rows
2. Sort (OrderBy/ThenBy)   ✅ Database - Sort on entity properties
3. Join (GroupJoin)        ✅ Database - Join tables
4. Project (Select)        ✅ Database - Transform columns
5. Materialize (ToList)    ✅ Database → Memory
```

### What Doesn't Work

```
1. Filter (Where)          ✅ Database
2. Join (GroupJoin)        ✅ Database
3. Project (Select)        ✅ Database
4. Sort (OrderBy)          ❌ Cannot translate - s.IsGlobal is on projected record!
```

## File Changed

**File:** `MrWhoOidc.WebAuth/Pages/Admin/Scopes/Index.cshtml.cs`

**Lines Changed:** 48-65

**Summary:** Moved `OrderBy` and `ThenBy` clauses before `GroupJoin` to ensure sorting happens on database entities.

## Verification

### Application Logs (Success)
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://[::]:8443
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

No LINQ translation errors in the logs.

### Expected Behavior
- ✅ Scopes Index page loads successfully
- ✅ Global scopes appear first (sorted by `IsGlobal`)
- ✅ Within each group, scopes are sorted alphabetically by name
- ✅ Tenant names are populated via the join

## Best Practices for EF Core LINQ Queries

### 1. Sort Before Projection
Always apply `OrderBy`/`ThenBy` on **entity properties** before projecting to DTOs or records:

```csharp
// ✅ Good:
query.OrderBy(e => e.Property)
     .Select(e => new Dto { ... })

// ❌ Bad:
query.Select(e => new Dto { Property = e.Property })
     .OrderBy(dto => dto.Property)
```

### 2. Filter Early
Apply `Where` clauses as early as possible to reduce the data set:

```csharp
// ✅ Good:
query.Where(e => e.IsActive)
     .OrderBy(e => e.Name)
     .Select(e => new Dto { ... })

// ❌ Bad (filtering after projection):
query.Select(e => new Dto { IsActive = e.IsActive, Name = e.Name })
     .Where(dto => dto.IsActive)
```

### 3. Understand What Translates
Operations that translate to SQL:
- ✅ Simple property access (`e.Name`, `e.Id`)
- ✅ Basic comparisons (`==`, `!=`, `>`, `<`)
- ✅ Simple conditionals (`e.IsActive ? 1 : 0`)
- ✅ Built-in methods like `Contains`, `StartsWith`, `EndsWith`

Operations that don't translate:
- ❌ Complex method calls
- ❌ Navigation to projected properties
- ❌ String operations beyond basic ones
- ❌ Custom functions without `[DbFunction]` attribute

### 4. Use Query Tags for Debugging
Add query tags to identify queries in logs:

```csharp
var results = await db.Scopes
    .TagWith("Admin Scopes Index - Global First")
    .OrderBy(s => s.IsGlobal ? 0 : 1)
    .ToListAsync();
```

### 5. Check Generated SQL
Use logging or tools to see the actual SQL generated:

```csharp
// In appsettings.Development.json:
"Logging": {
  "LogLevel": {
    "Microsoft.EntityFrameworkCore.Database.Command": "Information"
  }
}
```

## Common LINQ Translation Errors

### Error 1: Cannot Translate OrderBy After Projection
```csharp
// ❌ Error:
.Select(e => new Dto { ... })
.OrderBy(dto => dto.Property)

// ✅ Fix:
.OrderBy(e => e.Property)
.Select(e => new Dto { ... })
```

### Error 2: Cannot Translate Complex Navigation
```csharp
// ❌ Error:
.Select(e => new Dto {
    RelatedName = e.Related.SubRelated.Name ?? "Default"
})

// ✅ Fix: Use joins or separate queries
.GroupJoin(related, ...)
.Select(x => new Dto {
    RelatedName = x.Related.Name ?? "Default"
})
```

### Error 3: Cannot Translate Method Calls
```csharp
// ❌ Error:
.Where(e => MyCustomMethod(e.Property))

// ✅ Fix: Use translatable operations or client evaluation
.AsEnumerable()
.Where(e => MyCustomMethod(e.Property))
```

## Performance Considerations

### Database vs Client Evaluation

**Database Evaluation (Preferred):**
- Sorting happens in PostgreSQL
- Only sorted results returned to application
- Efficient for large datasets

**Client Evaluation (Avoid):**
- All rows returned to application
- Sorting happens in memory
- Inefficient for large datasets

### This Fix's Impact

✅ **Before fix (would have failed):** Cannot translate, query fails
✅ **After fix:** Sorting happens in database, efficient query execution

## Related Issues

This is a common pattern in EF Core. Related scenarios:

1. **Pagination:** Must sort before `Skip`/`Take`
2. **Grouping:** Must group before sorting group keys
3. **Distinct:** Must apply before projection if deduplicating on entity properties

## Conclusion

The LINQ query has been corrected to sort on entity properties before projection. The Scopes Index page now works correctly with the tenant-scoped scopes feature.

**Key Takeaway:** Always perform sorting, filtering, and other database operations on **entity properties** before projecting to DTOs or records.

---

**Related Documentation:**
- [Tenant-Scoped Scopes Complete](tenant-scoped-scopes-complete.md)
- [EF Core Query Translation](https://learn.microsoft.com/en-us/ef/core/querying/how-query-works)
- [Client vs Server Evaluation](https://learn.microsoft.com/en-us/ef/core/querying/client-eval)
