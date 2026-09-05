# Multi-Tenancy Fix Instructions (for an implementing agent)

> **Historical instructions; do not apply the OLD/NEW patches verbatim.** They target an earlier source snapshot and are retained only to explain the original remediation intent. Current changes require a reproducing test and inspection of the owning code path. See [documentation status](documentation-status.md).

**Read this whole file before editing.** Apply fixes **in the given order**. After every
numbered fix, run the verification command and do not continue until it passes.

All paths are relative to the repo root `MrWhoOidc/` (the folder that contains
`MrWhoOidc.Auth/` and `MrWhoOidc.UnitTests/`).

Rules:
- Make **only** the changes described. Do not refactor unrelated code.
- Each fix has an **OLD** block (find it exactly) and a **NEW** block (replace it with).
- If an OLD block does not match what you see, STOP and report the mismatch instead of guessing.

---

## Group A — Dangerous regressions (fix first)

### A1. Revert the TenantIcon cascade (it currently deletes tenants)

The foreign key lives on `Tenant` (`HasForeignKey<Tenant>(x => x.TenantIconId)`), so
`TenantIcon` is the *principal* and `Tenant` is the *dependent*. `OnDelete(Cascade)` therefore
means **deleting an icon deletes the whole tenant** — the opposite of the intent.

**File:** `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`

**OLD**
```csharp
                .HasForeignKey<Tenant>(x => x.TenantIconId)
                .OnDelete(DeleteBehavior.Cascade); // L3: Cascade delete to prevent orphaned icons
```
**NEW**
```csharp
                .HasForeignKey<Tenant>(x => x.TenantIconId)
                .OnDelete(DeleteBehavior.SetNull);
```

Reason: tenants are soft-deleted (`Status = Deleted`, `DeletedAt` set), never hard-deleted, so
the icon is never orphaned in practice. `SetNull` is the safe behavior for this FK direction.

---

### A2. Fix the navigation-property write guard (it throws on valid saves)

`entry.Property(name)` only works for **scalar** properties. Calling it with a navigation name
throws `InvalidOperationException` at runtime for every entity that has a `Tenant` navigation
(e.g. `TenantIconEntity`, memberships, domain claims). Use the reference-navigation API instead.

**File:** `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (inside `ApplyTenantWriteGuards`)

**OLD**
```csharp
            // Also validate navigation properties that reference Tenant
            foreach (var navEntry in entry.Metadata.GetNavigations()
                .Where(n => n.ClrType == typeof(Tenant) ||
                           (n.ClrType.IsGenericType && n.ClrType.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                            n.ClrType.GetGenericArguments()[0] == typeof(Tenant))))
            {
                var navValue = entry.Property(navEntry.Name).CurrentValue;
                if (navValue is Tenant tenantEntity && tenantEntity.Id != Guid.Empty && tenantEntity.Id != currentTenantId.Value)
                {
                    throw new InvalidOperationException($"Refusing to save {entry.Metadata.ClrType.Name} with navigation to a different tenant.");
                }
            }
```
**NEW**
```csharp
            // Also validate reference navigations that point at a Tenant.
            foreach (var reference in entry.References)
            {
                if (reference.TargetEntry?.Entity is Tenant tenantEntity
                    && tenantEntity.Id != Guid.Empty
                    && tenantEntity.Id != currentTenantId.Value)
                {
                    throw new InvalidOperationException(
                        $"Refusing to save {entry.Metadata.ClrType.Name} with navigation to a different tenant.");
                }
            }
```

Note: `entry.References` (an `EntityEntry` API) enumerates only reference navigations and never
throws on scalar properties. Collection navigations are intentionally not checked here — the
child entity's own `TenantId` scalar guard already covers them.

---

### A3. Fix slug generation (lost its uniqueness check + produces invalid slugs)

Two problems in the current code:
1. `GenerateUniqueSlug()` has `do { return ...; } while (true)` — it returns on the first
   iteration, so the database uniqueness check that used to exist is **gone**, and the loop /
   `attempts > 10` guard is dead code.
2. `Base64UrlEncoder` produces mixed-case strings containing `_`, which **fail**
   `TenantSlug.IsValid` (that regex allows only lowercase `[a-z0-9-]`). Generated slugs are
   inconsistent with custom slugs and with the case-insensitive lookup in fix B1.

Use a lowercase hex generator and restore the DB check.

**File:** `MrWhoOidc.Auth/Services/TenantService.cs`

**OLD**
```csharp
    private string GenerateUniqueSlug()
    {
        int attempts = 0;
        do
        {
            attempts++;
            if (attempts > 10) throw new InvalidOperationException("Failed to generate unique tenant slug.");

            // Generate 8 bytes -> ~11 chars in Base64Url
            var bytes = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(bytes);
        }
        while (true); // Will be checked for uniqueness after the loop in the calling code
    }
