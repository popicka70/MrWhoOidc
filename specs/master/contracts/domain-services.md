# Domain Service Contracts - License System

## Overview

This document defines the service contracts for the license system domain layer. These interfaces establish the contract between the domain services and the rest of the application.

## Core Service Interfaces

### ILicenseService

Primary service for license management and validation.

```csharp
namespace MrWhoOidc.Auth.Licensing.Services;

public interface ILicenseService
{
    /// <summary>
    /// Gets the current active license information.
    /// </summary>
    /// <param name="tenantId">Tenant ID for tenant-specific license, null for platform license</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current license information or null if no active license</returns>
    Task<LicenseInfo?> GetCurrentLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Validates and installs a new license key.
    /// </summary>
    /// <param name="licenseKey">License key in JWS format</param>
    /// <param name="tenantId">Tenant ID for tenant-specific license, null for platform license</param>
    /// <param name="installedBy">User ID who is installing the license</param>
    /// <param name="notes">Optional notes about the installation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>License validation and installation result</returns>
    Task<LicenseValidationResult> InstallLicenseAsync(
        string licenseKey, 
        Guid? tenantId = null, 
        Guid? installedBy = null, 
        string? notes = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Validates a license key without installing it.
    /// </summary>
    /// <param name="licenseKey">License key to validate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>License validation result</returns>
    Task<LicenseValidationResult> ValidateLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Revokes the current active license.
    /// </summary>
    /// <param name="reason">Reason for revocation</param>
    /// <param name="tenantId">Tenant ID for tenant-specific license, null for platform license</param>
    /// <param name="revokedBy">User ID who is revoking the license</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if license was revoked successfully</returns>
    Task<bool> RevokeLicenseAsync(
        string reason, 
        Guid? tenantId = null, 
        Guid? revokedBy = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets license history for audit purposes.
    /// </summary>
    /// <param name="tenantId">Tenant ID for tenant-specific history, null for platform history</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Items per page</param>
    /// <param name="actionFilter">Optional action filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paginated license history</returns>
    Task<PagedResult<LicenseHistoryEntry>> GetLicenseHistoryAsync(
        Guid? tenantId = null,
        int page = 1,
        int pageSize = 20,
        string? actionFilter = null,
        CancellationToken cancellationToken = default);
}
```

### IFeatureService

Service for checking feature availability and limits.

```csharp
namespace MrWhoOidc.Auth.Licensing.Services;

public interface IFeatureService
{
    /// <summary>
    /// Checks if a feature is enabled for the current license.
    /// </summary>
    /// <param name="featureName">Feature flag name</param>
    /// <param name="tenantId">Tenant ID for tenant-specific license, null for platform license</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if feature is enabled</returns>
    Task<bool> IsFeatureEnabledAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets all enabled features for the current license.
    /// </summary>
    /// <param name="tenantId">Tenant ID for tenant-specific license, null for platform license</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Set of enabled feature names</returns>
    Task<IReadOnlySet<string>> GetEnabledFeaturesAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Records feature usage for analytics.
    /// </summary>
    /// <param name="featureName">Feature flag name</param>
    /// <param name="tenantId">Tenant ID for tenant-specific usage, null for platform usage</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task RecordFeatureUsageAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets feature usage metrics.
    /// </summary>
    /// <param name="tenantId">Tenant ID for tenant-specific metrics, null for platform metrics</param>
    /// <param name="featureName">Optional feature filter</param>
    /// <param name="fromDate">Start date for metrics</param>
    /// <param name="toDate">End date for metrics</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Feature usage metrics</returns>
    Task<IReadOnlyList<FeatureUsageMetric>> GetFeatureUsageAsync(
        Guid? tenantId = null,
        string? featureName = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);
}
```

### ILimitService

Service for checking and enforcing usage limits.

