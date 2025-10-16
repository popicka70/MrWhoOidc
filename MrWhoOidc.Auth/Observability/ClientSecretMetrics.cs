using System.Diagnostics.Metrics;

namespace MrWhoOidc.Auth.Observability;

/// <summary>
/// Metrics for client secret operations including authentication, rotation, and expiry monitoring.
/// </summary>
public interface IClientSecretMetrics
{
    /// <summary>
    /// Total count of successful client secret authentications.
    /// Tags: client_id, is_primary
    /// </summary>
    Counter<long> AuthenticationSuccess { get; }
    
    /// <summary>
    /// Total count of failed client secret authentications.
    /// Tags: client_id, reason (expired|revoked|invalid|missing)
    /// </summary>
    Counter<long> AuthenticationFailure { get; }
    
    /// <summary>
    /// Total count of secret rotation events (create, activate, revoke).
    /// Tags: action (created|activated|revoked|set-primary)
    /// </summary>
    Counter<long> RotationEvents { get; }
    
    /// <summary>
    /// Observable gauge of active secrets per client.
    /// Requires periodic update via SetActiveSecretsCount.
    /// </summary>
    ObservableGauge<int> ActiveSecretsCount { get; }
    
    /// <summary>
    /// Update the active secrets count for metrics collection.
    /// Called by background monitoring service.
    /// </summary>
    /// <param name="count">Total number of active secrets across all clients</param>
    void SetActiveSecretsCount(int count);
    
    /// <summary>
    /// Observable gauge of days until secret expiry.
    /// Lower values indicate urgency.
    /// </summary>
    ObservableGauge<double> DaysUntilExpiry { get; }
    
    /// <summary>
    /// Update the days until expiry metric for secrets expiring soon.
    /// Called by background monitoring service.
    /// </summary>
    /// <param name="minDays">Minimum days until expiry across all active secrets</param>
    void SetDaysUntilExpiry(double minDays);
}

public sealed class ClientSecretMetrics : IClientSecretMetrics
{
    public const string MeterName = "MrWhoOidc.Auth.ClientSecrets";
    private static readonly Meter Meter = new(MeterName);
    
    private int _activeSecretsCount;
    private double _minDaysUntilExpiry = double.MaxValue;

    public Counter<long> AuthenticationSuccess { get; } = 
        Meter.CreateCounter<long>(
            "oidc.client_secrets.auth.success",
            description: "Successful client secret authentications");

    public Counter<long> AuthenticationFailure { get; } = 
        Meter.CreateCounter<long>(
            "oidc.client_secrets.auth.failure",
            description: "Failed client secret authentications");

    public Counter<long> RotationEvents { get; } = 
        Meter.CreateCounter<long>(
            "oidc.client_secrets.rotation",
            description: "Client secret rotation events (create, activate, revoke, set-primary)");

    public ObservableGauge<int> ActiveSecretsCount { get; }
    
    public ObservableGauge<double> DaysUntilExpiry { get; }

    public ClientSecretMetrics()
    {
        ActiveSecretsCount = Meter.CreateObservableGauge(
            "oidc.client_secrets.active.count",
            () => _activeSecretsCount,
            description: "Total number of active client secrets");
        
        DaysUntilExpiry = Meter.CreateObservableGauge(
            "oidc.client_secrets.expiry.days_remaining",
            () => _minDaysUntilExpiry,
            description: "Minimum days until any active secret expires");
    }

    public void SetActiveSecretsCount(int count)
    {
        _activeSecretsCount = count;
    }

    public void SetDaysUntilExpiry(double minDays)
    {
        _minDaysUntilExpiry = minDays;
    }
}
