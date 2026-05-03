using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;
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

    public static TaskCompletionSource<bool> GetMigrationCompletionSource() => _migrationCompletionSource;

    public static void MapMrWhoOidcEndpoints(this WebApplication app)
    {
        // This method is a straight extraction of the mapping logic from Program.cs (Phase 0 safety refactor step).
        // IMPORTANT: Keep order equivalent; do not introduce middleware changes here.

        app.MapDefaultEndpoints();
        app.MapRazorPages().WithStaticAssets();

        // Run DB migration & seeding asynchronously (but DO NOT add middleware here)
        // Middleware has been moved to UseMrWhoOidcPipeline to ensure correct ordering
        var skipMigrations = app.Configuration["Testing:SkipAuthMigrations"];
        if (!string.Equals(skipMigrations, "true", StringComparison.OrdinalIgnoreCase))
        {
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

                        logger.LogInformation("Initializing tenant context for startup...");
                        var multiTenancyOptions = scope.ServiceProvider.GetRequiredService<IMultiTenancyOptions>();
                        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();

                        // Load default tenant for startup operations
                        var defaultTenant = await db.Tenants
                            .Where(t => t.Slug == multiTenancyOptions.DefaultTenantSlug && t.Status == TenantStatus.Active)
                            .FirstOrDefaultAsync();

                        if (defaultTenant == null)
                        {
                            logger.LogWarning("Default tenant '{Slug}' not found. Signing key initialization skipped.",
                                multiTenancyOptions.DefaultTenantSlug);
                        }
                        else
                        {
                            // Set tenant context for startup operations
                            var tenantContext = new TenantContext
                            {
                                TenantId = defaultTenant.Id,
                                Slug = defaultTenant.Slug,
                                Name = defaultTenant.Name,
                                IssuerUri = defaultTenant.IssuerUri,
                                IsMultiTenantMode = multiTenancyOptions.Enabled
                            };
                            tenantAccessor.SetTenant(tenantContext);
                            logger.LogInformation("Tenant context set to '{TenantSlug}' for startup operations.", defaultTenant.Slug);

                            logger.LogInformation("Initializing signing keys...");
                            var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
                            await keyStore.GetActiveSigningKeyAsync();
                            logger.LogInformation("Signing keys initialized.");

                            logger.LogInformation("Applying key rotation policies...");
                            var rotation = scope.ServiceProvider.GetRequiredService<IKeyRotationService>();
                            await rotation.EnsureInitializedAsync();
                            logger.LogInformation("Key rotation policies applied.");
                        }

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

        // Always register both tenant-prefixed and root-level routes.
        // Multi-tenancy state is determined at runtime from license (via IMultiTenancyOptions),
        // but routes must be mapped at startup before the license is loaded.
        // The handlers will enforce mode-appropriate behavior at request time.

        // Register tenant-prefixed routes for multi-tenant mode
        var tenantGroup = app.MapGroup("/t/{slug}");
        MapOidcEndpoints(tenantGroup);

        // Register root-level routes (used in single-tenant mode, or as fallback in multi-tenant mode)
        MapOidcEndpoints(app);

        var admin = app.MapGroup("/admin/api").RequireAuthorization("admin").RequireRateLimiting("rl-admin");

        // NOTE: For brevity, admin endpoints not yet extracted in detail for snapshot step; keeping manifest stability focus.
        // (Retain existing admin endpoints inline in Program for now to reduce patch size.)
    }

    /// <summary>
    /// Maps all OIDC protocol and auth endpoints to the specified route builder.
    /// Can be called with app (root level) or a MapGroup (tenant-prefixed).
    /// </summary>
    private static void MapOidcEndpoints(IEndpointRouteBuilder routes)
    {
        var app = routes as WebApplication;
        var authOptions = (app?.Services ?? routes.ServiceProvider).GetRequiredService<IOptions<AuthOptions>>();

        // OIDC Discovery and JWKS endpoints
        routes.MapGet("/.well-known/openid-configuration", (IDiscoveryHandler h, HttpContext ctx) => h.HandleAsync(ctx))
            .RequireCors("oidc")
            .RequireRateLimiting("rl-authorize");

        routes.MapGet("/jwks", GetServerJwks)
            .RequireCors("oidc");

        // Optional client JWKS endpoint
        if (authOptions.Value.ExposeClientJwks)
        {
            routes.MapGet("/clients/{clientId}/jwks", async (string clientId, IPublicJwksCache cache, HttpContext ctx, CancellationToken ct) =>
            {
                var (etag, json) = await cache.GetClientAsync(clientId, ct);
                var notModified = EtagHelpers.SetConditionalEtag(ctx, etag);
                ctx.Response.Headers["Cache-Control"] = $"public, max-age={authOptions.Value.ClientJwksCacheSeconds}";
                if (notModified) return Results.StatusCode(StatusCodes.Status304NotModified);
                return Results.Text(json, "application/json");
            }).RequireRateLimiting("rl-jwks");
        }

        // Optional provider JWKS endpoints
        if (authOptions.Value.ExposeProviderJwks)
        {
            routes.MapGet("/providers/{providerName}/jwks", async (string providerName, IPublicJwksCache cache, HttpContext ctx, CancellationToken ct) =>
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
            routes.MapGet("/providers/jwks", async (IPublicJwksCache cache, HttpContext ctx, CancellationToken ct) =>
            {
                var (etag, json) = await cache.GetAllProvidersAsync(ct);
                var notModified = EtagHelpers.SetConditionalEtag(ctx, etag);
                ctx.Response.Headers["Cache-Control"] = $"public, max-age={authOptions.Value.ProviderJwksCacheSeconds}";
                if (notModified) return Results.StatusCode(StatusCodes.Status304NotModified);
                return Results.Text(json, "application/json");
            }).RequireRateLimiting("rl-jwks");
        }

        // OIDC protocol endpoints
        routes.MapMethods("/authorize", new[] { "GET", "POST" }, (IAuthorizeHandler h, HttpContext ctx) => h.HandleAsync(ctx))
            .RequireRateLimiting("rl-authorize");

        // OIDC Session Management (check_session_iframe)
        routes.MapGet("/connect/checksession", (ICheckSessionHandler h, HttpContext ctx) => h.HandleAsync(ctx))
            .RequireCors("oidc")
            .RequireRateLimiting("rl-authorize");

        routes.MapGet("/logout", (ILogoutHandler h, HttpContext ctx) => h.LogoutEntryAsync(ctx))
            .RequireRateLimiting("rl-logout");
        routes.MapGet("/logout/federated-callback", (ILogoutHandler h, HttpContext ctx) => h.FederatedCallbackAsync(ctx))
            .RequireRateLimiting("rl-logout");
        routes.MapGet("/logout/final", (ILogoutHandler h, HttpContext ctx) => h.FinalRedirectAsync(ctx))
            .RequireRateLimiting("rl-logout");
        routes.MapGet("/connect/endsession", (ILogoutHandler h, HttpContext ctx) => h.EndSessionAsync(ctx))
            .RequireRateLimiting("rl-logout");

        routes.MapPost("/token", (ITokenHandler h, HttpContext ctx) => h.HandleAsync(ctx))
            .RequireCors("oidc")
            .RequireRateLimiting("rl-token")
            .RequireRateLimiting("rl-token-exchange");
        routes.MapMethods("/token", new[] { "OPTIONS" }, () => Results.Ok())
            .RequireCors("oidc");

        routes.MapPost("/revoke", (IRevocationHandler h, HttpContext ctx) => h.HandleAsync(ctx))
            .RequireRateLimiting("rl-revoke");

        // Dynamic client registration (RFC 7591)
        routes.MapPost("/register", (IRegistrationHandler h, HttpContext ctx) => h.HandleAsync(ctx))
            .RequireCors("oidc")
            .RequireRateLimiting("rl-authorize"); // Use authorize rate limit to prevent abuse

        // Dynamic client configuration management (RFC 7592)
        routes.MapGet("/register/{clientId}", (IClientConfigurationHandler h, HttpContext ctx, string clientId) => h.GetClientAsync(ctx, clientId))
            .RequireCors("oidc");
        routes.MapPut("/register/{clientId}", (IClientConfigurationHandler h, HttpContext ctx, string clientId) => h.UpdateClientAsync(ctx, clientId))
            .RequireCors("oidc");
        routes.MapDelete("/register/{clientId}", (IClientConfigurationHandler h, HttpContext ctx, string clientId) => h.DeleteClientAsync(ctx, clientId))
            .RequireCors("oidc");
        routes.MapMethods("/register/{clientId}", new[] { "OPTIONS" }, () => Results.Ok())
            .RequireCors("oidc");

        routes.MapMethods("/userinfo", new[] { "GET", "POST" }, (IUserInfoHandler h, HttpContext ctx) => h.HandleAsync(ctx))
            .RequireCors("oidc")
            .RequireRateLimiting("rl-userinfo");
        routes.MapMethods("/userinfo", new[] { "OPTIONS" }, () => Results.Ok())
            .RequireCors("oidc");

        routes.MapPost("/introspect", (IIntrospectionHandler h, HttpContext ctx) => h.HandleAsync(ctx))
            .RequireRateLimiting("rl-introspect");

        routes.MapPost("/par", (IParHandler h, HttpContext ctx) => h.HandleAsync(ctx))
            .RequireCors("oidc")
            .RequireRateLimiting("rl-par");
        routes.MapMethods("/par", new[] { "OPTIONS" }, () => Results.Ok())
            .RequireCors("oidc");

        // RFC 8628: Device Authorization Grant endpoint
        routes.MapPost("/device/authorize", (IDeviceAuthorizationHandler h, HttpContext ctx) => h.HandleAsync(ctx))
            .RequireCors("oidc")
            .RequireRateLimiting("rl-authorize");
        routes.MapMethods("/device/authorize", new[] { "OPTIONS" }, () => Results.Ok())
            .RequireCors("oidc");

        // OpenID Connect CIBA (Client Initiated Backchannel Authentication) endpoint
        routes.MapPost("/bc-authorize", (ICibaAuthenticationHandler h, HttpContext ctx) => h.HandleAsync(ctx))
            .RequireCors("oidc")
            .RequireRateLimiting("rl-authorize");
        routes.MapMethods("/bc-authorize", new[] { "OPTIONS" }, () => Results.Ok())
            .RequireCors("oidc");

        // External OIDC (IdP chaining) endpoints
        routes.MapGet("/auth/external/start", (IExternalOidcHandler h, HttpContext ctx) => h.StartAsync(ctx))
            .RequireRateLimiting("rl-external");
        routes.MapGet("/auth/external/callback", (IExternalOidcHandler h, HttpContext ctx) => h.CallbackAsync(ctx))
            .RequireRateLimiting("rl-external");
        routes.MapGet("/auth/external/confirm", (IExternalOidcHandler h, HttpContext ctx) => h.ConfirmLinkAsync(ctx))
            .RequireRateLimiting("rl-external");

        // QR login endpoints
        // Note: /auth/qr and /auth/qr-confirm are handled by Razor Pages directly
        routes.MapGet("/auth/qr-mobile", (IQrLoginHandler h, HttpContext ctx) => h.MobileLandingAsync(ctx));
        routes.MapGet("/auth/qr-complete", (IQrLoginHandler h, HttpContext ctx, string session) => h.CompleteAsync(ctx, session))
            .RequireRateLimiting("rl-qr-poll");
        routes.MapGet("/api/qr/status/{sessionToken}", (IQrLoginHandler h, HttpContext ctx, string sessionToken) => h.GetStatusAsync(ctx, sessionToken))
            .RequireRateLimiting("rl-qr-poll");
        routes.MapPost("/api/qr/confirm", (IQrLoginHandler h, HttpContext ctx) => h.ConfirmAsync(ctx))
            .RequireRateLimiting("rl-qr-confirm");
        routes.MapPost("/api/qr/cancel", (IQrLoginHandler h, HttpContext ctx) => h.CancelAsync(ctx))
            .RequireRateLimiting("rl-qr-cancel");

        // WebAuthn/FIDO2 endpoints
        routes.MapPost("/api/webauthn/registration/challenge", (IWebAuthnHandler h, HttpContext ctx) => h.RegistrationChallengeAsync(ctx))
            .RequireAuthorization()
            .RequireRateLimiting("rl-authorize");

        routes.MapPost("/api/webauthn/registration/complete", (IWebAuthnHandler h, HttpContext ctx) => h.RegistrationCompletionAsync(ctx))
            .RequireAuthorization()
            .RequireRateLimiting("rl-authorize");

        routes.MapPost("/api/webauthn/authentication/challenge", (IWebAuthnHandler h, HttpContext ctx) => h.AuthenticationChallengeAsync(ctx))
            .RequireRateLimiting("rl-authorize");

        routes.MapPost("/api/webauthn/authentication/complete", (IWebAuthnHandler h, HttpContext ctx) => h.AuthenticationCompletionAsync(ctx))
            .RequireRateLimiting("rl-authorize");

        routes.MapGet("/api/webauthn/credentials", (IWebAuthnHandler h, HttpContext ctx) => h.GetUserCredentialsAsync(ctx))
            .RequireAuthorization()
            .RequireRateLimiting("rl-authorize");

        routes.MapMethods("/api/webauthn/credentials/{credentialId:guid}", new[] { "PATCH" }, (IWebAuthnHandler h, HttpContext ctx) => h.RenameCredentialAsync(ctx))
            .RequireAuthorization()
            .RequireRateLimiting("rl-authorize");

        routes.MapDelete("/api/webauthn/credentials/{credentialId:guid}", (IWebAuthnHandler h, HttpContext ctx) => h.RemoveCredentialAsync(ctx))
            .RequireAuthorization()
            .RequireRateLimiting("rl-authorize");

        // Tenant icon endpoint (public access for display in UI)
        routes.MapGet("/api/icon/{iconId:guid}", async (
            Guid iconId,
            MrWhoOidc.Auth.Services.ITenantIconService iconService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var icon = await iconService.GetIconAsync(iconId, ct);
            if (icon == null)
            {
                return Results.NotFound();
            }

            // Set cache headers for icon serving
            ctx.Response.Headers["Cache-Control"] = "public, max-age=3600"; // 1 hour cache
            ctx.Response.Headers["ETag"] = $"\"{iconId}\"";

            // Check if client has cached version
            var ifNoneMatch = ctx.Request.Headers["If-None-Match"].FirstOrDefault();
            if (ifNoneMatch == $"\"{iconId}\"")
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.File(icon.FileData, icon.ContentType, icon.FileName);
        });

        // Identity Provider logo endpoint (serves logo from database)
        routes.MapGet("/api/providers/{id:guid}/logo", async (
            Guid id,
            AuthDbContext db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var provider = await db.IdentityProviders
                .Where(p => p.Id == id)
                .Select(p => new { p.LogoData, p.LogoContentType, p.UpdatedAt })
                .FirstOrDefaultAsync(ct);

            if (provider?.LogoData == null || provider.LogoData.Length == 0)
            {
                return Results.NotFound();
            }

            // Set cache headers for logo serving
            ctx.Response.Headers["Cache-Control"] = "public, max-age=3600"; // 1 hour cache
            var etag = $"\"{id}:{provider.UpdatedAt.ToUnixTimeSeconds()}\"";
            ctx.Response.Headers["ETag"] = etag;

            // Check if client has cached version
            var ifNoneMatch = ctx.Request.Headers["If-None-Match"].FirstOrDefault();
            if (ifNoneMatch == etag)
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.File(provider.LogoData, provider.LogoContentType ?? "image/png");
        });
    }

    // Separate method so [FromServices] attribute is honored by minimal API binder (lambda parameter attributes can be ignored).
    private static async Task<IResult> GetServerJwks(HttpContext ctx, [FromServices] IKeyStore keyStore, [FromServices] IOptions<AuthOptions> authOptions, CancellationToken ct)
    {
        var jwks = await keyStore.GetPublicJwksAsync(includeEncryptionKeys: authOptions.Value.EnableRequestObjectEncryption, ct: ct);
        ctx.Response.Headers["Cache-Control"] = "public, max-age=300";
        return Results.Json(new { keys = jwks });
    }
}
