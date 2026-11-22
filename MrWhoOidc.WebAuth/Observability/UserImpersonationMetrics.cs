using System.Diagnostics.Metrics;

namespace MrWhoOidc.WebAuth.Observability;

/// <summary>Metrics for real user impersonation sessions.</summary>
public interface IUserImpersonationMetrics
{
    Counter<long> Starts { get; }
    Counter<long> Stops { get; }
    Counter<long> Expirations { get; }
    Counter<long> Revocations { get; }
    Counter<long> ValidationFailures { get; }
    Histogram<double> SessionDuration { get; }
}

/// <summary>Metrics recorder for user impersonation on the shared MrWhoOidc.WebAuth meter.</summary>
public sealed class UserImpersonationMetrics : IUserImpersonationMetrics
{
    private static readonly Meter Meter = new("MrWhoOidc.WebAuth");

    public Counter<long> Starts { get; } = Meter.CreateCounter<long>("user_impersonation.starts");
    public Counter<long> Stops { get; } = Meter.CreateCounter<long>("user_impersonation.stops");
    public Counter<long> Expirations { get; } = Meter.CreateCounter<long>("user_impersonation.expirations");
    public Counter<long> Revocations { get; } = Meter.CreateCounter<long>("user_impersonation.revocations");
    public Counter<long> ValidationFailures { get; } = Meter.CreateCounter<long>("user_impersonation.validation_failures");
    public Histogram<double> SessionDuration { get; } = Meter.CreateHistogram<double>("user_impersonation.session_duration");
}
