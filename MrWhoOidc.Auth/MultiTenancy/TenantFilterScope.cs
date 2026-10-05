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
/// </summary>
public static class TenantFilterScope
{
    private static readonly AsyncLocal<int> Depth = new();

    /// <summary>True while a system scope is active on the current async flow.</summary>
    public static bool IsSystemScope => Depth.Value > 0;

    /// <summary>
    /// Opens a system scope in which the tenant query filter is bypassed. Dispose the result to close it.
    /// </summary>
    public static IDisposable BeginSystemScope()
    {
        Depth.Value = Depth.Value + 1;
        return new Handle();
    }

    private sealed class Handle : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && Depth.Value > 0)
            {
                Depth.Value = Depth.Value - 1;
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
