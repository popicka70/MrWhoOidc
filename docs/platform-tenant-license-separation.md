# Platform and Tenant License Separation

This document describes the licensing model that separates platform licenses from tenant licenses, enabling flexible licensing scenarios for single-tenant and multi-tenant deployments.

## Overview

The licensing system supports two distinct deployment modes:

1. **Single-Tenant Mode**: One tenant, platform license applies directly
2. **Multi-Tenant Mode**: Multiple tenants with platform license defining maximum capabilities

In multi-tenant mode, tenants can either:
- **Inherit** the platform license (projected to tenant scope)
- Have their own **sublicense** (validated against platform license)

## Key Concepts

### Deployment Mode

The `DeploymentMode` enum indicates the platform's deployment configuration:

```csharp
public enum DeploymentMode
{
    SingleTenant = 0,  // Single-tenant deployment
    MultiTenant = 1    // Multi-tenant deployment
}
```

Platform licenses include a `deployment_mode` claim that specifies this setting.

### Tenant License Mode

The `TenantLicenseMode` enum determines how a tenant receives its license:

```csharp
public enum TenantLicenseMode
{
    InheritPlatform = 0,  // Tenant uses projected platform license
    Sublicense = 1        // Tenant has its own sublicense
}
```

This is configured per-tenant in the admin UI under **Platform Admin > Tenants > Edit > Licensing**.

### License Info Properties

The `LicenseInfo` record includes:

| Property | Description |
|----------|-------------|
| `DeploymentMode` | Platform deployment mode (single/multi-tenant) |
| `LicenseId` | Unique identifier (JWT `jti` claim) |
| `ParentLicenseId` | For sublicenses, references the platform license `jti` |
| `IsSublicense` | Boolean indicating if this is a sublicense |

## License Types

### Platform License

- Scope: `platform`
- Contains all available features and limits
- Generated using **KeyGen > Generate Platform License**
- Can specify:
  - Deployment mode (single-tenant or multi-tenant)
  - Default tenant feature overrides (for inherited licenses)
  - Platform-only features (e.g., `multi_tenant`)

### Tenant Sublicense

- Scope: `tenant`
- Must reference a platform license via `parent_license_jti`
- Cannot exceed platform license:
  - Features must be subset of platform features
  - Limits cannot exceed platform limits
  - Expiry cannot exceed platform expiry
- Generated using **KeyGen > Generate Tenant License**
- Requires parent license ID (JTI)

## Validation Rules

### Sublicense Validation

When a tenant is in `Sublicense` mode, the system validates:

1. **Scope Check**: Sublicense must have `tenant` scope
2. **Expiry Check**: Cannot exceed platform license expiry
3. **Feature Check**: All features must exist in platform license
4. **Limit Check**: No limit can exceed platform limit
   - If platform has unlimited (-1), sublicense can have any value
   - If platform has a specific limit, sublicense cannot exceed it
   - Sublicense cannot be unlimited if platform is limited

### Error Codes

| Error Code | Description |
|------------|-------------|
| `invalid_sublicense_scope` | Sublicense is not tenant-scoped |
| `invalid_platform_scope` | Platform license is not platform-scoped |
| `sublicense_expiry_exceeds_platform` | Expiry date exceeds platform |
| `sublicense_features_exceed_platform` | Features not in platform license |
| `sublicense_limit_exceeds_platform` | Limit exceeds platform limit |

## Effective License Resolution

The `GetEffectiveLicenseAsync` method resolves the effective license for a tenant:

```
GetEffectiveLicenseAsync(tenantId)
│
├─ tenantId is null? → Return platform license
│
├─ Single-tenant mode? → Return platform license
│
├─ Tenant mode = InheritPlatform?
│   └─ Project platform license to tenant scope
│      (excludes platform-only features)
│
└─ Tenant mode = Sublicense?
    ├─ Get tenant's sublicense
    ├─ Validate against platform license
    └─ Return validated sublicense (or null if invalid)
```

## KeyGen Tool Usage

### Generate Platform License

1. Navigate to **KeyGen > Generate Platform License**
2. Select deployment mode:
   - **Single-Tenant**: For single-organization deployments
   - **Multi-Tenant**: For SaaS/multi-org deployments
3. Select features (including platform-only features like `multi_tenant`)
4. For multi-tenant, optionally set default tenant feature overrides
5. Generate and install via WebAuth admin

### Generate Tenant License

1. Navigate to **KeyGen > Generate Tenant License**
2. Enter the **Parent License ID (JTI)** from the platform license
3. Enter the **Tenant ID** the license is for
4. Select features (platform-only features are filtered out)
5. Set limits and expiry (cannot exceed platform)
6. Generate and install via tenant admin

## Admin UI Configuration

### Platform Admin: Tenant Edit

In **Platform Admin > Tenants > Edit**, admins can configure:

- **Inherit Platform License**: Tenant uses projected platform capabilities
- **Own Sublicense**: Tenant requires its own installed sublicense

### License Display

The **Admin > License** page shows:

- Deployment mode (Single-Tenant / Multi-Tenant)
- For sublicenses: Parent license ID and sublicense indicator

## Migration

If upgrading from a previous version:

1. Run the EF migration to add `LicenseMode` column to tenants:
   ```powershell
   dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth
   ```
2. Existing tenants default to `InheritPlatform` mode
3. Existing platform licenses default to `MultiTenant` deployment mode

## API Reference

### ITenantLicenseModeProvider

```csharp
public interface ITenantLicenseModeProvider
{
    Task<TenantLicenseMode> GetLicenseModeAsync(
        Guid tenantId, 
        CancellationToken cancellationToken = default);
}
```

### ILicenseValidator.ValidateSublicenseAsync

```csharp
Task<LicenseValidationResult> ValidateSublicenseAsync(
    LicenseInfo sublicense,
    LicenseInfo platformLicense,
    CancellationToken cancellationToken = default);
```

### ILicenseService.GetEffectiveLicenseAsync

```csharp
Task<LicenseInfo?> GetEffectiveLicenseAsync(
    Guid? tenantId = null,
    CancellationToken cancellationToken = default);
```

## Best Practices

1. **Platform license first**: Always install platform license before tenant sublicenses
2. **Validate before install**: Use KeyGen validation before generating sublicenses
3. **Monitor expiry**: Sublicenses tied to platform expiry—renew platform first
4. **Feature planning**: Plan tenant features within platform capabilities
5. **Limit allocation**: Distribute platform limits across tenants appropriately

## Related Documentation

- [Licensing Overview](./licensing-overview.md)
- [KeyGen Tool Guide](./key-license-generator-deployment.md)
- [Multi-Tenancy Guide](./multitenancy-quick-reference.md)
