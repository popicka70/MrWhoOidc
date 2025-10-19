# Data Model: License Key System

## Overview

This document defines the data model for the License Key System in MrWhoOidc. The design integrates with the existing `AuthDbContext` and follows the established patterns for multi-tenancy, UUIDv7 primary keys, and domain-driven architecture.

## Entity Definitions

### License Entity

Primary entity storing active license information.

```csharp
public class License
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    
    // Multi-tenancy support - NULL for platform-wide licenses
    public Guid? TenantId { get; set; }
    
    // License key as received from customer (JWS format)
    [MaxLength(2000)]
    public string LicenseKey { get; set; } = string.Empty;
    
    // Parsed license information
    [MaxLength(50)]
    public string Tier { get; set; } = string.Empty; // "community", "professional", "enterprise", "enterprise+"
    
    [MaxLength(500)]
    public string? OrganizationName { get; set; }
    
    // Validity period
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    
    // Status tracking
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    
    // Audit fields
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    
    // Navigation properties
    public Tenant? Tenant { get; set; }
    public User? CreatedByUser { get; set; }
    public User? UpdatedByUser { get; set; }
    public ICollection<LicenseHistoryEntry> History { get; set; } = new List<LicenseHistoryEntry>();
    public ICollection<FeatureUsageMetric> UsageMetrics { get; set; } = new List<FeatureUsageMetric>();
}
```

### LicenseHistoryEntry Entity

Audit trail for all license-related changes.

```csharp
public class LicenseHistoryEntry
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    
    public Guid LicenseId { get; set; }
    
    [MaxLength(50)]
    public string Action { get; set; } = string.Empty; // "installed", "updated", "expired", "revoked", "validated"
    
    [MaxLength(2000)]
    public string? OldLicenseKey { get; set; }
    
    [MaxLength(2000)]
    public string? NewLicenseKey { get; set; }
    
    [MaxLength(50)]
    public string? OldTier { get; set; }
    
    [MaxLength(50)]
    public string? NewTier { get; set; }
    
    [MaxLength(1000)]
    public string? Notes { get; set; }
    
    [MaxLength(500)]
    public string? Reason { get; set; }
    
    // Audit fields
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    
    // Client information for web requests
    [MaxLength(200)]
    public string? UserAgent { get; set; }
    
    [MaxLength(45)]
    public string? IpAddress { get; set; }
    
    // Navigation properties
    public License License { get; set; } = null!;
    public User? CreatedByUser { get; set; }
}
```

### FeatureUsageMetric Entity

Track feature usage for analytics and compliance reporting.

```csharp
public class FeatureUsageMetric
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    
    public Guid? LicenseId { get; set; }
    public Guid? TenantId { get; set; }
    
    [MaxLength(100)]
    public string FeatureName { get; set; } = string.Empty;
    
    public long UsageCount { get; set; } = 1;
    public DateTimeOffset FirstUsed { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    
    // Aggregation period (for time-series data)
    public DateOnly AggregationDate { get; set; }
    
    // Navigation properties
    public License? License { get; set; }
    public Tenant? Tenant { get; set; }
}
```

### LicenseLimit Entity

Store configurable limits for different license tiers.

```csharp
public class LicenseLimit
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    
    [MaxLength(50)]
    public string Tier { get; set; } = string.Empty;
    
    [MaxLength(100)]
    public string LimitType { get; set; } = string.Empty; // "users", "tenants", "api_calls_per_hour"
    
    public long LimitValue { get; set; } = -1; // -1 for unlimited
    
    public bool IsActive { get; set; } = true;
    
    // Audit fields
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

## Value Objects

### LicenseInfo

Read-only value object representing parsed license information.

```csharp
public record LicenseInfo(
    string Tier,
    string? OrganizationName,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    IReadOnlySet<string> EnabledFeatures,
    IReadOnlyDictionary<string, long> Limits,
    bool IsExpired,
    bool IsValid
)
{
    public bool IsFeatureEnabled(string featureName) => EnabledFeatures.Contains(featureName);
    
    public long GetLimit(string limitType) => Limits.TryGetValue(limitType, out var limit) ? limit : 0;
    
    public bool HasUnlimitedAccess(string limitType) => GetLimit(limitType) == -1;
    
    public TimeSpan TimeUntilExpiry => ValidUntil - DateTimeOffset.UtcNow;
    
    public bool IsNearExpiry(TimeSpan threshold) => TimeUntilExpiry <= threshold && TimeUntilExpiry > TimeSpan.Zero;
}
```

### LicenseValidationResult

Result of license validation operations.

```csharp
public record LicenseValidationResult(
    bool IsValid,
    string? ErrorCode,
    string? ErrorMessage,
    LicenseInfo? LicenseInfo
)
{
    public static LicenseValidationResult Success(LicenseInfo licenseInfo) =>
        new(true, null, null, licenseInfo);
    
    public static LicenseValidationResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null);
    
    public static LicenseValidationResult InvalidSignature() =>
        Failure("invalid_signature", "License signature is invalid or tampered");
    
    public static LicenseValidationResult Expired() =>
        Failure("expired", "License has expired");
    
    public static LicenseValidationResult NotYetValid() =>
        Failure("not_yet_valid", "License is not yet valid");
    
    public static LicenseValidationResult InvalidFormat() =>
        Failure("invalid_format", "License format is invalid");
}
```

## Enumerations

### LicenseTier

Strongly-typed license tier enumeration.

```csharp
public enum LicenseTier
{
    Community = 0,
    Professional = 1,
    Enterprise = 2,
    EnterprisePlus = 3
}

