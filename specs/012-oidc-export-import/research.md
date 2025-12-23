# Research: OIDC Configuration Export/Import

**Feature**: 012-oidc-export-import  
**Date**: 2024-12-23

## Research Questions Addressed

1. Existing SeedManifest schema and patterns
2. Entity structures for export (Tenant, Realm, Client, IdentityProvider)
3. Secret handling strategies
4. Transaction patterns for import operations
5. Audit logging patterns

---

## 1. Existing SeedManifest Schema

**Decision**: Extend the existing SeedManifest schema rather than creating a separate export format.

**Rationale**: 
- SeedManifest already defines the structure for tenants, realms, clients, and scopes
- Consistent format between seeding and export/import reduces complexity
- Existing JSON serialization infrastructure can be reused

**Alternatives Considered**:
- Separate ExportFormat: Rejected - would duplicate definitions and create format drift
- Protocol Buffers: Rejected - overkill for configuration data, JSON is human-readable

### Current SeedManifest Structure

```csharp
SeedManifest
├── version: int (default: 1)
├── scopes: List<ScopeSeedDefinition>
│   ├── name: string (required)
│   ├── description: string?
│   ├── isGlobal: bool?
│   ├── isExposed: bool?
│   └── tenantSlug: string?
│
└── tenants: List<TenantSeedDefinition>
    ├── slug: string (required)
    ├── name: string (required)
    ├── description: string?
    ├── issuerUri: string?
    ├── adminEmail: string?
    ├── billingPlan: string?
    ├── realms: List<RealmSeedDefinition>
    │   ├── name: string (required)
    │   ├── displayName: string?
    │   └── allowUnconfirmedLogin: bool?
    │
    └── clients: List<ClientSeedDefinition>
        ├── clientId: string (required)
        ├── clientName: string (required)
        ├── realm: string?
        ├── requirePkce: bool?
        ├── requireConsent: bool?
        ├── autoApprovalMode: string?
        ├── clientSecret: string?           # Plaintext (dev only)
        ├── clientSecretEnv: string?        # Config key
        ├── allowedLoginRedirectUris: List<string>
        ├── allowedLogoutRedirectUris: List<string>
        ├── allowedScopes: List<string>
        └── obo*: various OBO settings
```

### Missing from Current Schema (Required for Export)

1. **IdentityProvider definitions** - Not supported in current SeedManifest
2. **ClientSecret multi-secret support** - Current schema uses deprecated single secret field
3. **Client JWKS/public keys** - Not captured
4. **Export metadata** - No timestamp, exporter, version info
5. **Secret obfuscation markers** - No standard placeholder format
6. **Roles** - Realm-specific roles not in manifest

---

## 2. Entity Structures for Export

### Tenant Entity Fields

| Field | Export | Notes |
|-------|--------|-------|
| Id | NO | Auto-generated on import |
| Slug | YES | Primary identifier |
| Name | YES | |
| Description | YES | |
| IssuerUri | YES | May need recalculation on import |
| Status | YES | |
| LogoUrl | YES | |
| PrimaryColor | YES | |
| AccentColor | YES | |
| SettingsJson | YES | |
| AdminEmail | YES | |
| BillingPlan | YES | |
| MetadataJson | YES | |
| MaxUsers | YES | License limit |
| MaxClients | YES | License limit |
| CreatedAt | NO | Reset on import |
| TenantIconId | NO | FK, handle separately |

### Client Entity Fields

| Field | Export | Secret Handling |
|-------|--------|-----------------|
| ClientId | YES | |
| ClientName | YES | |
| RequirePkce | YES | |
| RequireConsent | YES | |
| RequirePar | YES | |
| ClientSecretHash | CONDITIONAL | Obfuscate or include hash |
| PublicJwksJson | YES | Public keys only |
| PublicJwksUri | YES | |
| AllowedLoginRedirectUrisJson | YES | |
| AllowedLogoutRedirectUrisJson | YES | |
| BackChannelLogoutUri | YES | |
| FrontChannelLogoutUri | YES | |
| OBO settings | YES | |
| M2M settings | YES | |
| Auto-provision settings | YES | |