```
**NEW**
```csharp
    private async Task<string> GenerateUniqueSlugAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            // 8 random bytes -> 16 lowercase hex chars; always passes TenantSlug.IsValid.
            var bytes = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var candidate = Convert.ToHexString(bytes).ToLowerInvariant();

            var exists = await _db.Tenants.AnyAsync(t => t.Slug == candidate, ct);
            if (!exists)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Failed to generate unique tenant slug.");
    }
```

Now update the **call site** in `CreateTenantAsync` (the overload that takes `string? slug`).

**OLD**
```csharp
        if (string.IsNullOrWhiteSpace(slug))
        {
            // Generate unique slug
            slug = GenerateUniqueSlug();
        }
```
**NEW**
```csharp
        if (string.IsNullOrWhiteSpace(slug))
        {
            // Generate unique slug
            slug = await GenerateUniqueSlugAsync(ct);
        }
```

---

### A4. Make tenant lookup truly case-insensitive across providers

`EF.Functions.Like(t.Slug, slug)` is **case-sensitive on PostgreSQL** (only `ILIKE` is
insensitive), so the current "fix" silently fails in production while the in-memory test passes.
Because slugs are always stored lowercase (fix A3 guarantees it for generated slugs; custom slugs
are validated lowercase), a plain equality on a lowercased input is correct, index-friendly, and
translates on every provider.

**File:** `MrWhoOidc.Auth/MultiTenancy/TenantResolver.cs` (method `ResolveTenantBySlugAsync`)

**OLD**
```csharp
        var cacheKey = $"{CacheKeyPrefix}{slug.ToLowerInvariant()}";

        if (_cache.TryGetValue<TenantContext>(cacheKey, out var cachedContext) && cachedContext != null)
        {
            return cachedContext;
        }

        // Use database-level case-insensitive comparison via EF Core's
        // StringComparer.OrdinalIgnoreCase translation (works for PostgreSQL, SQL Server, SQLite).
        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.Status == TenantStatus.Active
                    && EF.Functions.Like(t.Slug, slug),
                cancellationToken);
```
**NEW**
```csharp
        var normalizedSlug = slug.ToLowerInvariant();
        var cacheKey = $"{CacheKeyPrefix}{normalizedSlug}";

        if (_cache.TryGetValue<TenantContext>(cacheKey, out var cachedContext) && cachedContext != null)
        {
            return cachedContext;
        }

        // Slugs are always stored lowercase (validated on create), so an equality match on the
        // lowercased input is case-insensitive, uses the unique index on Slug, and translates
        // on every provider (PostgreSQL, SQL Server, SQLite, in-memory).
        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.Status == TenantStatus.Active && t.Slug == normalizedSlug,
                cancellationToken);
```

---

### A5. Align the duplicate-name / duplicate-slug checks with the DB unique indexes

The unique index `IX_Tenants_Name` (and the existing unique index on `Slug`) apply to **all**
rows regardless of status. The code checks only `Status == Active`, so a soft-deleted tenant that
still holds a name/slug will pass the code check and then throw a raw `DbUpdateException` at
`SaveChanges`. Remove the `Status == Active` filter so the code matches the constraint.

**File:** `MrWhoOidc.Auth/Services/TenantService.cs` (in `CreateTenantAsync(..., string? slug, ...)`)

**OLD**
```csharp
        // Check for duplicate tenant name
        var existingTenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Name == name && t.Status == TenantStatus.Active, ct);
        if (existingTenant != null)
        {
            throw new InvalidOperationException($"A tenant with the name '{name}' already exists.");
        }
```
**NEW**
```csharp
        // Check for duplicate tenant name (the unique index covers all rows, incl. soft-deleted).
        var nameTaken = await _db.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Name == name, ct);
        if (nameTaken)
        {
            throw new InvalidOperationException($"A tenant with the name '{name}' already exists.");
        }
