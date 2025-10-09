using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.UnitTests.Helpers;

/// <summary>
/// Mock implementation of ITenantAccessor for unit tests.
/// </summary>
public class MockTenantAccessor : ITenantAccessor
{
    private TenantContext? _currentTenant;

    /// <summary>
    /// Gets or sets the current tenant context. Returns null by default (single-tenant mode).
    /// </summary>
    public TenantContext? CurrentTenant
    {
        get => _currentTenant;
        set => _currentTenant = value;
    }

    /// <summary>
    /// Sets the current tenant context (implements interface).
    /// </summary>
    public void SetTenant(TenantContext context)
    {
        _currentTenant = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Creates a mock accessor that returns null (single-tenant mode behavior).
    /// </summary>
    public static MockTenantAccessor CreateSingleTenantMode()
    {
        return new MockTenantAccessor { CurrentTenant = null };
    }

    /// <summary>
    /// Creates a mock accessor with a specific tenant context.
    /// </summary>
    public static MockTenantAccessor CreateWithTenant(
        Guid tenantId,
        string slug,
        string name = "Test Tenant",
        string issuerUri = "https://localhost:5001",
        bool isMultiTenantMode = true)
    {
        return new MockTenantAccessor
        {
            CurrentTenant = new TenantContext
            {
                TenantId = tenantId,
                Slug = slug,
                Name = name,
                IssuerUri = issuerUri,
                IsMultiTenantMode = isMultiTenantMode
            }
        };
    }

    /// <summary>
    /// Creates a mock accessor with the default tenant.
    /// </summary>
    public static MockTenantAccessor CreateWithDefaultTenant()
    {
        return CreateWithTenant(
            tenantId: new Guid("00000000-0000-0000-0000-000000000001"),
            slug: "default",
            name: "Default Tenant",
            issuerUri: "https://localhost:5001",
            isMultiTenantMode: false
        );
    }
}