### IdentityProvider Entity Fields

| Field | Export | Secret Handling |
|-------|--------|-----------------|
| Name | YES | Primary identifier |
| DisplayName | YES | |
| Type | YES | Oidc/Saml enum |
| Enabled | YES | |
| IsDefault | YES | |
| LogoUrl | YES | |
| SortOrder | YES | |
| ConfigJson | CONDITIONAL | ClientSecret in config obfuscated |

### IdentityProviderKey Fields

| Field | Export | Notes |
|-------|--------|-------|
| Purpose | YES | Signing/Encryption |
| Jwk | CONDITIONAL | Public keys only, private keys NEVER |
| Alg | YES | |
| Active | YES | |
| Publishable | YES | |
| Kid | YES | |

---

## 3. Secret Handling Strategy

**Decision**: Two export modes with clear handling rules.

### Obfuscated Mode (Default)

```json
{
  "clientSecret": "***OBFUSCATED***",
  "clientSecretHash": "***OBFUSCATED***",
  "identityProviders": [{
    "configJson": {
      "clientId": "actual-client-id",
      "clientSecret": "***OBFUSCATED***"
    }
  }]
}
```

**Rationale**: 
- Safe for sharing, version control, documentation
- Clear marker indicates value must be provided on import
- Consistent placeholder across all secret fields

### Full Export Mode (With Hashes)

```json
{
  "clientSecretHash": "$argon2id$v=19$m=65536...",
  "identityProviders": [{
    "configJson": {
      "clientId": "actual-client-id",
      "clientSecret": "***OBFUSCATED***"  // IdP secrets always obfuscated
    }
  }]
}
```

