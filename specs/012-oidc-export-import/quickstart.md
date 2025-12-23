# Quickstart: OIDC Configuration Export/Import

**Feature**: 012-oidc-export-import  
**Date**: 2024-12-23

## Overview

This guide covers implementing the export/import feature for MrWhoOidc configuration management.

---

## Prerequisites

- MrWhoOidc solution cloned and building
- PostgreSQL running via Aspire (`dotnet run --project MrWhoOidc.AppHost`)
- Familiarity with existing SeedManifest pattern

---

## Implementation Steps

### Phase 1: Core Domain Services (MrWhoOidc.Auth)

#### 1.1 Extend SeedManifest Schema

**File**: `MrWhoOidc.Auth/Seeding/SeedManifest.cs`

Add new seed definition types:
- `IdentityProviderSeedDefinition`
- `ClaimMappingSeedDefinition`
- `ProviderKeySeedDefinition`
- `ClientIdpAssignmentSeedDefinition`
- `RoleSeedDefinition`

Extend existing types:
- `TenantSeedDefinition` - add `identityProviders`, `roles` properties
- `ClientSeedDefinition` - add IdP assignments, logout URIs, M2M settings

#### 1.2 Create Export Manifest Container

**File**: `MrWhoOidc.Auth/Seeding/ExportManifest.cs`

```csharp
public sealed record ExportManifest
{
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://mrwhooidc.io/schemas/export/v1";
    
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;
    
    [JsonPropertyName("exportType")]
    public string ExportType { get; init; } = "tenant";
    
    [JsonPropertyName("exportMode")]
    public string ExportMode { get; init; } = "obfuscated";
    
    [JsonPropertyName("metadata")]
    public ExportMetadata Metadata { get; init; } = new();
    
    [JsonPropertyName("data")]
    public SeedManifest Data { get; init; } = new();
}
```

#### 1.3 Create Export Service

**File**: `MrWhoOidc.Auth/Services/IConfigurationExportService.cs`

```csharp
public interface IConfigurationExportService
{
    Task<ExportManifest> ExportTenantAsync(Guid tenantId, ExportOptions options, CancellationToken ct = default);
    Task<ExportManifest> ExportRealmAsync(Guid realmId, ExportOptions options, CancellationToken ct = default);
    Task<ExportManifest> ExportClientAsync(Guid clientId, ExportOptions options, CancellationToken ct = default);
    Task<ExportManifest> ExportProviderAsync(Guid providerId, ExportOptions options, CancellationToken ct = default);
}
```

#### 1.4 Create Import Service

**File**: `MrWhoOidc.Auth/Services/IConfigurationImportService.cs`

```csharp
public interface IConfigurationImportService
{
    Task<ImportPreview> PreviewImportAsync(ExportManifest manifest, CancellationToken ct = default);
    Task<ImportResult> ImportAsync(ExportManifest manifest, ImportOptions options, CancellationToken ct = default);
}
```

#### 1.5 Add Audit Entity

**File**: `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`

Add `ConfigurationAuditLog` entity and configure in `OnModelCreating`.

**Migration**:
```bash
dotnet ef migrations add AddConfigurationAuditLog --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations
```

---

### Phase 2: HTTP Surface (MrWhoOidc.WebAuth)

#### 2.1 Add API Endpoints

**File**: `MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs`

Register in `Program.cs`:
```csharp
// Platform Admin - Tenant Export/Import
app.MapGet("/admin/api/platform/tenants/{slug}/export", ExportImportHandler.ExportTenant)
   .RequireAuthorization("platform-admin");
app.MapPost("/admin/api/platform/tenants/import/preview", ExportImportHandler.PreviewImportTenant)
   .RequireAuthorization("platform-admin");
app.MapPost("/admin/api/platform/tenants/import", ExportImportHandler.ImportTenant)
   .RequireAuthorization("platform-admin");

// Tenant Admin - Realm/Client/Provider Export/Import
app.MapGet("/admin/api/realms/{id}/export", ExportImportHandler.ExportRealm)
   .RequireAuthorization("tenant-admin");
// ... additional endpoints
```