```

**OLD**
```csharp
            // Check for duplicate slug
            var existingSlug = await _db.Tenants
                .AsNoTracking()
                .AnyAsync(t => t.Slug == slug && t.Status == TenantStatus.Active, ct);
            if (existingSlug)
```
**NEW**
```csharp
            // Check for duplicate slug (the unique index covers all rows, incl. soft-deleted).
            var existingSlug = await _db.Tenants
                .AsNoTracking()
                .AnyAsync(t => t.Slug == slug, ct);
            if (existingSlug)
```

**Verification for Group A:**
```bash
dotnet build MrWhoOidc.Auth/MrWhoOidc.Auth.csproj
```
Must compile with no errors.

---

## Group B — Wire up the "ghost" files (created but never used)

### B1. H3 — make public email domains configurable (currently still hardcoded)

`PublicEmailDomainOptions.cs` exists but `TenantDomainClaimService` still uses a hardcoded
`static` set. Inject the options and use them.

**File:** `MrWhoOidc.Auth/Services/TenantDomainClaimService.cs`

**OLD (constructor)**
```csharp
internal sealed partial class TenantDomainClaimService(
    AuthDbContext db,
    ILogger<TenantDomainClaimService> logger) : ITenantDomainClaimService
{
    private static readonly IdnMapping Idn = new();
    private static readonly HashSet<string> PublicEmailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "aol.com", "gmail.com", "googlemail.com", "gmx.com", "hotmail.com",
        "icloud.com", "live.com", "mac.com", "mail.com", "me.com", "msn.com",
        "outlook.com", "pm.me", "proton.me", "protonmail.com", "yahoo.com", "zoho.com"
    };
