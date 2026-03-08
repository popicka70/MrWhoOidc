using System;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Middleware;
using MrWhoOidc.WebAuth.Observability;
using Microsoft.AspNetCore.Http;

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

        // Forwarded headers: safe-by-default (loopback only) unless explicitly configured.
        // NOTE: Never trust X-Forwarded-* from arbitrary clients in production.
        if (ForwardedHeadersConfigurator.TryBuild(app.Configuration, app.Environment, app.Logger, out var fwdOptions))
        {
            app.UseForwardedHeaders(fwdOptions);
        }

        // Optional host allow-list enforcement (recommended in production when honoring X-Forwarded-Host).
        // Controlled via ForwardedHeaders:EnforceHostAllowList.
        app.UseMiddleware<HostAllowListMiddleware>();

        // Forward client certificates if upstream proxy supplies them
        app.UseCertificateForwarding();

        var requireHttps = app.Configuration.GetValue<bool?>("Security:RequireHttps")
            ?? !app.Environment.IsDevelopment();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        // Default behavior: skip HTTPS redirects in Development (local callback flexibility).
        // Override with Security:RequireHttps=true to enforce HTTPS even in Development (recommended for E2E).
        if (requireHttps)
        {
            app.UseHttpsRedirection();
        }

        // Security headers for user-facing HTML pages (Razor Pages).
        // Kept separate from protocol endpoints to avoid breaking OAuth/OIDC responses.
        app.UseMiddleware<SecurityHeadersMiddleware>();

        app.UseMiddleware<CorrelationTrackingMiddleware>();
        app.UseWhen(static ctx => ctx.Request.Path.StartsWithSegments("/admin/api", StringComparison.OrdinalIgnoreCase), branch =>
        {
            branch.UseMiddleware<AdminCorrelationMiddleware>();
        });

        // Diagnostics for 400s (safe metadata only). Note: Kestrel parse-time rejects won't reach this.
        if (app.Environment.IsDevelopment())
        {
            app.UseMiddleware<BadRequestDiagnosticsMiddleware>();
        }

        app.UseRouting();

        // Multi-tenancy: resolve tenant from path early in pipeline
        app.UseTenantResolution();

        // Session storage (required for tenant discovery flow)
        app.UseSession();

        // Track tenant selections in session for tenant-unaware routes
        app.UseMiddleware<TenantSelectionTrackingMiddleware>();

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
        app.UseMiddleware<FeatureGatingMiddleware>();

        // Tenant-aware redirect: redirect users from tenant-unaware URLs to tenant-specific versions
        app.UseTenantAwareRedirect();

        // Handle status code pages (must be after authentication/authorization)
        // IMPORTANT: Only handle 4xx/5xx status codes for user-facing pages. Do NOT transform API/protocol responses.
        app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;
            var response = http.Response;
            var request = http.Request;

            // If protocol/auth headers are present or JSON content is being returned, do not interfere
            var contentType = response.ContentType ?? string.Empty;
            if (response.Headers.ContainsKey("WWW-Authenticate") ||
                response.Headers.ContainsKey("DPoP-Nonce") ||
                contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return; // preserve original response (e.g., OAuth/DPoP challenges)
            }

            // Skip status code page for API/protocol endpoints
            var p = request.Path;
            if (p.StartsWithSegments("/.well-known", out _) ||
                p.StartsWithSegments("/jwks", out _) ||
                p.StartsWithSegments("/token", out _) ||
                p.StartsWithSegments("/revoke", out _) ||
                p.StartsWithSegments("/introspect", out _) ||
                p.StartsWithSegments("/par", out _) ||
                p.StartsWithSegments("/userinfo", out _) ||
                p.StartsWithSegments("/api", out _) ||
                p.StartsWithSegments("/connect", out _) ||
                p.StartsWithSegments("/auth/external", out _))
            {
                return; // leave status code and body untouched
            }

            // For user-facing routes, re-execute pipeline to render error page
            // For 404 errors, include kebab-case URL suggestion if applicable
            var originalPath = request.Path.Value ?? "/";

            if (response.StatusCode == 404)
            {
                // Check if PascalCase URL might have kebab-case equivalent
                var suggestion = MrWhoOidc.WebAuth.Extensions.UrlConversionHelper.SuggestKebabCase(originalPath);

                request.Path = "/NotFound";
                if (!string.IsNullOrEmpty(suggestion))
                {
                    request.QueryString = new QueryString($"?path={Uri.EscapeDataString(originalPath)}&suggestion={Uri.EscapeDataString(suggestion)}");
                }
                else
                {
                    request.QueryString = new QueryString($"?path={Uri.EscapeDataString(originalPath)}");
                }
            }
            else
            {
                // Other status codes (401, 403, 500, etc.)
                request.Path = "/Error";
                request.QueryString = new QueryString($"?statusCode={response.StatusCode}");
            }

            await context.Next(http);
        });

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
