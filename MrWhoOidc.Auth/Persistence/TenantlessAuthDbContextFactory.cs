using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// H5: a factory-owned <see cref="AuthDbContext"/> must never resolve the request's scoped
/// <see cref="ITenantAccessor"/> from the root provider.
/// <para>
/// <c>AddDbContextFactory&lt;AuthDbContext&gt;()</c> activates the context against the root service provider.
/// With scope validation on (Development) that throws for the scoped accessor, so optional client/provider
/// JWKS cache misses and the backchannel alert sampler failed to activate; with scope validation off
/// (Production) every factory-created context silently shared one root-scoped accessor, so a single
/// <c>SetTenant</c> call would re-scope every factory context at once.
/// </para>
/// <para>
/// This factory owns its accessor explicitly: every context gets its own <see cref="TenantAccessor"/> with no
/// tenant set. Such a context fails CLOSED (tenant-scoped rows are invisible; optional-tenant entities expose
/// only their platform-wide rows), which is the correct default for a context that is not bound to a request.
/// Cross-tenant work must then say so explicitly - <c>IgnoreQueryFilters()</c> on the individual query or
/// <see cref="TenantFilterScope.BeginSystemScope"/> - exactly like every other tenantless code path.
/// </para>
/// </summary>
public sealed class TenantlessAuthDbContextFactory(
    DbContextOptions<AuthDbContext> options,
    ISecretProtector? secretProtector,
    TenantFilterOptions? tenantFilterOptions) : IDbContextFactory<AuthDbContext>
{
    /// <summary>
    /// Creates a context that carries its own, tenantless accessor.
    /// </summary>
    public AuthDbContext CreateDbContext()
        => new(options, new TenantAccessor(), secretProtector, tenantFilterOptions);

    /// <inheritdoc />
    public Task<AuthDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
