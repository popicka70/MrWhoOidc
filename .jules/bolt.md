## 2024-05-18 - Fix N+1 queries by replacing .Contains in memory iterations
**Learning:** Found an opportunity to replace `IEnumerable.Contains()` in loops with `.ToHashSet()` which has O(1) complexity, specifically applied to `ConfigurationImportService.cs` and `TenantSeedingService.cs`. The codebase often performs `.Contains` on Lists across thousands of items when seeding/importing.
**Action:** Replace `List<string>.Contains` inside loops with `.ToHashSet().Contains()` for memory-intensive operations.
## 2026-03-16 - Avoid LINQ .Except for memory optimization
**Learning:** Chaining LINQ methods like `.Where().ToList()` and `.Except().ToList()` causes multiple passes over collections and excessive allocations. However, when optimizing to a single `foreach` loop, do not attempt to pre-allocate `new List<T>(IEnumerable.Count())` as it invokes a full enumeration just to get the count, negating the benefit.
**Action:** When replacing LINQ with `foreach` for performance on an `IEnumerable`, do not pre-allocate using `.Count()`. Use simple `new List<T>()` and rely on standard dynamic resizing, or check if the source implements `ICollection`.
