using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Background;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Phase 2 extraction: consolidates observability-related service registrations (App Insights telemetry,
/// metrics, audit sink, alert publisher + sampler diagnostics) into a single extension. Logic is a
/// near verbatim move from Program.cs to minimize behavioral risk; future phases can decompose further.
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddMrWhoOidcObservability(this IServiceCollection services, IConfiguration configuration)
    {
        // Application Insights (optional) – only wires if a connection string is provided.
        var aiConn = configuration["ApplicationInsights:ConnectionString"] ?? configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(aiConn))
        {
            services.AddApplicationInsightsTelemetry(o => o.ConnectionString = aiConn);
        }

        // Metrics registration (existing extension kept intact)
        services.AddMrWhoOidcMetrics();

        // HttpClient for webhook alert publisher (idempotent if added elsewhere)
        services.AddHttpClient();

        // Provide system clock abstraction for alert sampler
        services.AddSingleton<MrWhoOidc.WebAuth.Background.ISystemClock, MrWhoOidc.WebAuth.Background.SystemClock>();

        // Alert publisher (webhook or noop)
        services.AddSingleton<IAlertPublisher>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var hasWebhook = !string.IsNullOrWhiteSpace(cfg["Backchannel:AlertWebhook"]);
            return hasWebhook ? new WebhookAlertPublisher(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<WebhookAlertPublisher>>(), cfg) : new NoopAlertPublisher();
        });

        // Backchannel alert sampler (threshold evaluation)
        services.Configure<BackchannelAlertOptions>(configuration.GetSection("Backchannel:Alerts"));
        services.AddHostedService<BackchannelAlertSampler>();
        // Expose diagnostics interface for sampler
        services.AddSingleton<IBackchannelAlertDiagnostics>(sp => (IBackchannelAlertDiagnostics)sp.GetRequiredService<BackchannelAlertSampler>());

        // Audit sink configuration
        services.Configure<AuditOptions>(configuration.GetSection("Audit"));
        services.AddSingleton<IAuditSink>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var opts = sp.GetRequiredService<IOptions<AuditOptions>>().Value;
            if (!opts.Enabled)
                return new NoopAuditSink();

            var sinks = new List<IAuditSink>();
            var sink = opts.Sink?.ToLowerInvariant() ?? "logger";
            if (sink is "logger" or "both")
            {
                sinks.Add(new LoggerAuditSink(
                    sp.GetRequiredService<ILogger<LoggerAuditSink>>(),
                    sp.GetRequiredService<IOptions<AuditOptions>>()));
            }
            if (sink is "appinsights" or "both")
            {
                var telemetry = sp.GetService<Microsoft.ApplicationInsights.TelemetryClient>();
                if (telemetry != null)
                {
                    sinks.Add(new ApplicationInsightsAuditSink(
                        telemetry,
                        sp.GetRequiredService<ILogger<ApplicationInsightsAuditSink>>(),
                        sp.GetRequiredService<IOptions<AuditOptions>>()));
                }
                else if (sink != "logger")
                {
                    sinks.Add(new LoggerAuditSink(
                        sp.GetRequiredService<ILogger<LoggerAuditSink>>(),
                        sp.GetRequiredService<IOptions<AuditOptions>>()));
                }
            }

            if (sinks.Count == 1)
                return sinks[0];
            if (sinks.Count == 0)
                return new NoopAuditSink();
            return new CompositeAuditSink(sinks);
        });

        return services;
    }
}