```csharp
namespace MrWhoOidc.Auth.Licensing.Services;

public interface ILimitService
{
    /// <summary>
    /// Gets the limit value for a specific limit type.
    /// </summary>
    /// <param name="limitType">Type of limit (users, tenants, etc.)</param>
    /// <param name="tenantId">Tenant ID for tenant-specific license, null for platform license</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Limit value (-1 for unlimited, 0 for disabled)</returns>
    Task<long> GetLimitAsync(string limitType, Guid? tenantId = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Checks if current usage is within the specified limit.
    /// </summary>
    /// <param name="limitType">Type of limit</param>
    /// <param name="currentUsage">Current usage count</param>
    /// <param name="tenantId">Tenant ID for tenant-specific license, null for platform license</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if within limits</returns>
    Task<bool> IsWithinLimitAsync(string limitType, long currentUsage, Guid? tenantId = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets current usage vs limits information.
    /// </summary>
    /// <param name="tenantId">Tenant ID for tenant-specific license, null for platform license</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Usage vs limits data</returns>
    Task<IReadOnlyList<UsageLimitInfo>> GetUsageLimitsAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Checks if adding the specified count would exceed limits.
    /// </summary>
    /// <param name="limitType">Type of limit</param>
    /// <param name="currentUsage">Current usage count</param>
    /// <param name="additionalCount">Additional count to check</param>
    /// <param name="tenantId">Tenant ID for tenant-specific license, null for platform license</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the addition would be within limits</returns>
    Task<bool> CanAddAsync(string limitType, long currentUsage, int additionalCount = 1, Guid? tenantId = null, CancellationToken cancellationToken = default);
}
```

### ILicenseValidator

Low-level license validation service.

```csharp
namespace MrWhoOidc.Auth.Licensing.Validators;

public interface ILicenseValidator
{
    /// <summary>
    /// Validates the cryptographic signature of a license key.
    /// </summary>
    /// <param name="licenseKey">License key in JWS format</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>License validation result</returns>
    Task<LicenseValidationResult> ValidateSignatureAsync(string licenseKey, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Parses license claims from a validated license key.
    /// </summary>
    /// <param name="licenseKey">License key in JWS format</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Parsed license information</returns>
    Task<LicenseInfo?> ParseLicenseAsync(string licenseKey, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Validates license business rules (expiration, tier validity, etc.).
    /// </summary>
    /// <param name="licenseInfo">Parsed license information</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>License validation result</returns>
    Task<LicenseValidationResult> ValidateBusinessRulesAsync(LicenseInfo licenseInfo, CancellationToken cancellationToken = default);
}
```

## Repository Interfaces

### ILicenseRepository

Repository for license persistence operations.

```csharp
namespace MrWhoOidc.Auth.Licensing.Repositories;

public interface ILicenseRepository
{
    /// <summary>
    /// Gets the current active license.
    /// </summary>
    /// <param name="tenantId">Tenant ID for tenant-specific license, null for platform license</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Active license or null</returns>
    Task<License?> GetActiveLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new license record.
    /// </summary>
    /// <param name="license">License entity to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created license</returns>
    Task<License> CreateLicenseAsync(License license, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Updates an existing license.
    /// </summary>
    /// <param name="license">License entity to update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated license</returns>
    Task<License> UpdateLicenseAsync(License license, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Deactivates the current active license.
    /// </summary>
    /// <param name="tenantId">Tenant ID for tenant-specific license, null for platform license</param>
    /// <param name="reason">Reason for deactivation</param>
    /// <param name="deactivatedBy">User ID who deactivated the license</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if license was deactivated</returns>
    Task<bool> DeactivateLicenseAsync(Guid? tenantId, string reason, Guid? deactivatedBy = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets license history entries.
    /// </summary>
    /// <param name="tenantId">Tenant ID for tenant-specific history, null for platform history</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Items per page</param>
    /// <param name="actionFilter">Optional action filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paginated license history</returns>
    Task<PagedResult<LicenseHistoryEntry>> GetLicenseHistoryAsync(
        Guid? tenantId = null,
        int page = 1,
        int pageSize = 20,
        string? actionFilter = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Adds a license history entry.
    /// </summary>
    /// <param name="historyEntry">History entry to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created history entry</returns>
    Task<LicenseHistoryEntry> AddHistoryEntryAsync(LicenseHistoryEntry historyEntry, CancellationToken cancellationToken = default);
}
```

### IFeatureUsageRepository

Repository for feature usage tracking.

```csharp
namespace MrWhoOidc.Auth.Licensing.Repositories;

public interface IFeatureUsageRepository
{
    /// <summary>
    /// Records feature usage.
    /// </summary>
    /// <param name="featureName">Feature name</param>
    /// <param name="tenantId">Tenant ID for tenant-specific usage, null for platform usage</param>
    /// <param name="licenseId">Associated license ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task RecordUsageAsync(string featureName, Guid? tenantId, Guid? licenseId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets feature usage metrics.
    /// </summary>
    /// <param name="tenantId">Tenant ID for tenant-specific metrics, null for platform metrics</param>
    /// <param name="featureName">Optional feature filter</param>
    /// <param name="fromDate">Start date for metrics</param>
    /// <param name="toDate">End date for metrics</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Feature usage metrics</returns>
    Task<IReadOnlyList<FeatureUsageMetric>> GetUsageMetricsAsync(
        Guid? tenantId = null,
        string? featureName = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets aggregated usage counts by feature.
    /// </summary>
    /// <param name="tenantId">Tenant ID for tenant-specific usage, null for platform usage</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of feature names to usage counts</returns>
    Task<IReadOnlyDictionary<string, long>> GetUsageCountsAsync(Guid? tenantId = null, CancellationToken cancellationToken = default);
}
```

