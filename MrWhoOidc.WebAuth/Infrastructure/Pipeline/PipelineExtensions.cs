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
    public static WebApplication UseMrWhoOidcPipeline(this WebApplication app, IConnectionMultiplexer? redisMux, TaskCompletionSource<bool> migrationCompletionSource)
    {
        // Wait for migrations to complete before processing any requests
        // This middleware must be very early in the pipeline, before UseRouting
        app.Use(async (context, next) =>
        {
            // Wait for migrations to complete (will be instant after first completion)
            await migrationCompletionSource.Task;
            await next(context);
        });

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

        // Session storage (required for tenant discovery flow)
        app.UseSession();

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

        // Tenant-aware redirect: redirect users from tenant-unaware URLs to tenant-specific versions
        app.UseTenantAwareRedirect();

        // Handle status code pages (must be after authentication/authorization)
        // IMPORTANT: Only handle 4xx/5xx status codes, and NotFound page will filter API paths
        app.UseStatusCodePagesWithReExecute("/NotFound", "?path={0}");

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
