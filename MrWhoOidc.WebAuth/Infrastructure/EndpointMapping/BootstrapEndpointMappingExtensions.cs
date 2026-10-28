using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Security.Admin;
using MrWhoOidc.WebAuth.Seeding;

namespace MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;

public static class BootstrapEndpointMappingExtensions
{
    private const string BootstrapTokenHeaderName = "X-Bootstrap-Token";
    private static readonly string[] BootstrapPaths = ["/bootstrap", "/api/bootstrap"];
    private static readonly string[] BootstrapManifestPaths = ["/bootstrap/apply-seed-manifest", "/api/bootstrap/apply-seed-manifest"];

    /// <summary>
    /// The moment the endpoints were mapped (the process start). The manifest apply window is measured from
    /// here, so it is a real time box and not a configuration value that stays valid forever.
    /// </summary>
    private static DateTimeOffset MappedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public static void MapMrWhoBootstrapEndpoints(this WebApplication app)
    {
        MappedAtUtc = DateTimeOffset.UtcNow;

        foreach (var path in BootstrapPaths)
        {
            app.MapPost(path, HandleBootstrapAsync)
                .AllowAnonymous()
                // M-Boot: the bootstrap endpoints get their own tight budget.
                .RequireRateLimiting("rl-bootstrap");
        }

        foreach (var path in BootstrapManifestPaths)
        {
            app.MapPost(path, HandleApplySeedManifestAsync)
                .AllowAnonymous()
                .RequireRateLimiting("rl-bootstrap");
        }
    }

    /// <summary>
    /// M-Boot: the bootstrap capability is guarded by a configured secret. A failed attempt is audited (a
    /// structured warning with the path and the caller) so a leaked or guessed token leaves a trace.
    /// </summary>
    private static IResult? AuthorizeBootstrapAsync(
        HttpContext http,
        IConfiguration config,
        ILogger logger,
        string path)
    {
        var configuredToken = config["Bootstrap:Token"];
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            // Safe-by-default: if no token configured, do not expose bootstrap capability.
            logger.LogWarning("Bootstrap request for {Path} rejected: no bootstrap token is configured", path);
            return Results.NotFound();
        }

        if (!TryGetProvidedToken(http, out var providedToken) || !FixedTimeEquals(configuredToken, providedToken))
        {
            logger.LogWarning(
                "SECURITY: rejected bootstrap request for {Path} from {RemoteIp}: invalid or missing bootstrap token",
                path,
                http.Connection.RemoteIpAddress?.ToString());
            return Results.Unauthorized();
        }

