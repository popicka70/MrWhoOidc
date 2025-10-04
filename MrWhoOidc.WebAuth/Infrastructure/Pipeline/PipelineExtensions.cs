using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Middleware;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Infrastructure.Pipeline;

/// <summary>
/// Encapsulates the standard HTTP middleware pipeline for the OIDC server host.
/// Extracted from Program.cs to reduce composition root size while preserving ordering.
/// </summary>
public static class PipelineExtensions
{
    public static WebApplication UseMrWhoOidcPipeline(this WebApplication app, IConnectionMultiplexer? redisMux)
    {
        // Trust proxy forwarded headers (scheme/host)
        var fwdOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
        };
        fwdOptions.KnownNetworks.Clear();
        fwdOptions.KnownProxies.Clear();
        app.UseForwardedHeaders(fwdOptions);

        // Forward client certificates if upstream proxy supplies them
        app.UseCertificateForwarding();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
            app.UseHttpsRedirection();
        }
        // In development we intentionally skip automatic HTTPS redirect to allow http callbacks during local dev.

        app.UseMiddleware<CorrelationTrackingMiddleware>();
        app.UseWhen(static ctx => ctx.Request.Path.StartsWithSegments("/admin/api", StringComparison.OrdinalIgnoreCase), branch =>
        {
            branch.UseMiddleware<AdminCorrelationMiddleware>();
        });

        app.UseRouting();

        // Multi-tenancy: resolve tenant from path early in pipeline
        app.UseTenantResolution();

        // Localization (single supported culture placeholder; future expansion can move to configuration)
        var supportedCultures = new[] { "en-US" };
        var localizationOptions = new RequestLocalizationOptions()
            .SetDefaultCulture(supportedCultures[0])
            .AddSupportedCultures(supportedCultures)
            .AddSupportedUICultures(supportedCultures);
        app.UseRequestLocalization(localizationOptions);

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();

        if (redisMux is not null)
        {
            // Shared Redis sliding-window style limiter (custom middleware) for certain high-volume endpoints
            app.UseMiddleware<DistributedRateLimiterMiddleware>();
        }
        app.UseRateLimiter();

        // Conditionally map static assets (skipped in certain test scenarios)
        if (!app.Configuration.GetValue<bool>("Testing:DisableStaticAssets"))
        {
            app.MapStaticAssets();
        }

        return app;
    }
}