**Rationale**:
- Hashes are one-way, cannot be reversed
- Enables exact restoration of authentication state
- External IdP secrets still obfuscated (we don't own them)

### Import Handling

| Export Mode | Secret State | Import Behavior |
|-------------|--------------|-----------------|
| Obfuscated | `***OBFUSCATED***` | Prompt user for value OR create client without secret |
| Full | Hash value | Store hash directly |
| Full | Empty/null | Public client (no secret) |

---

## 4. Transaction Patterns

**Decision**: Use EF Core execution strategy with explicit transactions for all imports.

**Rationale**: 
- Current SeedManifestApplier lacks transactions (partial failure risk)
- Import operations must be atomic (all or nothing)
- Execution strategy handles PostgreSQL transient failures

### Recommended Pattern

```csharp
var strategy = db.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    try
    {
        // 1. Validate all entities
        // 2. Resolve conflicts
        // 3. Create/update entities
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ImportResult.Success(...);
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync(ct);
        return ImportResult.Failure(ex.Message);
    }
});
```

### Processing Order (Dependency Resolution)

1. **Scopes** - No dependencies
2. **Realms** - Depends on Tenant
3. **Roles** - Depends on Tenant, Realm
4. **Clients** - Depends on Tenant, Realm
5. **ClientScopes** - Depends on Client, Scope
6. **IdentityProviders** - Depends on Tenant
7. **ClientIdentityProviders** - Depends on Client, IdentityProvider
8. **IdentityProviderClaimMappings** - Depends on IdentityProvider
9. **IdentityProviderKeys** - Depends on IdentityProvider

---

## 5. Audit Logging Pattern

**Decision**: Create dedicated audit entity + structured logging.

**Rationale**:
- Existing patterns: ImpersonationAuditLog, RevocationAudit, BackchannelLogoutNotification
- Queryable audit trail required for compliance
- Structured logging supplements with operational detail

### Proposed Audit Entity

```csharp
public class ConfigurationAuditLog
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    public Guid? TenantId { get; set; }              // null for platform-level
    public string Operation { get; set; }            // Export|Import
    public string EntityType { get; set; }           // Tenant|Realm|Client|Provider
    public string? EntityIdentifier { get; set; }    // slug, clientId, name
    public string ExportMode { get; set; }           // Obfuscated|Full
    public string? Result { get; set; }              // Success|Failed|PartialSuccess
    public int? EntitiesCreated { get; set; }
    public int? EntitiesUpdated { get; set; }
    public int? EntitiesSkipped { get; set; }
    public string? ErrorDetails { get; set; }
    public string PerformedBy { get; set; }          // Username
    public Guid? PerformedByUserId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
```

### Structured Log Events

```csharp
// Export started
logger.LogInformation(
    "Configuration export started: {Operation} {EntityType} {EntityId} by {User}",
    "Export", "Tenant", tenantSlug, user);

// Export completed
logger.LogInformation(
    "Configuration export completed: {EntityType} {EntityId}, Mode={Mode}, Size={Bytes}",
    "Tenant", tenantSlug, "Obfuscated", fileSize);

// Import validation
logger.LogInformation(
    "Configuration import validation: {EntityType}, Conflicts={Conflicts}",
    "Tenant", conflictCount);

// Import completed
logger.LogInformation(
    "Configuration import completed: {EntityType}, Created={Created}, Updated={Updated}, Skipped={Skipped}",
    "Tenant", created, updated, skipped);
```

---

## 6. Conflict Resolution Strategy

**Decision**: Four resolution modes with explicit user selection.

| Mode | Behavior |
|------|----------|
| **Skip** | Do not import conflicting entity, continue with others |
| **Rename** | Auto-rename (append suffix) and import as new |
| **Merge** | Update only non-conflicting fields, preserve existing values |
| **Overwrite** | Replace entire entity with imported data |

### Conflict Detection

```csharp
public enum ConflictType
{
    TenantSlugExists,
    RealmNameExists,
    ClientIdExists,
    ProviderNameExists,
    ScopeNameConflict    // Global vs tenant-scoped
}

public record ImportConflict(
    ConflictType Type,
    string EntityType,
    string Identifier,
    Guid? ExistingEntityId,
    string? SuggestedRename
);
```

---

## 7. Export Manifest Schema Extension

**Decision**: Wrap SeedManifest with export metadata container.

```csharp
public sealed record ExportManifest
{
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://mrwhooidc.io/schemas/export/v1";
    
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;
    
    [JsonPropertyName("exportType")]
    public string ExportType { get; init; } = "tenant";  // tenant|realm|client|provider
    
    [JsonPropertyName("exportMode")]
    public string ExportMode { get; init; } = "obfuscated";  // obfuscated|full
    
    [JsonPropertyName("metadata")]
    public ExportMetadata Metadata { get; init; } = new();
    
    [JsonPropertyName("data")]
    public SeedManifest Data { get; init; } = new();
}

public sealed record ExportMetadata
{
    [JsonPropertyName("exportedAt")]
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;
    
    [JsonPropertyName("exportedBy")]
    public string? ExportedBy { get; init; }
    
    [JsonPropertyName("sourceSystem")]
    public string? SourceSystem { get; init; }
    
    [JsonPropertyName("sourceVersion")]
    public string? SourceVersion { get; init; }
    
    [JsonPropertyName("sourceTenant")]
    public string? SourceTenant { get; init; }
    
    [JsonPropertyName("checksum")]
    public string? Checksum { get; init; }  // SHA-256 of Data
}
```

---

## 8. Extended SeedManifest Types

### IdentityProviderSeedDefinition (NEW)

```csharp
public sealed record IdentityProviderSeedDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }
    
    [JsonPropertyName("type")]
    public string Type { get; init; } = "oidc";  // oidc|saml
    
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }
    
    [JsonPropertyName("isDefault")]
    public bool? IsDefault { get; init; }
    
    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; init; }
    
    [JsonPropertyName("sortOrder")]
    public int? SortOrder { get; init; }
    
    [JsonPropertyName("config")]
    public Dictionary<string, object>? Config { get; init; }
    
    [JsonPropertyName("claimMappings")]
    public List<ClaimMappingSeedDefinition> ClaimMappings { get; init; } = [];
    
    [JsonPropertyName("keys")]
    public List<ProviderKeySeedDefinition> Keys { get; init; } = [];
}

public sealed record ClaimMappingSeedDefinition
{
    [JsonPropertyName("externalClaim")]
    public required string ExternalClaim { get; init; }
    
    [JsonPropertyName("localClaim")]
    public required string LocalClaim { get; init; }
    
    [JsonPropertyName("transform")]
    public string? Transform { get; init; }
    
    [JsonPropertyName("order")]
    public int? Order { get; init; }
}

public sealed record ProviderKeySeedDefinition
{
    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = "signing";  // signing|encryption
    
    [JsonPropertyName("alg")]
    public string Alg { get; init; } = "RS256";
    
    [JsonPropertyName("kid")]
    public string? Kid { get; init; }
    
    [JsonPropertyName("jwk")]
    public string? Jwk { get; init; }  // Public key only
    
    [JsonPropertyName("active")]
    public bool? Active { get; init; }
}
```

### Extended TenantSeedDefinition

```csharp
// Add to existing TenantSeedDefinition:
[JsonPropertyName("identityProviders")]
public List<IdentityProviderSeedDefinition> IdentityProviders { get; init; } = [];

[JsonPropertyName("roles")]
public List<RoleSeedDefinition> Roles { get; init; } = [];
```

### RoleSeedDefinition (NEW)

```csharp
public sealed record RoleSeedDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    
    [JsonPropertyName("realmName")]
    public required string RealmName { get; init; }
    
    [JsonPropertyName("isActive")]
    public bool? IsActive { get; init; }
}
```

### Extended ClientSeedDefinition

```csharp
// Add to existing ClientSeedDefinition:
[JsonPropertyName("clientSecretHash")]
public string? ClientSecretHash { get; init; }  // For full export mode

[JsonPropertyName("publicJwksJson")]
public string? PublicJwksJson { get; init; }

[JsonPropertyName("publicJwksUri")]
public string? PublicJwksUri { get; init; }

[JsonPropertyName("identityProviderAssignments")]
public List<ClientIdpAssignmentSeedDefinition> IdentityProviderAssignments { get; init; } = [];

// Additional client settings for completeness
[JsonPropertyName("requirePar")]
public bool? RequirePar { get; init; }

[JsonPropertyName("allowLocalLogin")]
public bool? AllowLocalLogin { get; init; }

[JsonPropertyName("allowExternalIdp")]
public bool? AllowExternalIdp { get; init; }

[JsonPropertyName("allowQrLogin")]
public bool? AllowQrLogin { get; init; }

[JsonPropertyName("backChannelLogoutUri")]
public string? BackChannelLogoutUri { get; init; }

[JsonPropertyName("frontChannelLogoutUri")]
public string? FrontChannelLogoutUri { get; init; }
```

### ClientIdpAssignmentSeedDefinition (NEW)

```csharp
public sealed record ClientIdpAssignmentSeedDefinition
{
    [JsonPropertyName("providerName")]
    public required string ProviderName { get; init; }
    
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }
    
    [JsonPropertyName("isDefaultForClient")]
    public bool? IsDefaultForClient { get; init; }
    
    [JsonPropertyName("autoRedirectIfSingle")]
    public bool? AutoRedirectIfSingle { get; init; }
    
    [JsonPropertyName("requiredAcr")]
    public string? RequiredAcr { get; init; }
    
    [JsonPropertyName("order")]
    public int? Order { get; init; }
}
```

---

## Summary of Decisions

| Area | Decision | Key Rationale |
|------|----------|---------------|
| Schema | Extend SeedManifest | Consistency, reuse existing infrastructure |
| Secrets | Obfuscated default, hash optional | Security + restoration flexibility |
| Transactions | Execution strategy + explicit TX | Atomicity, PostgreSQL compatibility |
| Audit | Dedicated entity + structured logs | Compliance, queryability |
| Conflicts | 4-mode resolution | User control over import behavior |
| IdP Export | Public keys only | Private keys must never be exported |