#### 2.2 Add Razor Pages for UI

**Platform Admin - Tenant Export**:
- `Pages/PlatformAdmin/Tenants/Export.cshtml`
- `Pages/PlatformAdmin/Tenants/Import.cshtml`

**Tenant Admin - Realm/Client/Provider Export**:
- `Pages/Admin/Realms/Export.cshtml`
- `Pages/Admin/Clients/Export.cshtml`
- `Pages/Admin/Providers/Export.cshtml`

#### 2.3 Enhance SeedManifestApplier

**File**: `MrWhoOidc.WebAuth/Seeding/SeedManifestApplier.cs`

Add:
- Transaction wrapper using execution strategy
- Conflict detection and resolution
- Identity provider import logic
- Audit logging integration

---

### Phase 3: Testing (MrWhoOidc.UnitTests)

#### 3.1 Unit Tests

**File**: `MrWhoOidc.UnitTests/Export/ConfigurationExportServiceTests.cs`

Test cases:
- Export tenant with all entities
- Export realm with clients
- Export single client
- Export identity provider
- Obfuscated mode hides secrets
- Full mode includes hashes

#### 3.2 Import Tests

**File**: `MrWhoOidc.UnitTests/Import/ConfigurationImportServiceTests.cs`

Test cases:
- Import new tenant creates all entities
- Import with conflicts detected
- Skip resolution skips entity
- Rename resolution creates new entity
- Merge resolution updates fields
- Overwrite resolution replaces entity
- Transaction rollback on error

#### 3.3 Round-Trip Tests

**File**: `MrWhoOidc.UnitTests/Export/ExportImportRoundTripTests.cs`

Test cases:
- Export then import produces equivalent configuration
- Manifest serialization/deserialization preserves data
- Version compatibility checks

---

## Key Patterns

### Secret Obfuscation

```csharp
private const string ObfuscatedMarker = "***OBFUSCATED***";

private string ObfuscateSecret(string? value) =>
    string.IsNullOrEmpty(value) ? null : ObfuscatedMarker;

private bool IsObfuscated(string? value) =>
    value == ObfuscatedMarker;
```

### Transaction Wrapper

```csharp
var strategy = db.Database.CreateExecutionStrategy();
return await strategy.ExecuteAsync(async () =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    try
    {
        // ... import operations
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ImportResult.Success(...);
    }
    catch
    {
        await transaction.RollbackAsync(ct);
        throw;
    }
});
```

### Entity Reference Resolution

```csharp
// Resolve realm by name within tenant
var realm = await db.Realms
    .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == realmName, ct);

// Resolve IdP by name within tenant
var provider = await db.IdentityProviders
    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Name == providerName, ct);
```

---

## Testing Checklist

- [ ] Export tenant produces valid JSON
- [ ] Export obfuscates secrets correctly
- [ ] Export includes all child entities
- [ ] Import preview detects conflicts
- [ ] Import creates entities in correct order
- [ ] Import handles obfuscated secrets
- [ ] Import rolls back on error
- [ ] Audit log records all operations
- [ ] RBAC enforced on all endpoints
- [ ] Rate limiting applied

---

## Common Issues

### Issue: Foreign Key Violations During Import

**Cause**: Entities imported out of dependency order.

**Solution**: Process in order: Scopes → Realms → Roles → Clients → IdPs → Mappings

### Issue: Transaction Deadlock

**Cause**: Long-running import with concurrent operations.

**Solution**: Use execution strategy with retry policy, consider row-level locking hints.

### Issue: Large Export Files

**Cause**: Tenant with many clients/providers.

**Solution**: Implement streaming JSON serialization, consider chunked exports for very large tenants.
