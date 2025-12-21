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
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Seeding;

namespace MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;

public static class BootstrapEndpointMappingExtensions
{
    private const string BootstrapTokenHeaderName = "X-Bootstrap-Token";

    public static void MapMrWhoBootstrapEndpoints(this WebApplication app)
    {
        app.MapPost("/bootstrap", async (
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
            IOptions<OidcOptions> oidcOptions,
            IConfiguration config,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Bootstrap");

            var configuredToken = config["Bootstrap:Token"];
            if (string.IsNullOrWhiteSpace(configuredToken))
            {
                // Safe-by-default: if no token configured, do not expose bootstrap capability.
                return Results.NotFound();
            }

            if (!TryGetProvidedToken(http, out var providedToken) || !FixedTimeEquals(configuredToken, providedToken))
            {
                return Results.Unauthorized();
            }

            // Only allow bootstrap on an empty DB.
            if (await db.Tenants.AnyAsync(ct).ConfigureAwait(false))
            {
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

            // Set tenant context for seeding.
            tenantAccessor.SetTenant(new TenantContext
            {
                TenantId = tenant.Id,
                Slug = tenant.Slug,
                Name = tenant.Name,
                IssuerUri = tenant.IssuerUri,
                IsMultiTenantMode = multiTenancyOptions.Enabled
            });

            await seeder.SeedAsync(ct).ConfigureAwait(false);

            // Optional: apply manifest-provided realms/clients for this tenant.
            var seedManifest = await seedManifestProvider.TryLoadAsync(ct).ConfigureAwait(false);
            if (seedManifest is not null)
            {
                await seedManifestApplier.ApplyForCurrentTenantAsync(seedManifest, ct).ConfigureAwait(false);
            }

            // Ensure the seeded admin account uses the operator-supplied password and email.
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

                adminAccount.PasswordHash = passwordHasher.Hash(request.AdminPassword);
                adminAccount.HashAlgorithm = "argon2id";
                adminAccount.PasswordUpdatedAt = DateTimeOffset.UtcNow;
                adminAccount.FailedLoginAttempts = 0;
                adminAccount.LastFailedLoginAt = null;
                adminAccount.LockedOutUntil = null;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Initialize signing keys and rotation policies now that a tenant exists.
            await keyStore.GetActiveSigningKeyAsync().ConfigureAwait(false);
            await keyRotationService.EnsureInitializedAsync().ConfigureAwait(false);

            logger.LogInformation("Bootstrap completed for tenant '{TenantSlug}'", tenantSlug);

            return Results.Ok(new { tenantId = tenant.Id, slug = tenant.Slug, issuer = tenant.IssuerUri });
        })
        .AllowAnonymous();
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
