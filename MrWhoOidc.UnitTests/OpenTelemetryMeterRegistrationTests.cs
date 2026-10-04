using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MrWhoOidc.Auth.Observability;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace MrWhoOidc.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class OpenTelemetryMeterRegistrationTests
{
    [TestMethod]
    [DataRow(ClientSecretMetrics.MeterName)]
    [DataRow(GlobalAuthMetrics.MeterName)]
    public void ConfigureOpenTelemetry_ListensToAuthMeters(string meterName)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.ConfigureOpenTelemetry();
        // The SDK only enables instruments when a reader is attached; production adds one via the OTLP exporter.
        builder.Services.ConfigureOpenTelemetryMeterProvider(m => m.AddReader(new BaseExportingMetricReader(new NoopExporter())));
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<MeterProvider>(); // starts the OTel meter listener

        using var meter = new Meter(meterName);
        var counter = meter.CreateCounter<long>("probe");

        Assert.IsTrue(counter.Enabled, $"Meter '{meterName}' is not registered with AddMeter, so its metrics are never exported.");
    }

    private sealed class NoopExporter : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch) => ExportResult.Success;
    }
}
