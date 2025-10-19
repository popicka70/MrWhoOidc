# Research Phase: License Key System for MrWhoOidc

## Overview

This document contains research findings for implementing a license key system in MrWhoOidc. The research addresses technical decisions around cryptographic approaches, license key formats, feature gating patterns, and integration with the existing architecture.

## Research Tasks Completed

### 1. Cryptographic License Signing Approach

**Decision**: Use ECDSA P-256 with JSON Web Signature (JWS) format

**Rationale**:

- **ECDSA P-256**: Provides strong security with smaller key/signature sizes compared to RSA
- **JWS Format**: Leverages existing JWT infrastructure in MrWhoOidc, ensures standard compliance
- **Offline Validation**: No network dependency, signatures can be verified with public key only
- **Tamper Resistance**: Cryptographic signature prevents modification of license data

**Alternatives Considered**:

- **RSA 2048/4096**: Larger signatures, slower performance, but widely supported
- **Ed25519**: Excellent performance but less ecosystem support in .NET
- **Symmetric HMAC**: Requires shared secret, less secure for offline validation
- **Custom binary format**: More complex, less standard than JWS

**Implementation Details**:

- Use `System.IdentityModel.Tokens.Jwt` for JWS handling
- Generate ECDSA P-256 key pair for license signing
- License data stored as JWT claims with custom claim names
- Public key embedded in application for signature verification

### 2. License Key Format and Structure

**Decision**: Base64-encoded JWS with embedded license metadata

**Format**:
```text
License Key: eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c
```

**Claims Structure**:
```json
{
  "iss": "MrWhoOidc-License-Authority",
  "sub": "customer-id-or-organization",
  "iat": 1729353600,
  "exp": 1760889600,
  "tier": "enterprise",
  "features": ["multi-tenancy", "jar", "jarm", "totp", "dpop", "obo"],
  "limits": {
    "users": -1,
    "tenants": -1
  },
  "organization": "Customer Organization Name"
}
```

**Benefits**:

- Human-readable when decoded (for support/debugging)
- Standard JWT format ensures proper parsing/validation
- Extensible for future license attributes
- Compact representation for easy distribution

### 3. Feature Gating Architecture

**Decision**: Service-based feature gating with dependency injection

**Architecture**:

```csharp
// Core service interface
public interface ILicenseService
{
    LicenseInfo GetCurrentLicense();
    bool IsFeatureEnabled(string featureName);
    bool IsWithinLimits(LimitType limitType, int currentUsage);
    Task<LicenseValidationResult> ValidateLicenseAsync(string licenseKey);
}

// Feature-specific services
public interface IMultiTenancyLicenseService
{
    bool CanCreateTenant();
    int GetMaxTenants();
    bool IsMultiTenancyEnabled();
}

// Usage in existing services
public class TenantService
{
    private readonly IMultiTenancyLicenseService _licenseService;
    
    public async Task<CreateTenantResult> CreateTenantAsync(...)
    {
        if (!_licenseService.CanCreateTenant())
        {
            return CreateTenantResult.LicenseLimitExceeded();
        }
        // ... existing logic
    }
}
```

**Benefits**:

- Clean separation of concerns
- Easy to test with mocked license services
- Minimal impact on existing codebase
- Consistent pattern across all licensed features

### 4. Database Schema Design

**Decision**: Add licensing tables to existing AuthDbContext

**Schema**:

```sql
-- Store current active license
CREATE TABLE Licenses (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    TenantId UUID NULL, -- NULL for platform-wide license
    LicenseKey TEXT NOT NULL,
    Tier VARCHAR(50) NOT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT true,
    ValidFrom TIMESTAMPTZ NOT NULL,
    ValidUntil TIMESTAMPTZ NOT NULL,
    OrganizationName VARCHAR(500),
    CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CreatedBy UUID,
    
    CONSTRAINT FK_Licenses_TenantId FOREIGN KEY (TenantId) REFERENCES Tenants(Id)
);

-- Audit trail for license changes
CREATE TABLE LicenseHistory (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    LicenseId UUID NOT NULL,
    Action VARCHAR(50) NOT NULL, -- 'installed', 'updated', 'expired', 'revoked'
    OldLicenseKey TEXT,
    NewLicenseKey TEXT,
    ChangedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ChangedBy UUID,
    Reason TEXT,
    
    CONSTRAINT FK_LicenseHistory_LicenseId FOREIGN KEY (LicenseId) REFERENCES Licenses(Id)
);

-- Track feature usage for analytics
CREATE TABLE FeatureUsage (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    TenantId UUID NULL,
    FeatureName VARCHAR(100) NOT NULL,
    UsageCount BIGINT NOT NULL DEFAULT 1,
    LastUsed TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    
    CONSTRAINT FK_FeatureUsage_TenantId FOREIGN KEY (TenantId) REFERENCES Tenants(Id),
    UNIQUE(TenantId, FeatureName)
);
```

**Benefits**:

