## 2024-05-18 - Fix N+1 queries by replacing .Contains in memory iterations
**Learning:** Found an opportunity to replace `IEnumerable.Contains()` in loops with `.ToHashSet()` which has O(1) complexity, specifically applied to `ConfigurationImportService.cs` and `TenantSeedingService.cs`. The codebase often performs `.Contains` on Lists across thousands of items when seeding/importing.
**Action:** Replace `List<string>.Contains` inside loops with `.ToHashSet().Contains()` for memory-intensive operations.
