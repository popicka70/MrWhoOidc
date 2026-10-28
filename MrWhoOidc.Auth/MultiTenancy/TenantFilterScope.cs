namespace MrWhoOidc.Auth.MultiTenancy;

/// <summary>
/// Explicit opt-in to cross-tenant ("system") data access for <c>AuthDbContext</c>.
/// <para>
/// The EF Core tenant query filter fails CLOSED: when no tenant is set on <see cref="ITenantAccessor"/>,
/// tenant-scoped rows are invisible (optional-tenant entities only show their platform-wide rows,
/// <c>TenantId == null</c>). Code that legitimately needs to see every tenant's data without a tenant
/// context (startup seeding, platform administration, cross-tenant maintenance jobs, account-level
/// flows that start from a global <c>UserAccount</c>) must say so explicitly, either with
/// <c>IgnoreQueryFilters()</c> on the individual query or by wrapping the work in
/// <c>using (TenantFilterScope.BeginSystemScope()) { ... }</c>.
/// </para>
/// <para>
/// The scope is carried by an <see cref="AsyncLocal{T}"/>: it flows into awaited calls and child tasks
/// started inside it and ends when the returned handle is disposed (or when the async method that opened
/// it returns). It bypasses the tenant filter entirely, even when a tenant is set.
/// </para>
/// <para>
/// A system scope only lifts the READ filter. The tenant WRITE guard
/// (<c>AuthDbContext.ApplyTenantWriteGuards</c>) keeps rejecting writes aimed at another tenant, so a
/// system scope alone can never write cross-tenant. Code that is explicitly authorized to write for
/// another tenant (the platform-admin configuration import) must additionally open
/// <see cref="BeginCrossTenantScope"/>.
/// </para>
/// </summary>
public static class TenantFilterScope
{
    private static readonly AsyncLocal<int> Depth = new();
    private static readonly AsyncLocal<int> CrossTenantWriteDepth = new();

    /// <summary>True while a system scope is active on the current async flow.</summary>
    public static bool IsSystemScope => Depth.Value > 0;

    /// <summary>
    /// True while a cross-tenant scope that also lifts the tenant write guard is active on the current
    /// async flow. Only platform-admin-authorized cross-tenant operations may open such a scope.
    /// </summary>
    public static bool AllowsCrossTenantWrites => CrossTenantWriteDepth.Value > 0;

    /// <summary>
    /// Opens a system scope in which the tenant query filter is bypassed. Dispose the result to close it.
    /// The tenant write guard stays active.
    /// </summary>
    public static IDisposable BeginSystemScope()
    {
        Depth.Value = Depth.Value + 1;
        return new Handle(crossTenantWrites: false);
    }

    /// <summary>
    /// Opens a system scope that ALSO lifts the tenant write guard, so the guarded save path may write rows
    /// that belong to a tenant other than the one currently set on <see cref="ITenantAccessor"/>.
    /// <para>
    /// This is the explicitly scoped cross-tenant read/write path: it must only be used by operations that
    /// are authorized to act for another tenant (platform-admin configuration export/import). It is
    /// deliberately a separate call from <see cref="BeginSystemScope"/> so that an ordinary cross-tenant
    /// READ never silently gains cross-tenant WRITE.
    /// </para>
    /// </summary>
    public static IDisposable BeginCrossTenantScope()
    {
        Depth.Value = Depth.Value + 1;
        CrossTenantWriteDepth.Value = CrossTenantWriteDepth.Value + 1;
        return new Handle(crossTenantWrites: true);
    }

    private sealed class Handle : IDisposable
    {
        private readonly bool _crossTenantWrites;
        private int _disposed;

        public Handle(bool crossTenantWrites) => _crossTenantWrites = crossTenantWrites;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (Depth.Value > 0)
            {
                Depth.Value = Depth.Value - 1;
            }

            if (_crossTenantWrites && CrossTenantWriteDepth.Value > 0)
            {
                CrossTenantWriteDepth.Value = CrossTenantWriteDepth.Value - 1;
            }
        }
    }
}

/// <summary>
/// Operator settings for the tenant query filter (<c>MultiTenancy:TenantFilterFailOpen</c>).
/// </summary>
public sealed class TenantFilterOptions
{
    public const string ConfigurationKey = "MultiTenancy:TenantFilterFailOpen";

    /// <summary>
    /// EMERGENCY ESCAPE HATCH. When true, restores the legacy behaviour in which a query issued without a
    /// tenant context sees every tenant's rows. Default false (fail closed). A warning is logged at startup
    /// while enabled. Only use it to unblock an outage caused by a code path that lacks an explicit system
    /// scope, and report that path so it can be fixed.
    /// </summary>
    public bool FailOpen { get; set; }
}