public static class LicenseTierExtensions
{
    public static string ToTierString(this LicenseTier tier) => tier switch
    {
        LicenseTier.Community => "community",
        LicenseTier.Professional => "professional", 
        LicenseTier.Enterprise => "enterprise",
        LicenseTier.EnterprisePlus => "enterprise+",
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };
    
    public static LicenseTier FromTierString(string tierString) => tierString.ToLowerInvariant() switch
    {
        "community" => LicenseTier.Community,
        "professional" => LicenseTier.Professional,
        "enterprise" => LicenseTier.Enterprise,
        "enterprise+" => LicenseTier.EnterprisePlus,
        _ => throw new ArgumentException($"Unknown license tier: {tierString}")
    };
}
```

### FeatureFlag

Licensed feature identifiers.

```csharp
public static class FeatureFlags
{
    // Basic features (Community+)
    public const string BasicOidc = "basic_oidc";
    public const string BasicAdminUi = "basic_admin_ui";
    
    // Professional features
    public const string MultiTenancy = "multi_tenancy";
    public const string AdvancedSecurity = "advanced_security"; // JAR, JARM, TOTP
    public const string ClientSecretRotation = "client_secret_rotation";
    public const string EnhancedAuditLogging = "enhanced_audit_logging";
    
    // Enterprise features
    public const string UnlimitedScale = "unlimited_scale";
    public const string DPoP = "dpop";
    public const string TokenExchange = "token_exchange";
    public const string BackchannelLogout = "backchannel_logout";
    public const string LdapIntegration = "ldap_integration";
    public const string CustomClaimMappings = "custom_claim_mappings";
    public const string AdvancedMonitoring = "advanced_monitoring";
    
    // Enterprise+ features  
    public const string WebAuthn = "webauthn";
    public const string RiskBasedAuth = "risk_based_auth";
    public const string HsmIntegration = "hsm_integration";
    public const string ProfessionalServices = "professional_services";
    
    public static readonly IReadOnlySet<string> AllFeatures = new HashSet<string>
    {
        BasicOidc, BasicAdminUi, MultiTenancy, AdvancedSecurity, ClientSecretRotation,
        EnhancedAuditLogging, UnlimitedScale, DPoP, TokenExchange, BackchannelLogout,
        LdapIntegration, CustomClaimMappings, AdvancedMonitoring, WebAuthn,
        RiskBasedAuth, HsmIntegration, ProfessionalServices
    };
    
