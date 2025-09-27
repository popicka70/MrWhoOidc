using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Security;
using MrWhoOidc.WebAuth.Admin.Helpers;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.WebAuth.Infrastructure.Http;
using Microsoft.AspNetCore.Mvc;

namespace MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;

internal static class EndpointMappingExtensions
{
    public static void MapMrWhoOidcEndpoints(this WebApplication app)
    {
        // This method is a straight extraction of the mapping logic from Program.cs (Phase 0 safety refactor step).
        // IMPORTANT: Keep order equivalent; do not introduce middleware changes here.

        app.MapDefaultEndpoints();
        app.MapRazorPages().WithStaticAssets();

        // Delay DB migration & seeding (unchanged)
        var skipMigrations = app.Configuration["Testing:SkipAuthMigrations"];
        if (!string.Equals(skipMigrations, "true", StringComparison.OrdinalIgnoreCase))
        {
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                Task.Run(async () =>
                {
                    using var scope = app.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                    await db.Database.MigrateAsync();
                    var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
                    await keyStore.GetActiveSigningKeyAsync();
                    await MrWhoOidc.Auth.Seeding.DatabaseSeeder.EnsureSeedDataAsync(app.Services);
                });
            });
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