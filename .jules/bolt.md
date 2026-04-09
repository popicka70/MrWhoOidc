## 2024-05-18 - Fix N+1 queries by replacing .Contains in memory iterations
**Learning:** Found an opportunity to replace `IEnumerable.Contains()` in loops with `.ToHashSet()` which has O(1) complexity, specifically applied to `ConfigurationImportService.cs` and `TenantSeedingService.cs`. The codebase often performs `.Contains` on Lists across thousands of items when seeding/importing.
**Action:** Replace `List<string>.Contains` inside loops with `.ToHashSet().Contains()` for memory-intensive operations.
## 2026-03-16 - Avoid LINQ .Except for memory optimization
**Learning:** Chaining LINQ methods like `.Where().ToList()` and `.Except().ToList()` causes multiple passes over collections and excessive allocations. However, when optimizing to a single `foreach` loop, do not attempt to pre-allocate `new List<T>(IEnumerable.Count())` as it invokes a full enumeration just to get the count, negating the benefit.
**Action:** When replacing LINQ with `foreach` for performance on an `IEnumerable`, do not pre-allocate using `.Count()`. Use simple `new List<T>()` and rely on standard dynamic resizing, or check if the source implements `ICollection`.

## 2025-06-15 - Optimize EF Core bulk deletes with ExecuteDeleteAsync
**Learning:** Found an opportunity to replace the Entity Framework Core pattern of `.ToListAsync()` followed by `.RemoveRange()` with `.ExecuteDeleteAsync()`. The older pattern requires loading all entities into memory before deleting them, causing unnecessary memory allocation and network round-trips.
**Action:** Always replace `.ToListAsync()` + `RemoveRange()` with `.ExecuteDeleteAsync()` for bulk deletion operations in EF Core to improve memory usage and reduce database latency.
## 2025-10-24 - Optimize client expiration logic with IQueryable
**Learning:** Found an opportunity to replace the memory-intensive pattern of fetching all entities and their relationships (like `Client` and `ClientSecrets`) using `.Include()` and `.ToListAsync()`, just to verify expiration states in a loop.
**Action:** Always project boolean or condition states natively to the database engine using `IQueryable.Select()` or `IQueryable.Any()` rather than evaluating conditions in application memory after a full object fetch.
## 2026-08-10 - Optimize EF Core bulk updates with ExecuteUpdateAsync
**Learning:** Found an opportunity to replace the Entity Framework Core pattern of `.ToListAsync()` followed by a `foreach` loop modifying properties and calling `.SaveChangesAsync()` with `.ExecuteUpdateAsync()`. The older pattern requires loading all entities into memory before modifying them, causing unnecessary memory allocation and network round-trips. Note that since the InMemory provider does not fully support `ExecuteUpdateAsync`, fallback to the older pattern using `if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")` is necessary for tests.
**Action:** Always replace `.ToListAsync()` + `foreach` property modification + `.SaveChangesAsync()` with `.ExecuteUpdateAsync()` for bulk update operations in EF Core to improve memory usage and reduce database latency.