```
> The exact contents of the hardcoded set may be formatted differently. Delete the whole
> `private static readonly HashSet<string> PublicEmailDomains = ... ;` declaration, keep `Idn`.

**NEW (constructor)**
```csharp
internal sealed partial class TenantDomainClaimService(
    AuthDbContext db,
    ILogger<TenantDomainClaimService> logger,
    IOptions<PublicEmailDomainOptions> publicEmailDomainOptions) : ITenantDomainClaimService
{
    private static readonly IdnMapping Idn = new();
    private readonly HashSet<string> _publicEmailDomains =
        publicEmailDomainOptions.Value.Domains;
```

Then replace **both** usages of the old `PublicEmailDomains` field with `_publicEmailDomains`:

**OLD** `return NormalizeDomainForClaim(normalizedEmail[(atIndex + 1)..], PublicEmailDomains);`
**NEW** `return NormalizeDomainForClaim(normalizedEmail[(atIndex + 1)..], _publicEmailDomains);`

**OLD** `var normalizedDomain = NormalizeDomainForClaim(domain, PublicEmailDomains);`
**NEW** `var normalizedDomain = NormalizeDomainForClaim(domain, _publicEmailDomains);`

(`NormalizeDomainForClaim` already takes the set as a parameter — leave its body unchanged.)

**Register the options** in `MrWhoOidc.Auth/DependencyInjection.cs`. Find the line that
registers `TenantCacheOptions` and add directly after it (in the multi-tenant branch):
```csharp
            services.Configure<PublicEmailDomainOptions>(configuration.GetSection("PublicEmailDomains"));
```
And in the single-tenant `else` branch add a default registration so DI always resolves it:
```csharp
            services.Configure<PublicEmailDomainOptions>(_ => { });
```

**Fix the two test call sites** that construct the service directly (they now need the 3rd arg).
**File:** `MrWhoOidc.UnitTests/MultiTenancy/MultiTenancyFixesTests.cs`

**OLD (appears twice)**
```csharp
        var service = new TenantDomainClaimService(db, NullLogger<TenantDomainClaimService>.Instance);
```
**NEW (appears twice)**
```csharp
        var service = new TenantDomainClaimService(
            db,
            NullLogger<TenantDomainClaimService>.Instance,
            Options.Create(new PublicEmailDomainOptions()));
```
Add `using Microsoft.Extensions.Options;` to the test file if it is not already present.

---

### B2. M4 — remove the non-compiling using; (optionally) wire validation

`TenantSettingsValidator.cs` has `using System.Text.Json.Schema;` which is unused and fails to
compile on .NET 8. Remove it (mandatory).

**File:** `MrWhoOidc.Auth/Validation/TenantSettingsValidator.cs`
**OLD** `using System.Text.Json.Schema;`
**NEW** *(delete the line entirely)*

> The class only checks JSON well-formedness, not a schema, despite its name. Leave the body as
> is. Wiring it into a tenant update path is optional follow-up work — do not invent an update
> method.

---

### B3. L4 — actually write an audit log on tenant creation

`TenantAuditLog` is mapped but never written. Add one write at the end of `CreateTenantAsync`,
just before the tenant is returned.

**File:** `MrWhoOidc.Auth/Services/TenantService.cs`

Find the end of the long `CreateTenantAsync(..., string? slug, ...)` method (it ends with
`return tenant;`). Immediately **before** `return tenant;` insert:
```csharp
        _db.TenantAuditLogs.Add(new TenantAuditLog
        {
            TenantId = tenant.Id,
            Action = "Created",
            PerformedBy = creatorUserAccountId.ToString(),
            OccurredAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
```
Add `using MrWhoOidc.Auth.Persistence;` if not already imported (it usually is).

---

### B4. M2 + M3 — unimplemented options/exception

`TenantCreationOptions.cs` (rate limiting) and `TenantOperationException.cs` are created but not
implemented or referenced anywhere. Implementing them properly is out of scope for this pass.
**Delete the two unused files** so the codebase has no dead scaffolding:
```bash
rm MrWhoOidc.Auth/Options/TenantCreationOptions.cs
rm MrWhoOidc.Auth/Exceptions/TenantOperationException.cs
```
Then check nothing references them (should print nothing):
```bash
grep -rn "TenantCreationOptions\|TenantOperationException" --include=*.cs MrWhoOidc.Auth MrWhoOidc.UnitTests
```

**Verification for Group B:**
```bash
dotnet build MrWhoOidc.Auth/MrWhoOidc.Auth.csproj
```

---

## Group C — Domain verification (H2): close the silent break

Default status was changed to `PendingVerification`, but auto-join enrollment matching filters on
`Status == Verified` (see `TenantDomainClaimService`, the auto-join match query). With no path to
`Verified`, **auto-join silently stops working**. Add an explicit admin verification method so a
claim can be moved to `Verified` on demand.

**Files:** `MrWhoOidc.Auth/Services/TenantDomainClaimService.cs` and its interface
`ITenantDomainClaimService` (in the same file, near the top).

Add this to the **interface**:
```csharp
    Task<bool> MarkClaimVerifiedAsync(Guid claimId, CancellationToken ct = default);
```

Add this **method** to the class:
```csharp
    public async Task<bool> MarkClaimVerifiedAsync(Guid claimId, CancellationToken ct = default)
    {
        var claim = await db.TenantDomainClaims.FirstOrDefaultAsync(c => c.Id == claimId, ct);
        if (claim is null || claim.Status == TenantDomainClaimStatus.Revoked)
        {
            return false;
        }

        claim.Status = TenantDomainClaimStatus.Verified;
        claim.VerifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Domain claim {DomainClaimId} marked verified", claim.Id);
        return true;
    }
```

> Real DNS-based verification (token + DNS TXT check + background job) is the proper long-term
> fix and is **not** done here. This method only unblocks the enrollment flow via an explicit
> admin action. Leave a `// TODO: implement DNS verification` comment above the method.

**Verification:**
```bash
dotnet build MrWhoOidc.Auth/MrWhoOidc.Auth.csproj
```

---

## Group D — Regenerate the EF migration (it is currently broken)

The hand-written `20260603120000_AddUniqueIndexOnTenantName` migration has **no `.Designer.cs`
file**, the `AuthDbContextModelSnapshot` was **not updated**, and it omits the new
`TenantAuditLogs` table and the `TenantIcon` FK change. EF will report the model is out of sync
and the `TenantAuditLogs` table will not be created.

Steps:

1. Delete the broken migration:
   ```bash
   rm MrWhoOidc.Auth/Persistence/Migrations/20260603120000_AddUniqueIndexOnTenantName.cs
   ```
2. Make sure the EF tools are installed:
   ```bash
   dotnet tool install --global dotnet-ef   # skip if already installed
   ```
3. From the repo root, generate a fresh migration that captures **all** current model changes
   (unique Name index + `TenantAuditLog` table; the `TenantIcon` FK reverts to its original
   `SetNull` after fix A1, so it should produce no FK change). Replace `<StartupProject>` with
   the web/host project that wires up `AuthDbContext` (look for the project whose
   `Program.cs`/`appsettings.json` configures the DB; e.g. the API or Web project):
   ```bash
   dotnet ef migrations add AddTenantNameIndexAndAuditLog \
     --project MrWhoOidc.Auth/MrWhoOidc.Auth.csproj \
     --startup-project <StartupProject>/<StartupProject>.csproj \
     --output-dir Persistence/Migrations
   ```
4. Confirm the generated files exist and the snapshot changed:
   ```bash
   git status --porcelain | grep -E "Migrations|ModelSnapshot"
   ```
   You should see a new `*_AddTenantNameIndexAndAuditLog.cs`, its `.Designer.cs`, and a modified
   `AuthDbContextModelSnapshot.cs`.

> If `dotnet ef` cannot run in this environment, STOP and report that the migration must be
> regenerated manually — do **not** hand-edit the snapshot.

---

## Group E — Test hygiene

**File:** `MrWhoOidc.UnitTests/MultiTenancy/MultiTenancyFixesTests.cs`

E1. Remove the duplicated DI registration. There are two identical lines registering
`IMultiTenancyOptions`; delete the second one.
**OLD**
```csharp
        // Multi-tenancy options (for backward compatibility with resolver)
        services.AddSingleton<IMultiTenancyOptions>(multiTenancyStateProvider);

        // Multi-tenancy options (for backward compatibility with resolver)
        services.AddSingleton<IMultiTenancyOptions>(multiTenancyStateProvider);
```
**NEW**
```csharp
        // Multi-tenancy options (for backward compatibility with resolver)
        services.AddSingleton<IMultiTenancyOptions>(multiTenancyStateProvider);
```

E2. Add a real navigation-guard test (the existing M1 test only exercises the scalar `TenantId`
guard). Add this method inside the `#region M1` block:
```csharp
    [TestMethod]
    public async Task M1_TenantWriteGuards_NavigationToDifferentTenant_ThrowsException()
    {
        var mockAccessor = new MockTenantAccessor();
        var db = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            mockAccessor);

        var tenant1 = await SeedTenantAsync(db, "tenant1", "Tenant 1");
        var tenant2 = await SeedTenantAsync(db, "tenant2", "Tenant 2");

        mockAccessor.SetTenant(new TenantContext
        {
            TenantId = tenant1.Id,
            Slug = "tenant1",
            Name = "Tenant 1",
            IssuerUri = "https://localhost:5001/t/tenant1",
            IsMultiTenantMode = true
        });

        // A TenantIcon whose Tenant navigation points at the wrong tenant.
        var icon = new TenantIconEntity { Id = Guid.NewGuid(), Tenant = tenant2 };
        db.Set<TenantIconEntity>().Add(icon);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
```
> If `TenantIconEntity` has required fields beyond `Id`/`Tenant`, set them so the only validation
> failure is the cross-tenant navigation. Adjust the type/namespace to match the real entity.

**Final verification (run the whole suite):**
```bash
dotnet test MrWhoOidc.UnitTests/MrWhoOidc.UnitTests.csproj
```
All tests must pass. If the C1 case-insensitive tests rely on the in-memory provider, they should
still pass because fix A4 lowercases the input and stored slugs are lowercase.

---

## Done-criteria checklist

- [ ] A1 icon FK is `SetNull`
- [ ] A2 write guard uses `entry.References` / `TargetEntry`
- [ ] A3 `GenerateUniqueSlugAsync` checks the DB and returns lowercase hex; call site `await`s it
- [ ] A4 resolver uses `t.Slug == normalizedSlug` (no `EF.Functions.Like`)
- [ ] A5 duplicate name/slug checks have no `Status == Active` filter
- [ ] B1 `TenantDomainClaimService` injects `IOptions<PublicEmailDomainOptions>`; options registered; tests updated
- [ ] B2 bad `using System.Text.Json.Schema;` removed
- [ ] B3 audit log written on create
- [ ] B4 unused `TenantCreationOptions` / `TenantOperationException` deleted
- [ ] C `MarkClaimVerifiedAsync` added
- [ ] D migration regenerated with `.Designer.cs` + updated snapshot
- [ ] E duplicate DI line removed; navigation-guard test added
- [ ] `dotnet build` and `dotnet test` both green
