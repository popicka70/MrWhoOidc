using System.Diagnostics.Metrics;

namespace MrWhoOidc.WebAuth.Observability;

/// <summary>Metrics for consent-based user assistance requests and sessions.</summary>
public interface IUserAssistanceMetrics
{
    Counter<long> Requests { get; }
    Counter<long> Accepts { get; }
    Counter<long> Declines { get; }
    Counter<long> Cancels { get; }
    Counter<long> Ends { get; }
    Counter<long> Revocations { get; }
    Counter<long> Expirations { get; }
    Counter<long> ValidationFailures { get; }
    Histogram<double> SessionDuration { get; }
}

/// <summary>Metrics recorder for consent-based assistance on the shared MrWhoOidc.WebAuth meter.</summary>
public sealed class UserAssistanceMetrics : IUserAssistanceMetrics
{
    private static readonly Meter Meter = new("MrWhoOidc.WebAuth");

    public Counter<long> Requests { get; } = Meter.CreateCounter<long>("user_assistance.requests");
    public Counter<long> Accepts { get; } = Meter.CreateCounter<long>("user_assistance.accepts");
    public Counter<long> Declines { get; } = Meter.CreateCounter<long>("user_assistance.declines");
    public Counter<long> Cancels { get; } = Meter.CreateCounter<long>("user_assistance.cancels");
    public Counter<long> Ends { get; } = Meter.CreateCounter<long>("user_assistance.ends");
    public Counter<long> Revocations { get; } = Meter.CreateCounter<long>("user_assistance.revocations");
    public Counter<long> Expirations { get; } = Meter.CreateCounter<long>("user_assistance.expirations");
    public Counter<long> ValidationFailures { get; } = Meter.CreateCounter<long>("user_assistance.validation_failures");
    public Histogram<double> SessionDuration { get; } = Meter.CreateHistogram<double>("user_assistance.session_duration");
}