    public static IReadOnlySet<string> GetFeaturesForTier(LicenseTier tier) => tier switch
    {
        LicenseTier.Community => new HashSet<string> { BasicOidc, BasicAdminUi },
        LicenseTier.Professional => new HashSet<string> 
        { 
            BasicOidc, BasicAdminUi, MultiTenancy, AdvancedSecurity, 
            ClientSecretRotation, EnhancedAuditLogging 
        },
        LicenseTier.Enterprise => new HashSet<string>
        {
            BasicOidc, BasicAdminUi, MultiTenancy, AdvancedSecurity, ClientSecretRotation,
            EnhancedAuditLogging, UnlimitedScale, DPoP, TokenExchange, BackchannelLogout,
            LdapIntegration, CustomClaimMappings, AdvancedMonitoring
        },
        LicenseTier.EnterprisePlus => AllFeatures,
        _ => new HashSet<string>()
    };
}
```

## Database Schema Changes

### AuthDbContext Integration

```csharp
public partial class AuthDbContext
{
    // License entities
    public DbSet<License> Licenses { get; set; } = null!;
    public DbSet<LicenseHistoryEntry> LicenseHistory { get; set; } = null!;
    public DbSet<FeatureUsageMetric> FeatureUsageMetrics { get; set; } = null!;
    public DbSet<LicenseLimit> LicenseLimits { get; set; } = null!;
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        ConfigureLicenseEntities(modelBuilder);
    }
    
    private static void ConfigureLicenseEntities(ModelBuilder modelBuilder)
    {
        // License configuration
        modelBuilder.Entity<License>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });
            entity.HasIndex(e => e.ValidUntil);
            
            entity.HasOne(e => e.Tenant)
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
                  
            entity.HasOne(e => e.CreatedByUser)
                  .WithMany()
                  .HasForeignKey(e => e.CreatedBy)
                  .OnDelete(DeleteBehavior.SetNull);
                  
            entity.HasOne(e => e.UpdatedByUser)
                  .WithMany()
                  .HasForeignKey(e => e.UpdatedBy)
                  .OnDelete(DeleteBehavior.SetNull);
        });
        
        // LicenseHistoryEntry configuration
        modelBuilder.Entity<LicenseHistoryEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.LicenseId);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.LicenseId, e.CreatedAt });
            
            entity.HasOne(e => e.License)
                  .WithMany(l => l.History)
                  .HasForeignKey(e => e.LicenseId)
                  .OnDelete(DeleteBehavior.Cascade);
                  
            entity.HasOne(e => e.CreatedByUser)
                  .WithMany()
                  .HasForeignKey(e => e.CreatedBy)
                  .OnDelete(DeleteBehavior.SetNull);
        });
        
        // FeatureUsageMetric configuration
        modelBuilder.Entity<FeatureUsageMetric>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.FeatureName, e.AggregationDate })
                  .IsUnique();
            entity.HasIndex(e => e.LicenseId);
            entity.HasIndex(e => e.AggregationDate);
            
            entity.HasOne(e => e.License)
                  .WithMany(l => l.UsageMetrics)
                  .HasForeignKey(e => e.LicenseId)
                  .OnDelete(DeleteBehavior.SetNull);
                  
            entity.HasOne(e => e.Tenant)
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
        
        // LicenseLimit configuration
        modelBuilder.Entity<LicenseLimit>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Tier, e.LimitType, e.IsActive })
                  .IsUnique()
                  .HasFilter("IsActive = true");
        });
    }
}
```

## Validation Rules

### License Entity Validation

- `LicenseKey`: Required, max 2000 characters, must be valid JWS format
- `Tier`: Required, must be one of: "community", "professional", "enterprise", "enterprise+"
- `ValidFrom`: Must be before `ValidUntil`
- `ValidUntil`: Must be in the future for new licenses
- `TenantId`: Must reference existing tenant (if specified)

### Business Rules

1. **Single Active License**: Only one active license per tenant (or platform-wide)
2. **License Hierarchy**: Cannot downgrade active features without explicit confirmation
3. **Expiration Grace**: 7-day grace period after license expiration
4. **Tenant Isolation**: Tenant-scoped licenses cannot access platform-wide features
5. **Limit Enforcement**: Current usage must not exceed license limits

## Migration Strategy

### Migration Order

1. Create `Licenses` table
2. Create `LicenseHistoryEntry` table  
3. Create `FeatureUsageMetric` table
4. Create `LicenseLimits` table with default data
5. Add foreign key constraints
6. Create necessary indexes
7. Seed default Community license for existing installations

### Seed Data

```sql
-- Default license limits
INSERT INTO LicenseLimits (Id, Tier, LimitType, LimitValue, IsActive, CreatedAt) VALUES
(gen_random_uuid(), 'community', 'users', 100, true, NOW()),
(gen_random_uuid(), 'community', 'tenants', 1, true, NOW()),
(gen_random_uuid(), 'professional', 'users', 10000, true, NOW()),
(gen_random_uuid(), 'professional', 'tenants', 5, true, NOW()),
(gen_random_uuid(), 'enterprise', 'users', -1, true, NOW()),
(gen_random_uuid(), 'enterprise', 'tenants', -1, true, NOW()),
(gen_random_uuid(), 'enterprise+', 'users', -1, true, NOW()),
(gen_random_uuid(), 'enterprise+', 'tenants', -1, true, NOW());
```

## Performance Considerations

### Indexing Strategy

- Primary indexes on all entity IDs
- Composite index on `(TenantId, IsActive)` for license lookup
- Index on `ValidUntil` for expiration checks
- Index on `(LicenseId, CreatedAt)` for history queries
- Unique index on `(TenantId, FeatureName, AggregationDate)` for usage metrics

### Query Optimization

- License validation queries should use indexes effectively
- Feature usage aggregation should be efficient for reporting
- Historical data can be archived after configurable retention period

### Caching Strategy

- Active licenses cached in memory for 5 minutes
- Feature flags cached per request
- Usage metrics can use eventual consistency