## Helper Classes and DTOs

### PagedResult&lt;T&gt;

Generic paging result wrapper.

```csharp
namespace MrWhoOidc.Auth.Licensing.Models;

public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    public bool HasPreviousPage => Page > 1;
    public bool HasNextPage => Page < TotalPages;
    
    public PagedResult(IReadOnlyList<T> items, int totalCount, int page, int pageSize)
    {
        Items = items;
        TotalCount = totalCount;
        Page = page;
        PageSize = pageSize;
    }
}
```

### UsageLimitInfo

Usage vs limit information.

```csharp
namespace MrWhoOidc.Auth.Licensing.Models;

public sealed record UsageLimitInfo(
    string LimitType,
    long CurrentUsage,
    long LimitValue,
    double UtilizationPercentage,
    bool IsNearLimit,
    bool IsAtLimit
)
{
    public bool IsUnlimited => LimitValue == -1;
    public bool IsDisabled => LimitValue == 0;
    public long RemainingCapacity => IsUnlimited ? long.MaxValue : Math.Max(0, LimitValue - CurrentUsage);
}
```

## Service Implementation Guidelines

### Error Handling

All services should use consistent error handling patterns:

```csharp
public class LicenseException : Exception
{
    public string ErrorCode { get; }
    
    public LicenseException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
    
    public LicenseException(string errorCode, string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

public static class LicenseErrorCodes
{
    public const string InvalidSignature = "invalid_signature";
    public const string ExpiredLicense = "expired_license";
    public const string InvalidFormat = "invalid_format";
    public const string FeatureNotAvailable = "feature_not_available";
    public const string LimitExceeded = "limit_exceeded";
    public const string LicenseNotFound = "license_not_found";
}
```

### Logging

Services should use structured logging with consistent log levels:

```csharp
// Information: Normal operations
_logger.LogInformation("License installed successfully for tenant {TenantId}, tier {Tier}", tenantId, tier);

// Warning: Potential issues
_logger.LogWarning("License expires in {Days} days for tenant {TenantId}", daysUntilExpiry, tenantId);

// Error: Operation failures
_logger.LogError(ex, "Failed to validate license signature for tenant {TenantId}", tenantId);
```

### Caching

Services should implement appropriate caching strategies:

```csharp
// License information cached for 5 minutes
private const int LicenseCacheMinutes = 5;

// Feature flags cached per HTTP request
private const string FeatureCacheKey = "license_features_{0}";
```

### Multi-Tenancy

All services must respect tenant isolation:

```csharp
// Always validate tenant access
private async Task ValidateTenantAccessAsync(Guid? tenantId)
{
    if (tenantId.HasValue)
    {
        var currentTenant = _tenantAccessor.CurrentTenant;
        if (currentTenant?.Id != tenantId)
        {
            throw new UnauthorizedAccessException("Access denied to tenant license");
        }
    }
}
```

## Integration Points

### Dependency Injection Registration

```csharp
// In MrWhoOidc.Auth service registration
services.AddScoped<ILicenseService, LicenseService>();
services.AddScoped<IFeatureService, FeatureService>();
services.AddScoped<ILimitService, LimitService>();
services.AddScoped<ILicenseValidator, LicenseValidator>();
services.AddScoped<ILicenseRepository, LicenseRepository>();
services.AddScoped<IFeatureUsageRepository, FeatureUsageRepository>();

// Caching
services.AddMemoryCache();
services.AddSingleton<ILicenseCache, LicenseCacheService>();
```

### Configuration

```csharp
public class LicenseOptions
{
    public string PublicKeyPem { get; set; } = string.Empty;
    public int CacheExpirationMinutes { get; set; } = 5;
    public int GracePeriodDays { get; set; } = 7;
    public bool StrictValidation { get; set; } = true;
    public string DefaultTier { get; set; } = "community";
}
```

This contract definition provides a comprehensive foundation for implementing the license system while maintaining consistency with MrWhoOidc's architectural patterns and ensuring proper separation of concerns.
 
 