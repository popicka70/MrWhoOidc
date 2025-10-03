using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Handlers.Logout;
using MrWhoOidc.WebAuth.Security;
using MrWhoOidc.WebAuth.Admin.Helpers;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.WebAuth.Infrastructure.Http;
using Microsoft.AspNetCore.Mvc;

namespace MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;

internal static class EndpointMappingExtensions
{
    private static readonly TaskCompletionSource<bool> _migrationCompletionSource = new();

    public static void MapMrWhoOidcEndpoints(this WebApplication app)
    {
        // This method is a straight extraction of the mapping logic from Program.cs (Phase 0 safety refactor step).
        // IMPORTANT: Keep order equivalent; do not introduce middleware changes here.

        app.MapDefaultEndpoints();
        app.MapRazorPages().WithStaticAssets();

        // Run DB migration & seeding asynchronously but gate requests until complete
        var skipMigrations = app.Configuration["Testing:SkipAuthMigrations"];
        if (!string.Equals(skipMigrations, "true", StringComparison.OrdinalIgnoreCase))
        {
            // Add middleware to wait for migrations before processing requests
            app.Use(async (context, next) =>
            {
                // Wait for migrations to complete (will be instant after first completion)
                await _migrationCompletionSource.Task;
                await next(context);
            });

            // Start migrations asynchronously on ApplicationStarted
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        using var scope = app.Services.CreateScope();
                        var logger = scope.ServiceProvider.GetRequiredService<ILogger<WebApplication>>();
                        
                        logger.LogInformation("Starting database migrations...");
                        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                        await db.Database.MigrateAsync();
                        logger.LogInformation("Database migrations completed successfully.");
                        
                        logger.LogInformation("Initializing signing keys...");
                        var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
                        await keyStore.GetActiveSigningKeyAsync();
                        logger.LogInformation("Signing keys initialized.");
                        
                        logger.LogInformation("Seeding database...");
                        await MrWhoOidc.Auth.Seeding.DatabaseSeeder.EnsureSeedDataAsync(app.Services);
                        logger.LogInformation("Database seeding completed.");
                        
                        // Signal that migrations are complete
                        _migrationCompletionSource.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        var logger = app.Services.GetRequiredService<ILogger<WebApplication>>();
                        logger.LogCritical(ex, "Fatal error during database migration/seeding. Application cannot start.");
                        _migrationCompletionSource.TrySetException(ex);
                        // Allow the exception to propagate - app should fail to start properly
                        throw;
                    }
                });
            });
        }
        else
        {
            // If migrations are skipped, signal completion immediately
            _migrationCompletionSource.TrySetResult(true);
        }

        app.MapGet("/.well-known/openid-configuration", (IDiscoveryHandler h, HttpContext ctx) => h.Handle(ctx))
           .RequireRateLimiting("rl-authorize");
        app.MapGet("/jwks", GetServerJwks);

        var authOptions = app.Services.GetRequiredService<IOptions<AuthOptions>>();
        if (authOptions.Value.ExposeClientJwks)
        {
            app.MapGet("/clients/{clientId}/jwks", async (string clientId, IPublicJwksCache cache, HttpContext ctx, CancellationToken ct) =>
            {
                var (etag, json) = await cache.GetClientAsync(clientId, ct);
                var notModified = EtagHelpers.SetConditionalEtag(ctx, etag);
                ctx.Response.Headers["Cache-Control"] = $"public, max-age={authOptions.Value.ClientJwksCacheSeconds}";
                if (notModified) return Results.StatusCode(StatusCodes.Status304NotModified);
                return Results.Text(json, "application/json");
            }).RequireRateLimiting("rl-jwks");
        }
        if (authOptions.Value.ExposeProviderJwks)
        {
            app.MapGet("/providers/{providerName}/jwks", async (string providerName, IPublicJwksCache cache, HttpContext ctx, CancellationToken ct) =>
            {
                var (etag, json) = await cache.GetProviderAsync(providerName, ct);
                if (json == "__not_found__") return Results.Problem(statusCode: 404, title: "Provider not found");
                var notModified = EtagHelpers.SetConditionalEtag(ctx, etag);
                ctx.Response.Headers["Cache-Control"] = $"public, max-age={authOptions.Value.ProviderJwksCacheSeconds}";
                if (notModified) return Results.StatusCode(StatusCodes.Status304NotModified);
                return Results.Text(json, "application/json");
            }).RequireRateLimiting("rl-jwks");
        }
        if (authOptions.Value.ExposeAggregatedProviderJwks)
        {
            app.MapGet("/providers/jwks", async (IPublicJwksCache cache, HttpContext ctx, CancellationToken ct) =>
            {
                var (etag, json) = await cache.GetAllProvidersAsync(ct);
                var notModified = EtagHelpers.SetConditionalEtag(ctx, etag);
                ctx.Response.Headers["Cache-Control"] = $"public, max-age={authOptions.Value.ProviderJwksCacheSeconds}";
                if (notModified) return Results.StatusCode(StatusCodes.Status304NotModified);
                return Results.Text(json, "application/json");
            }).RequireRateLimiting("rl-jwks");
        }

        app.MapGet("/authorize", (IAuthorizeHandler h, HttpContext ctx) => h.HandleAsync(ctx))
           .RequireRateLimiting("rl-authorize");
        app.MapGet("/logout", (ILogoutHandler h, HttpContext ctx) => h.LogoutEntryAsync(ctx));
        app.MapGet("/logout/federated-callback", (ILogoutHandler h, HttpContext ctx) => h.FederatedCallbackAsync(ctx));
        app.MapGet("/logout/final", (ILogoutHandler h, HttpContext ctx) => h.FinalRedirectAsync(ctx)); // new opaque redirect resolution
        app.MapGet("/connect/endsession", (ILogoutHandler h, HttpContext ctx) => h.EndSessionAsync(ctx));
        app.MapPost("/token", (ITokenHandler h, HttpContext ctx) => h.HandleAsync(ctx))
           .RequireCors("oidc")
            .RequireRateLimiting("rl-token")
            .RequireRateLimiting("rl-token-exchange");
        app.MapMethods("/token", new[] { "OPTIONS" }, () => Results.Ok())
           .RequireCors("oidc");
        app.MapPost("/revoke", (IRevocationHandler h, HttpContext ctx) => h.HandleAsync(ctx));
        app.MapGet("/userinfo", (IUserInfoHandler h, HttpContext ctx) => h.Handle(ctx))
           .RequireCors("oidc")
           .RequireRateLimiting("rl-userinfo");
        app.MapMethods("/userinfo", new[] { "OPTIONS" }, () => Results.Ok())
           .RequireCors("oidc");
        app.MapPost("/introspect", (IIntrospectionHandler h, HttpContext ctx) => h.HandleAsync(ctx))
           .RequireRateLimiting("rl-introspect");
        app.MapPost("/par", (IParHandler h, HttpContext ctx) => h.HandleAsync(ctx))
           .RequireCors("oidc")
           .RequireRateLimiting("rl-par");
        app.MapMethods("/par", new[] { "OPTIONS" }, () => Results.Ok())
           .RequireCors("oidc");

        app.MapGet("/Auth/External/Start", (IExternalOidcHandler h, HttpContext ctx) => h.StartAsync(ctx));
        app.MapGet("/Auth/External/Callback", (IExternalOidcHandler h, HttpContext ctx) => h.CallbackAsync(ctx));
        app.MapGet("/Auth/External/Confirm", (IExternalOidcHandler h, HttpContext ctx) => h.ConfirmLinkAsync(ctx));

        // QR login endpoints
        // Note: /Auth/Qr is handled by Razor Pages directly (Qr.cshtml)
        app.MapGet("/Auth/QrMobile", (IQrLoginHandler h, HttpContext ctx) => h.MobileLandingAsync(ctx));
        app.MapGet("/Auth/QrConfirm", (IQrLoginHandler h, HttpContext ctx) => h.ConfirmPageAsync(ctx));
        app.MapGet("/api/qr/status/{sessionToken}", (IQrLoginHandler h, HttpContext ctx, string sessionToken) => h.GetStatusAsync(ctx, sessionToken))
           .RequireRateLimiting("rl-qr-poll");
        app.MapPost("/api/qr/confirm", (IQrLoginHandler h, HttpContext ctx) => h.ConfirmAsync(ctx))
           .RequireRateLimiting("rl-qr-confirm");
        app.MapPost("/api/qr/cancel", (IQrLoginHandler h, HttpContext ctx) => h.CancelAsync(ctx))
           .RequireRateLimiting("rl-qr-cancel");

        var admin = app.MapGroup("/admin/api").RequireAuthorization("admin").RequireRateLimiting("rl-admin");

        // NOTE: For brevity, admin endpoints not yet extracted in detail for snapshot step; keeping manifest stability focus.
        // (Retain existing admin endpoints inline in Program for now to reduce patch size.)
    }

    // Separate method so [FromServices] attribute is honored by minimal API binder (lambda parameter attributes can be ignored).
    private static async Task<IResult> GetServerJwks(HttpContext ctx, [FromServices] IKeyStore keyStore, CancellationToken ct)
    {
        var jwks = await keyStore.GetPublicJwksAsync(ct);
        ctx.Response.Headers["Cache-Control"] = "public, max-age=300";
        return Results.Json(new { keys = jwks });
    }
}
