using System.Diagnostics.Metrics;

namespace MrWhoOidc.Auth.Observability;

/// <summary>
/// Metrics for global authentication operations.
/// </summary>
public sealed class GlobalAuthMetrics
{
    public const string MeterName = "MrWhoOidc.Auth.GlobalAuth";
    private static readonly Meter Meter = new(MeterName);

    private readonly Counter<long> _globalAuthSuccess = Meter.CreateCounter<long>(
        "oidc.global_auth.success",
        description: "Successful global credential authentications");

    private readonly Counter<long> _globalAuthFailure = Meter.CreateCounter<long>(
        "oidc.global_auth.failure",
        description: "Failed global credential authentications");

    private readonly Counter<long> _globalAccountLockout = Meter.CreateCounter<long>(
        "oidc.global_auth.lockout",
        description: "Account lockouts due to failed attempts");

    private readonly Counter<long> _adminPasswordReset = Meter.CreateCounter<long>(
        "oidc.admin.password_reset",
        description: "Admin password reset operations on global UserAccount");

    /// <summary>
    /// Records a successful global authentication.
    /// </summary>
    public void GlobalAuthSuccess()
    {
        _globalAuthSuccess.Add(1);
    }

    /// <summary>
    /// Records a failed global authentication with the specified reason.
    /// </summary>
    /// <param name="reason">The reason for the failure (e.g., "user_not_found", "invalid_password", "account_locked")</param>
    public void GlobalAuthFailure(string reason)
    {
        _globalAuthFailure.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    /// <summary>
    /// Records an account lockout event.
    /// </summary>
    public void GlobalAccountLockout()
    {
        _globalAccountLockout.Add(1);
    }

    /// <summary>
    /// Records an admin password reset operation.
    /// </summary>
    /// <param name="affectedTenantCount">Number of tenants the user has membership in</param>
    public void AdminPasswordReset(int affectedTenantCount)
    {
        _adminPasswordReset.Add(1, new KeyValuePair<string, object?>("affected_tenant_count", affectedTenantCount));
    }
}
