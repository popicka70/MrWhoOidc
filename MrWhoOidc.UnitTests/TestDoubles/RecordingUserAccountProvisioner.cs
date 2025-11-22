using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.TestDoubles;

/// <summary>
/// Test double that records every EnsureAsync call and optionally runs custom hooks.
/// </summary>
internal sealed class RecordingUserAccountProvisioner : IUserAccountProvisioner
{
    private readonly List<EnsureCall> _calls = new();
    private readonly object _lock = new();

    /// <summary>
    /// When set, EnsureAsync throws InvalidOperationException to help failure-path tests.
    /// </summary>
    public bool ThrowOnEnsure { get; set; }

    /// <summary>
    /// Optional callback executed for each EnsureAsync invocation.
    /// </summary>
    public Func<EnsureCall, Task>? OnEnsureAsync { get; set; }

    /// <summary>
    /// Snapshot of recorded EnsureAsync calls.
    /// </summary>
    public IReadOnlyList<EnsureCall> Calls
    {
        get
        {
            lock (_lock)
            {
                return _calls.ToArray();
            }
        }
    }

    public Task EnsureAsync(User user, Guid tenantId, Guid? defaultRealmId, bool isTenantAdmin, CancellationToken ct = default, bool autoSave = true)
    {
        if (user is null)
        {
            throw new ArgumentNullException(nameof(user));
        }

        var call = new EnsureCall(user, tenantId, defaultRealmId, isTenantAdmin);
        lock (_lock)
        {
            _calls.Add(call);
        }

        if (ThrowOnEnsure)
        {
            throw new InvalidOperationException("RecordingUserAccountProvisioner configured to throw.");
        }

        return OnEnsureAsync is not null ? OnEnsureAsync(call) : Task.CompletedTask;
    }

    /// <summary>
    /// Clears recorded calls so tests can assert per-scenario state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _calls.Clear();
        }
    }

    internal sealed record EnsureCall(User User, Guid TenantId, Guid? DefaultRealmId, bool IsTenantAdmin);
}
