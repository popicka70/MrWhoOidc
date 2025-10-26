using System.Diagnostics.Metrics;

namespace MrWhoOidc.ServiceDefaults.Observability;

/// <summary>
/// Meters and instruments used across licensing services for OTLP/OpenTelemetry export.
/// </summary>
public static class LicensingMetrics
{
    public const string MeterName = "MrWhoOidc.Auth.Licensing";

    private static readonly Meter Meter = new(MeterName);

    public static Counter<long> LicenseInstallSuccess { get; } = Meter.CreateCounter<long>("licensing.license.install.success");
    public static Counter<long> LicenseInstallFailure { get; } = Meter.CreateCounter<long>("licensing.license.install.failure");
    public static Counter<long> LicenseRevokeSuccess { get; } = Meter.CreateCounter<long>("licensing.license.revoke.success");
    public static Counter<long> LicenseRevokeFailure { get; } = Meter.CreateCounter<long>("licensing.license.revoke.failure");
    public static Histogram<double> LicenseInstallDurationMs { get; } = Meter.CreateHistogram<double>("licensing.license.install.duration.ms");
    public static Histogram<double> LicenseRevokeDurationMs { get; } = Meter.CreateHistogram<double>("licensing.license.revoke.duration.ms");
    public static Counter<long> LicenseValidationSuccess { get; } = Meter.CreateCounter<long>("licensing.license.validate.success");
    public static Counter<long> LicenseValidationFailure { get; } = Meter.CreateCounter<long>("licensing.license.validate.failure");

    public static void RecordInstallResult(bool success, double durationMs)
    {
        if (success)
        {
            LicenseInstallSuccess.Add(1);
        }
        else
        {
            LicenseInstallFailure.Add(1);
        }

        LicenseInstallDurationMs.Record(durationMs);
    }

    public static void RecordRevokeResult(bool success, double durationMs)
    {
        if (success)
        {
            LicenseRevokeSuccess.Add(1);
        }
        else
        {
            LicenseRevokeFailure.Add(1);
        }

        LicenseRevokeDurationMs.Record(durationMs);
    }

    public static void RecordValidationResult(bool success)
    {
        if (success)
        {
            LicenseValidationSuccess.Add(1);
        }
        else
        {
            LicenseValidationFailure.Add(1);
        }
    }
}
