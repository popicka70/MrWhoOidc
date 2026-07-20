using System.Diagnostics.Metrics;

namespace MrWhoOidc.WebAuth.Observability;

/// <summary>
/// Tenant Support Access metrics interface.
/// Records observability instruments for support session lifecycle events.
/// </summary>
public interface ITenantSupportAccessMetrics
{
    Counter<long> TenantSupportAccessStarts { get; }
    Counter<long> TenantSupportAccessStops { get; }
    Counter<long> TenantSupportAccessExpirations { get; }
    Counter<long> TenantSupportAccessRevocations { get; }
    Counter<long> TenantSupportAccessWriteDenials { get; }
    Counter<long> TenantSupportAccessValidationFailures { get; }
    Histogram<double> TenantSupportAccessSessionDuration { get; }
}

/// <summary>
/// Concrete metrics recorder for Tenant Support Access.
/// All instruments are registered on the shared "MrWhoOidc.WebAuth" meter.
/// </summary>
public sealed class TenantSupportAccessMetrics : ITenantSupportAccessMetrics
{
    private static readonly Meter Meter = new("MrWhoOidc.WebAuth");

    public Counter<long> TenantSupportAccessStarts { get; } = Meter.CreateCounter<long>("tenant_support_access.starts");
    public Counter<long> TenantSupportAccessStops { get; } = Meter.CreateCounter<long>("tenant_support_access.stops");
    public Counter<long> TenantSupportAccessExpirations { get; } = Meter.CreateCounter<long>("tenant_support_access.expirations");
    public Counter<long> TenantSupportAccessRevocations { get; } = Meter.CreateCounter<long>("tenant_support_access.revocations");
    public Counter<long> TenantSupportAccessWriteDenials { get; } = Meter.CreateCounter<long>("tenant_support_access.write_denials");
    public Counter<long> TenantSupportAccessValidationFailures { get; } = Meter.CreateCounter<long>("tenant_support_access.validation_failures");
    public Histogram<double> TenantSupportAccessSessionDuration { get; } = Meter.CreateHistogram<double>("tenant_support_access.session_duration");
}

/// <summary>
/// No-op metrics implementation for Tenant Support Access (used in test mode).
/// All instruments are no-ops that do not affect production behavior.
/// </summary>
internal sealed class NoopTenantSupportAccessMetrics : ITenantSupportAccessMetrics
{
    private static readonly Meter Meter = new("MrWhoOidc.WebAuth.noop");
    private static Counter<long> C(string name) => Meter.CreateCounter<long>(name + ".noop");
    private static Histogram<double> H(string name) => Meter.CreateHistogram<double>(name + ".noop");
    public Counter<long> TenantSupportAccessStarts { get; } = C("tenant_support_access.starts");
    public Counter<long> TenantSupportAccessStops { get; } = C("tenant_support_access.stops");
    public Counter<long> TenantSupportAccessExpirations { get; } = C("tenant_support_access.expirations");
    public Counter<long> TenantSupportAccessRevocations { get; } = C("tenant_support_access.revocations");
    public Counter<long> TenantSupportAccessWriteDenials { get; } = C("tenant_support_access.write_denials");
    public Counter<long> TenantSupportAccessValidationFailures { get; } = C("tenant_support_access.validation_failures");
    public Histogram<double> TenantSupportAccessSessionDuration { get; } = H("tenant_support_access.session_duration");
}