        return null;
    }

    private static async Task<IResult> HandleBootstrapAsync(
            HttpContext http,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IMultiTenancyOptions multiTenancyOptions,
            IIssuerBuilder issuerBuilder,
            ISeeder seeder,
            ISeedManifestProvider seedManifestProvider,
            ISeedManifestApplier seedManifestApplier,
            IKeyStore keyStore,
            IKeyRotationService keyRotationService,
            IPasswordHasher passwordHasher,
            IUserAccountService userAccountService,
            IOptions<OidcOptions> oidcOptions,
            IConfiguration config,
            ILoggerFactory loggerFactory,
            CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Bootstrap");

        var denied = AuthorizeBootstrapAsync(http, config, logger, http.Request.Path.Value ?? "/bootstrap");
        if (denied is not null)
        {
            return denied;
        }

        // The bootstrap token authorises seeding the platform realm and its admin role.
        PlatformRealmWriteGuard.AllowSystemWrite(http);

        // Only allow bootstrap on an empty DB.
        if (await db.Tenants.AnyAsync(ct).ConfigureAwait(false))
        {
            logger.LogWarning("Bootstrap request rejected: the database is not empty");
            return Results.Conflict(new { error = "already_bootstrapped" });
        }

        var request = await http.Request.ReadFromJsonAsync<BootstrapRequest>(cancellationToken: ct).ConfigureAwait(false);
        if (request is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        if (string.IsNullOrWhiteSpace(request.AdminEmail) || string.IsNullOrWhiteSpace(request.AdminPassword))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Validation failed", detail: "adminEmail and adminPassword are required.");
        }

        var tenantSlug = string.IsNullOrWhiteSpace(request.TenantSlug)
            ? (multiTenancyOptions.DefaultTenantSlug ?? "default")
            : request.TenantSlug.Trim();

        if (!TenantSlug.IsValid(tenantSlug))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Validation failed",
                detail: "tenantSlug must contain only lowercase letters, digits and hyphens (1-63 chars, no leading/trailing hyphen).");
        }

        var baseUrlCandidate =
            (!string.IsNullOrWhiteSpace(oidcOptions.Value.PublicBaseUrl) ? oidcOptions.Value.PublicBaseUrl : null)
            ?? (!string.IsNullOrWhiteSpace(oidcOptions.Value.Issuer) ? oidcOptions.Value.Issuer : null)
            ?? $"{http.Request.Scheme}://{http.Request.Host}";

        if (!TryGetAuthorityBaseUrl(baseUrlCandidate, out var baseUrl))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid configuration", detail: "Unable to determine a valid base URL from Oidc:PublicBaseUrl/Oidc:Issuer or request URL.");
        }

        var issuerUri = issuerBuilder.BuildIssuer(baseUrl, tenantSlug).TrimEnd('/');

        // Create the initial tenant.
        var tenant = new Tenant
        {
            Slug = tenantSlug,
            Name = string.IsNullOrWhiteSpace(request.TenantName) ? "Default Tenant" : request.TenantName.Trim(),
            Description = "Tenant created via explicit bootstrap",
            IssuerUri = issuerUri,
            Status = TenantStatus.Active,
            MaxUsers = 100000,
            MaxClients = 1000,
            AdminEmail = request.AdminEmail.Trim(),
            BillingPlan = "Enterprise",
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = tenant.Id,
            Slug = tenant.Slug,
            Name = tenant.Name,
            IssuerUri = tenant.IssuerUri,
            IsMultiTenantMode = multiTenancyOptions.Enabled
        });

        await seeder.SeedAsync(ct).ConfigureAwait(false);

        var seedManifest = await seedManifestProvider.TryLoadAsync(ct).ConfigureAwait(false);
        if (seedManifest is not null)
        {
            await seedManifestApplier.ApplyForCurrentTenantAsync(seedManifest, ct).ConfigureAwait(false);
        }

        var normalizedEmail = request.AdminEmail.Trim().ToLowerInvariant();

        var adminUser = await db.Users
            .FirstOrDefaultAsync(u => u.TenantId == tenant.Id && u.Username == "admin", ct)
            .ConfigureAwait(false);

        if (adminUser is not null)
        {
            adminUser.Email = request.AdminEmail.Trim();
            if (!string.IsNullOrWhiteSpace(request.AdminName))
            {
                adminUser.Name = request.AdminName.Trim();
            }
        }

        var adminAccount = await db.UserAccounts
            .FirstOrDefaultAsync(a => a.Username == "admin", ct)
            .ConfigureAwait(false);

        if (adminAccount is not null)
        {
            adminAccount.Email = request.AdminEmail.Trim();
            adminAccount.NormalizedEmail = normalizedEmail;
            if (!string.IsNullOrWhiteSpace(request.AdminName))
            {
                adminAccount.Name = request.AdminName.Trim();
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (adminAccount is not null)
        {
            // Through the account service so the stamp rotates and any live tokens are revoked (C14).
            await userAccountService.UpdatePasswordAsync(adminAccount.Id, passwordHasher.Hash(request.AdminPassword), null, "argon2id", ct).ConfigureAwait(false);
        }

        await keyStore.GetActiveSigningKeyAsync().ConfigureAwait(false);
        await keyRotationService.EnsureInitializedAsync().ConfigureAwait(false);

        logger.LogInformation("Bootstrap completed for tenant '{TenantSlug}'", tenantSlug);

        return Results.Ok(new { tenantId = tenant.Id, slug = tenant.Slug, issuer = tenant.IssuerUri });
    }

    private static async Task<IResult> HandleApplySeedManifestAsync(
            HttpContext http,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IMultiTenancyOptions multiTenancyOptions,
            ISeedManifestProvider seedManifestProvider,
            ISeedManifestApplier seedManifestApplier,
            IOptions<OidcOptions> oidcOptions,
            IConfiguration config,
            ILoggerFactory loggerFactory,
            CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Bootstrap");

        var denied = AuthorizeBootstrapAsync(http, config, logger, http.Request.Path.Value ?? "/bootstrap/apply-seed-manifest");
        if (denied is not null)
        {
            return denied;
        }

        // M-Boot: the manifest endpoint can provision a deployment on its own, so it is not exposed by
        // default. It is enabled by a TIME BOX: the operator configures how long after the server started
        // the manifest may still be applied. After that window the endpoint is inert, so a leaked token
        // cannot be used to provision the deployment again.
        var windowMinutes = config.GetValue("Bootstrap:ManifestApplyWindowMinutes", 0);
        if (windowMinutes <= 0)
        {
            logger.LogWarning(
                "SECURITY: rejected seed manifest request from {RemoteIp}: Bootstrap:ManifestApplyWindowMinutes is not enabled",
                http.Connection.RemoteIpAddress?.ToString());
            return Results.NotFound();
        }

        var windowEndsAt = MappedAtUtc.AddMinutes(windowMinutes);
        if (DateTimeOffset.UtcNow > windowEndsAt)
        {
            logger.LogWarning(
                "SECURITY: rejected seed manifest request from {RemoteIp}: the manifest apply window ended at {WindowEndsAt}",
                http.Connection.RemoteIpAddress?.ToString(),
                windowEndsAt);
            return Results.NotFound();
        }

        // The bootstrap token authorises seeding the platform realm and its admin role.
        PlatformRealmWriteGuard.AllowSystemWrite(http);

        var seedManifest = await seedManifestProvider.TryLoadAsync(ct).ConfigureAwait(false);
        if (seedManifest is null)
        {
            return Results.NotFound(new { error = "seed_manifest_not_configured" });
        }

        var baseUrlCandidate =
            (!string.IsNullOrWhiteSpace(oidcOptions.Value.PublicBaseUrl) ? oidcOptions.Value.PublicBaseUrl : null)
            ?? (!string.IsNullOrWhiteSpace(oidcOptions.Value.Issuer) ? oidcOptions.Value.Issuer : null)
            ?? $"{http.Request.Scheme}://{http.Request.Host}";

        if (!TryGetAuthorityBaseUrl(baseUrlCandidate, out var baseUrl))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid configuration", detail: "Unable to determine a valid base URL from Oidc:PublicBaseUrl/Oidc:Issuer or request URL.");
        }

        await seedManifestApplier.ApplyTenantsAsync(seedManifest, baseUrl, ct).ConfigureAwait(false);

        var appliedTenants = new List<string>();
        foreach (var tenantDef in seedManifest.Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenantDef.Slug))
            {
                continue;
            }

            var slug = tenantDef.Slug.Trim();
            var tenant = await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Slug == slug, ct)
                .ConfigureAwait(false);

            if (tenant is null)
            {
                continue;
            }

            tenantAccessor.SetTenant(new TenantContext
            {
                TenantId = tenant.Id,
                Slug = tenant.Slug,
                Name = tenant.Name,
                IssuerUri = tenant.IssuerUri,
                IsMultiTenantMode = multiTenancyOptions.Enabled
            });

            await seedManifestApplier.ApplyForCurrentTenantAsync(seedManifest, ct).ConfigureAwait(false);
            appliedTenants.Add(tenant.Slug);
        }

        logger.LogInformation(
            "Seed manifest applied to existing deployment for {TenantCount} tenant(s): {TenantSlugs}",
            appliedTenants.Count,
            string.Join(", ", appliedTenants));

        return Results.Ok(new
        {
            appliedTenants,
            manifestConfigured = true
        });
    }

    private static bool TryGetProvidedToken(HttpContext http, out string token)
    {
        token = string.Empty;

        if (http.Request.Headers.TryGetValue(BootstrapTokenHeaderName, out var headerValues))
        {
            token = headerValues.ToString();
            return !string.IsNullOrWhiteSpace(token);
        }

        return false;
    }

    private static bool FixedTimeEquals(string configuredToken, string providedToken)
    {
        var a = Encoding.UTF8.GetBytes(configuredToken);
        var b = Encoding.UTF8.GetBytes(providedToken);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool TryGetAuthorityBaseUrl(string candidate, out string baseUrl)
    {
        baseUrl = string.Empty;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Only use the authority portion (scheme + host + port). Any path in the input is ignored.
        baseUrl = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return !string.IsNullOrWhiteSpace(baseUrl);
    }

    private sealed record BootstrapRequest(
        string? TenantSlug,
        string? TenantName,
        string AdminEmail,
        string AdminPassword,
        string? AdminName);
}