- Integrates with existing multi-tenancy model
- Supports both platform-wide and tenant-specific licenses
- Comprehensive audit trail for compliance
- Usage tracking for product analytics

### 5. License Validation Strategy

**Decision**: Cached validation with periodic refresh

**Strategy**:

- **Startup Validation**: Full license validation on application startup
- **Runtime Caching**: Cache license status for 5 minutes to optimize performance
- **Periodic Refresh**: Background service validates license every hour
- **Graceful Degradation**: 7-day grace period after license expiration
- **Failure Handling**: Log errors but don't crash on validation failures

**Implementation**:

```csharp
public class LicenseValidationService : ILicenseValidationService
{
    private readonly IMemoryCache _cache;
    private readonly ILicenseRepository _repository;
    private readonly ILogger<LicenseValidationService> _logger;
    
    public async Task<LicenseInfo> GetValidatedLicenseAsync()
    {
        var cacheKey = "current_license";
        
        if (_cache.TryGetValue(cacheKey, out LicenseInfo cachedLicense))
        {
            return cachedLicense;
        }
        
        var license = await ValidateCurrentLicenseAsync();
        
        _cache.Set(cacheKey, license, TimeSpan.FromMinutes(5));
        
        return license;
    }
}
```

### 6. Admin UI Integration Approach

**Decision**: Extend existing admin interface with license management section

**Integration Points**:

- **Navigation**: Add "License" section to existing admin menu
- **Dashboard**: License status widget on main admin dashboard
- **Pages**: Dedicated license management pages following existing patterns
- **APIs**: RESTful endpoints following existing `/admin/api/*` convention

**UI Components**:

- License status dashboard with current tier, expiration, usage
- License key input form with validation
- Feature matrix showing enabled/disabled features
- Usage analytics charts
- License history audit log

### 7. Testing Strategy

**Decision**: Comprehensive test coverage following MrWhoOidc patterns

**Test Categories**:

1. **Unit Tests**:
   - License validation logic
   - Feature gating services
   - Cryptographic signature verification
   - License parsing and claims extraction

2. **Integration Tests**:
   - Database operations for license storage
   - Admin API endpoints
   - License validation middleware
   - Multi-tenant license isolation

3. **End-to-End Tests**:
   - Complete license installation workflow
   - Feature enablement/disablement
   - License expiration handling
   - Upgrade/downgrade scenarios

**Test Data**:

- Valid/invalid license keys for each tier
- Expired licenses for grace period testing
- Tampered licenses for security testing
- Edge cases for limit enforcement

## Technology Stack Decisions

### Core Dependencies

- **System.IdentityModel.Tokens.Jwt**: JWT/JWS handling
- **System.Security.Cryptography**: ECDSA key operations
- **Microsoft.Extensions.Caching.Memory**: License caching
- **EF Core**: Database persistence (existing)

### Integration Points

- **AuthDbContext**: Extend with license entities
- **ITenantAccessor**: Respect existing tenant isolation
- **Admin UI**: Extend existing Razor Pages pattern
- **Service Registration**: Use existing DI container setup

## Security Considerations

### License Key Protection

- Private signing key stored securely (Azure Key Vault, HSM)
- Public key embedded in application binary
- License keys transmitted over HTTPS only
- No sensitive license data in client-side JavaScript

### Bypass Prevention

- License validation cannot be disabled via configuration
- Feature checks integrated deep in service layer
- Critical validations performed server-side only
- Logging and monitoring for license manipulation attempts

### Multi-Tenant Security

- License limits enforced per tenant
- No cross-tenant license information leakage
- Tenant admin can only view their own license
- Platform admin can manage all licenses

## Performance Considerations

### Optimization Strategies

- **Caching**: 5-minute cache for license status
- **Lazy Loading**: Feature checks only when needed
- **Batch Operations**: Bulk limit checking where possible
- **Async Operations**: Non-blocking license validation

### Monitoring

- License validation performance metrics
- Feature usage analytics
- Cache hit/miss ratios
- License expiration alerts

## Implementation Phases

### Phase 1: Core Infrastructure

- License entities and database schema
- Basic license validation service
- Cryptographic signing/verification
- Unit tests for core functionality

### Phase 2: Feature Integration

- Feature gating services
- Integration with existing services
- User/tenant limit enforcement
- Integration tests

### Phase 3: Admin Interface

- License management UI
- Admin API endpoints
- License status dashboard
- End-to-end tests

### Phase 4: Advanced Features

- License usage analytics
- Automated alerts and notifications
- Advanced reporting
- Performance optimization

## Conclusion

The research phase has established a comprehensive approach for implementing the license key system in MrWhoOidc. The chosen solutions balance security, performance, and maintainability while integrating seamlessly with the existing architecture. The cryptographic approach using ECDSA and JWS provides strong security with good performance, while the service-based feature gating ensures clean integration with existing code.

The proposed implementation follows MrWhoOidc's constitutional principles, maintains the domain-driven architecture, and provides a solid foundation for future license management enhancements.
