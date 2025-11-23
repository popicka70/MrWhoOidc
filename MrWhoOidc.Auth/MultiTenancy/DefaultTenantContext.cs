using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.MultiTenancy;

public interface IDefaultTenantContext
{
    string DefaultTenantSlug { get; }

    Task<Guid?> GetDefaultTenantIdAsync(CancellationToken cancellationToken = default);
}

internal sealed class DefaultTenantContext : IDefaultTenantContext
{
    private readonly AuthDbContext _dbContext;
    private readonly IMultiTenancyOptions _options;
    private Guid? _cachedTenantId;
    private bool _hasCachedTenant;

    public DefaultTenantContext(AuthDbContext dbContext, IMultiTenancyOptions options)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string DefaultTenantSlug => _options.DefaultTenantSlug;

    public async Task<Guid?> GetDefaultTenantIdAsync(CancellationToken cancellationToken = default)
    {
        if (_hasCachedTenant)
        {
            return _cachedTenantId;
        }

        var slug = _options.DefaultTenantSlug;
        if (string.IsNullOrWhiteSpace(slug))
        {
            _hasCachedTenant = true;
            return null;
        }

        var tenantId = await _dbContext.Tenants
            .AsNoTracking()
            .Where(t => t.Slug == slug && t.Status == TenantStatus.Active)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        _cachedTenantId = tenantId;
        _hasCachedTenant = true;
        return _cachedTenantId;
    }
}
