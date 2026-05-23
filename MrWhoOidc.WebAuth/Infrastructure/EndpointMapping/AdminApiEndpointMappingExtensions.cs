using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Admin.Api;
using MrWhoOidc.WebAuth.Admin.Dto;
using MrWhoOidc.WebAuth.Admin.Helpers;
using MrWhoOidc.WebAuth.Background;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Security;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;

/// <summary>
/// Maps admin management REST APIs (providers, claim mappings, provider keys, client-provider mappings,
/// client JWKS management, back-channel logout outbox controls) plus the lightweight backchannel health endpoint.
/// Extracted from Program.cs to shrink composition root. Behavior preserved verbatim.
/// </summary>
public static class AdminApiEndpointMappingExtensions
{
    public static void MapMrWhoAdminApiEndpoints(this WebApplication app)
    {
        // Admin Management APIs (tenant-admin, ProblemDetails on errors)
        var admin = app.MapGroup("/admin/api").RequireAuthorization("tenant-admin").RequireRateLimiting("rl-admin");

        // Multi-tenant admin API routes for tenant-aware endpoints
        var tenantAdmin = app.MapGroup("/t/{slug}/admin/api").RequireAuthorization("tenant-admin").RequireRateLimiting("rl-admin");

        // Friendly entry points for the admin UI.
        app.MapGet("/admin", static () => Results.Redirect("/admin/clients", permanent: false));
        app.MapGet("/t/{slug}/admin", static (string slug) => Results.Redirect($"/t/{slug}/admin/clients", permanent: false));

        // Client Secrets Management (Phase 2: Secret Rotation)
        MapClientSecretsEndpoints(admin);
        MapClientSecretsEndpoints(tenantAdmin);

        // Tenant Icon Endpoints (mapped to both admin groups)
        MapTenantIconEndpoints(admin);
        MapTenantIconEndpoints(tenantAdmin);

        // Resource listing endpoints (tenant-scoped and platform-wide)
        MapTenantResourceListEndpoints(admin);
        MapTenantResourceListEndpoints(tenantAdmin);

        // Realm CRUD
        MapRealmEndpoints(admin);
        MapRealmEndpoints(tenantAdmin);

        // Client single-get, create, delete
        MapClientMutationEndpoints(admin);
        MapClientMutationEndpoints(tenantAdmin);

        // Scope create, update, delete
        MapScopeMutationEndpoints(admin);
        MapScopeMutationEndpoints(tenantAdmin);

        // User admin: list, get, create, update, delete
        MapUserAdminEndpoints(admin);
        MapUserAdminEndpoints(tenantAdmin);

        // Tenant invitations
        MapInvitationEndpoints(admin);
        MapInvitationEndpoints(tenantAdmin);

        // Client update
        MapClientUpdateEndpoints(admin);
        MapClientUpdateEndpoints(tenantAdmin);

        // User update
        MapUserUpdateEndpoints(admin);
        MapUserUpdateEndpoints(tenantAdmin);

        // Role CRUD
        MapRoleEndpoints(admin);
        MapRoleEndpoints(tenantAdmin);

        // User ↔ Role assignments
        MapUserRoleEndpoints(admin);
        MapUserRoleEndpoints(tenantAdmin);

        // User ↔ Client assignments
        MapUserClientEndpoints(admin);
        MapUserClientEndpoints(tenantAdmin);

        // Client ↔ Scope management
        MapClientScopeEndpoints(admin);
        MapClientScopeEndpoints(tenantAdmin);

        // Provider endpoints (CRUD, claim-mappings, provider-keys, client↔provider mappings, client JWKS)
        ProviderAndBclEndpoints.MapProviderEndpoints(admin);
        ProviderAndBclEndpoints.MapProviderEndpoints(tenantAdmin);

        // BCL outbox admin endpoints
        ProviderAndBclEndpoints.MapBclOutboxEndpoints(admin);
        ProviderAndBclEndpoints.MapBclOutboxEndpoints(tenantAdmin);

        app.MapGet("/version", static (HttpContext http, IHostEnvironment env) =>
        {
            RuntimeVersionMetadata.ApplyResponseHeaders(http.Response);
            RuntimeVersionMetadata.ApplyNoStoreHeaders(http.Response);

            return Results.Ok(RuntimeVersionMetadata.CreatePayload(env.EnvironmentName));
        }).WithName("RuntimeVersion");

        // Root liveness/readiness endpoint used by public docs and operators.
        app.MapGet("/health", async (HttpContext http, AuthDbContext db, ILoggerFactory loggerFactory, IHostEnvironment env, CancellationToken ct) =>
        {
            RuntimeVersionMetadata.ApplyResponseHeaders(http.Response);
            var logger = loggerFactory.CreateLogger("RootHealth");
            var runtime = RuntimeVersionMetadata.CreatePayload(env.EnvironmentName);

            try
            {
                if (!await db.Database.CanConnectAsync(ct))
                {
                    return Results.Problem(
                        statusCode: 503,
                        title: "Unhealthy",
                        detail: "Database connection failed.",
                        instance: "/health");
                }

                var hasTenants = await db.Tenants.AsNoTracking().AnyAsync(ct);

                return Results.Ok(new
                {
                    status = hasTenants ? "healthy" : "degraded",
                    database = "healthy",
                    bootstrapRequired = !hasTenants,
                    runtime,
                    checks = new
                    {
                        issuer = "/health/issuer",
                        globalAuth = "/health/global-auth",
                        clientSecrets = "/health/client-secrets",
                        backchannel = "/health/backchannel",
                        forwardedHeaders = "/health/forwarded-headers"
                    }
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Root health probe failed.");

                return Results.Problem(
                    statusCode: 503,
                    title: "Unhealthy",
                    detail: "Health probe failed while checking application dependencies.",
                    instance: "/health");
            }
        }).WithName("RootHealth");

        // Lightweight health endpoint for BCL dispatcher
        app.MapGet("/health/backchannel", async (AuthDbContext db, BackchannelRuntimeState state, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var backlog = await db.BackchannelLogoutNotifications
                .AsNoTracking()
                .LongCountAsync(n => n.Status == "pending" && (n.NextAttemptAt == null || n.NextAttemptAt <= now), ct);
            var openCircuits = state.Circuits
                .Where(kv => kv.Value.OpenUntil is not null && kv.Value.OpenUntil > DateTimeOffset.UtcNow)
                .Select(kv => new { clientId = kv.Key, kv.Value.Failures, kv.Value.OpenUntil })
                .OrderByDescending(x => x.Failures)
                .Take(20)
                .ToList();
            return Results.Ok(new { enabled = state.EmissionEnabled, backlog, openCircuits });
        }).WithName("BackchannelHealth");

        // Client secret health endpoint
        app.MapGet("/health/client-secrets", async (AuthDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var degradedThreshold = now.AddDays(3); // Degraded if secrets expire within 3 days
            var warningThreshold = now.AddDays(7);   // Warning if secrets expire within 7 days

            // Check for clients with all secrets expired
            var criticalClients = await db.Clients
                .AsNoTracking()
                .Where(c => c.ClientSecrets.Any() && !c.ClientSecrets.Any(s =>
                    s.ActivatedAtUtc != null
                    && s.RevokedAtUtc == null
                    && (s.ExpiresAtUtc == null || s.ExpiresAtUtc > now)))
                .Select(c => new { clientId = c.ClientId, tenantId = c.TenantId })
                .ToListAsync(ct);

            // Check for secrets expiring soon
            var degradedSecrets = await db.ClientSecrets
                .AsNoTracking()
                .Include(s => s.Client)
                .Where(s => s.ActivatedAtUtc != null
                         && s.RevokedAtUtc == null
                         && s.ExpiresAtUtc != null
                         && s.ExpiresAtUtc > now
                         && s.ExpiresAtUtc <= degradedThreshold)
                .ToListAsync(ct);

            var warningSecrets = await db.ClientSecrets
                .AsNoTracking()
                .Include(s => s.Client)
                .Where(s => s.ActivatedAtUtc != null
                         && s.RevokedAtUtc == null
                         && s.ExpiresAtUtc != null
                         && s.ExpiresAtUtc > degradedThreshold
                         && s.ExpiresAtUtc <= warningThreshold)
                .ToListAsync(ct);

            var status = criticalClients.Count > 0 ? "unhealthy"
                       : degradedSecrets.Count > 0 ? "degraded"
                       : "healthy";

            var response = new
            {
                status,
                criticalClients = criticalClients.Count,
                degradedSecrets = degradedSecrets.Count,
                warningSecrets = warningSecrets.Count,
                details = new
                {
                    clientsWithoutActiveSecrets = criticalClients,
                    secretsExpiringWithin3Days = degradedSecrets.Select(s => new
                    {
                        clientId = s.Client.ClientId,
                        secretId = s.Id,
                        description = s.Description,
                        expiresAt = s.ExpiresAtUtc,
                        daysRemaining = (s.ExpiresAtUtc!.Value - now).TotalDays
                    }),
                    secretsExpiringWithin7Days = warningSecrets.Select(s => new
                    {
                        clientId = s.Client.ClientId,
                        secretId = s.Id,
                        description = s.Description,
                        expiresAt = s.ExpiresAtUtc,
                        daysRemaining = (s.ExpiresAtUtc!.Value - now).TotalDays
                    })
                }
            };

            return status == "unhealthy" ? Results.Problem(
                statusCode: 503,
                title: "Unhealthy",
                detail: $"{criticalClients.Count} client(s) have no active secrets",
                instance: "/health/client-secrets")
                : Results.Ok(response);
        }).WithName("ClientSecretHealth");

        // Global authentication health endpoint
        app.MapGet("/health/global-auth", async (AuthDbContext db, CancellationToken ct) =>
        {
            // Check UserAccount table is accessible and has accounts
            var totalAccounts = await db.UserAccounts.AsNoTracking().LongCountAsync(ct);
            var accountsWithPassword = await db.UserAccounts.AsNoTracking()
                .LongCountAsync(a => a.PasswordHash != null && a.PasswordHash != "", ct);
            var accountsWithMfa = await db.UserAccounts.AsNoTracking()
                .LongCountAsync(a => a.TotpEnabled, ct);
            var lockedAccounts = await db.UserAccounts.AsNoTracking()
                .LongCountAsync(a => a.LockedOutUntil != null && a.LockedOutUntil > DateTimeOffset.UtcNow, ct);

            // Determine status
            var status = totalAccounts == 0 ? "degraded" : "healthy";

            return Results.Ok(new
            {
                status,
                totalAccounts,
                accountsWithPassword,
                accountsWithMfa,
                currentlyLockedOut = lockedAccounts,
                migrationProgress = totalAccounts > 0
                    ? Math.Round(100.0 * accountsWithPassword / totalAccounts, 2)
                    : 0.0
            });
        }).WithName("GlobalAuthHealth");

        // OIDC issuer configuration health endpoint
        app.MapGet("/health/issuer", (Microsoft.Extensions.Options.IOptions<OidcOptions> oidcOptions, IWebHostEnvironment env) =>
        {
            static (bool ok, string? problem) ValidateAbsoluteHttps(string? value, bool requireHttps)
            {
                if (string.IsNullOrWhiteSpace(value)) return (false, "missing");
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return (false, "not_absolute_uri");
                if (!string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query)) return (false, "must_not_include_query_or_fragment");
                if (requireHttps && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return (false, "must_use_https");
                return (true, null);
            }

            var requireHttps = !env.IsDevelopment();

            var issuer = string.IsNullOrWhiteSpace(oidcOptions.Value.Issuer) ? null : oidcOptions.Value.Issuer.TrimEnd('/');
            var publicBaseUrl = string.IsNullOrWhiteSpace(oidcOptions.Value.PublicBaseUrl) ? null : oidcOptions.Value.PublicBaseUrl.TrimEnd('/');

            var hasExplicit = issuer is not null || publicBaseUrl is not null;

            var issuerCheck = ValidateAbsoluteHttps(issuer, requireHttps);
            var baseCheck = ValidateAbsoluteHttps(publicBaseUrl, requireHttps);

            // In production-like environments we expect an explicit public base to avoid Host/proxy ambiguity.
            if (!env.IsDevelopment() && !hasExplicit)
            {
                return Results.Problem(
                    statusCode: 503,
                    title: "Unhealthy",
                    detail: "Neither Oidc:Issuer nor Oidc:PublicBaseUrl is configured. Set one explicitly for correct issuer and endpoint URLs behind proxies.",
                    instance: "/health/issuer");
            }

            // Validate any configured values.
            if (issuer is not null && !issuerCheck.ok)
            {
                return Results.Problem(
                    statusCode: 503,
                    title: "Unhealthy",
                    detail: $"Oidc:Issuer is invalid ({issuerCheck.problem}).",
                    instance: "/health/issuer");
            }
            if (publicBaseUrl is not null && !baseCheck.ok)
            {
                return Results.Problem(
                    statusCode: 503,
                    title: "Unhealthy",
                    detail: $"Oidc:PublicBaseUrl is invalid ({baseCheck.problem}).",
                    instance: "/health/issuer");
            }

            return Results.Ok(new
            {
                status = hasExplicit ? "healthy" : "degraded",
                environment = env.EnvironmentName,
                requireHttps,
                issuer,
                publicBaseUrl
            });
        }).WithName("IssuerHealth");

        // Forwarded headers configuration health endpoint
        app.MapGet("/health/forwarded-headers", (
            HttpContext http,
            IConfiguration configuration,
            Microsoft.Extensions.Options.IOptions<OidcOptions> oidcOptions,
            IWebHostEnvironment env) =>
        {
            static string? GetHost(string? url)
            {
                if (string.IsNullOrWhiteSpace(url)) return null;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
                return string.IsNullOrWhiteSpace(uri.Host) ? null : uri.Host;
            }

            static int CountForwardedForEntries(string? headerValue)
            {
                if (string.IsNullOrWhiteSpace(headerValue)) return 0;
                return headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            }

            var forwardedEnabled = configuration.GetValue<bool?>("ForwardedHeaders:Enabled") ?? true;
            var requireHeaderSymmetry = configuration.GetValue<bool?>("ForwardedHeaders:RequireHeaderSymmetry") ?? false;
            var forwardLimit = configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;
            var unsafeTrustAll = configuration.GetValue<bool>("ForwardedHeaders:UnsafeTrustAll")
                                 || configuration.GetValue<bool>("Testing:UnsafeTrustAllForwardedHeaders");
            var enforceHostAllowList = configuration.GetValue<bool>("ForwardedHeaders:EnforceHostAllowList");

            var configuredAllowedHosts = configuration.GetSection("ForwardedHeaders:AllowedHosts").Get<string[]>() ?? Array.Empty<string>();
            var allowedHosts = configuredAllowedHosts
                .Select(static x => x?.Trim())
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(static x => x!)
                .ToArray();

            var canonicalHost = GetHost(oidcOptions.Value.PublicBaseUrl) ?? GetHost(oidcOptions.Value.Issuer);
            if (allowedHosts.Length == 0 && !string.IsNullOrWhiteSpace(canonicalHost))
            {
                allowedHosts = [canonicalHost];
            }

            var knownProxyCount = (configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>())
                .Count(static x => !string.IsNullOrWhiteSpace(x));
            var knownNetworkCount = (configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? Array.Empty<string>())
                .Count(static x => !string.IsNullOrWhiteSpace(x));

            var xff = http.Request.Headers["X-Forwarded-For"].ToString();
            var xfp = http.Request.Headers["X-Forwarded-Proto"].ToString();
            var xfh = http.Request.Headers["X-Forwarded-Host"].ToString();

            var requireProxyTrustConfig = !env.IsDevelopment() && forwardedEnabled;
            var hasProxyTrustConfig = unsafeTrustAll || knownProxyCount > 0 || knownNetworkCount > 0;
            var hasHostAllowList = allowedHosts.Length > 0;

            var status = "healthy";
            if (requireProxyTrustConfig && !hasProxyTrustConfig)
            {
                status = "degraded";
            }
            if (!env.IsDevelopment() && enforceHostAllowList && !hasHostAllowList)
            {
                status = "degraded";
            }

            return Results.Ok(new
            {
                status,
                environment = env.EnvironmentName,
                forwardedEnabled,
                requireHeaderSymmetry,
                forwardLimit,
                unsafeTrustAll,
                enforceHostAllowList,
                allowedHosts,
                knownProxies = knownProxyCount,
                knownNetworks = knownNetworkCount,
                canonicalHost,
                request = new
                {
                    scheme = http.Request.Scheme,
                    host = http.Request.Host.Value,
                    hasXForwardedFor = !string.IsNullOrWhiteSpace(xff),
                    xForwardedForCount = CountForwardedForEntries(xff),
                    hasXForwardedProto = !string.IsNullOrWhiteSpace(xfp),
                    xForwardedProto = string.IsNullOrWhiteSpace(xfp) ? null : xfp,
                    hasXForwardedHost = !string.IsNullOrWhiteSpace(xfh),
                    xForwardedHost = string.IsNullOrWhiteSpace(xfh) ? null : xfh
                }
            });
        }).WithName("ForwardedHeadersHealth");

        // Platform Admin: On-demand tenant seeding (platform-admin only)
        var platformAdmin = app.MapGroup("/platform-admin/api").RequireAuthorization("platform-admin").RequireRateLimiting("rl-admin");

        ProviderAndBclEndpoints.MapPlatformProviderEndpoints(platformAdmin);

        MapPlatformResourceListEndpoints(platformAdmin);

        // Tenant CRUD (get, update, delete — seed is already mapped above)
        MapTenantCrudEndpoints(platformAdmin);

        LicenseEndpoints.MapLicenseEndpoints(admin, tenantAdmin, platformAdmin);
        RateLimitingEndpoints.MapRateLimitingEndpoints(admin, tenantAdmin, platformAdmin);

        platformAdmin.MapPost("/seed-tenant", async (
            MrWhoOidc.WebAuth.Services.ITenantSeedingService seedingService,
            SeedTenantRequest request,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.TenantSlug))
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "TenantSlug is required");

            if (string.IsNullOrWhiteSpace(request.TenantName))
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "TenantName is required");

            var result = await seedingService.SeedSampleTenantAsync(
                request.TenantSlug,
                request.TenantName,
                request.AdminEmail,
                request.AdminPassword,
                ct);

            if (!result.IsSuccess)
                return Results.Problem(statusCode: 400, title: "Seeding failed", detail: result.ErrorMessage);

            return Results.Ok(new
            {
                success = true,
                tenantId = result.TenantId,
                tenantSlug = result.TenantSlug,
                tenantName = result.TenantName,
                adminEmail = result.AdminEmail,
                adminPassword = result.AdminPassword,
                adminClientId = result.AdminClientId,
                webClientId = result.WebClientId,
                loginUrl = result.LoginUrl,
                adminUrl = result.AdminUrl
            });
        }).WithName("SeedTenant");

        // Credential migration endpoints (platform-admin only)
#pragma warning disable CS0618
        platformAdmin.MapGet("/migrate-credentials/status", async (
            MrWhoOidc.Auth.Services.IPasswordMigrationService migrationService,
            CancellationToken ct) =>
        {
            var status = await migrationService.GetMigrationStatusAsync(ct);
            return Results.Ok(new
            {
                totalAccounts = status.TotalAccounts,
                migratedAccounts = status.MigratedAccounts,
                pendingAccounts = status.PendingAccounts,
                percentComplete = Math.Round(status.PercentComplete, 2)
            });
        }).WithName("GetMigrationStatus");

        platformAdmin.MapPost("/migrate-credentials", async (
            MrWhoOidc.Auth.Services.IPasswordMigrationService migrationService,
            MigrateBatchRequest? request,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("PasswordMigration");
            var batchSize = request?.BatchSize ?? 100;
            if (batchSize < 1 || batchSize > 1000)
            {
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "BatchSize must be between 1 and 1000");
            }

            logger.LogInformation("🔄 [Migration] Starting batch migration with size {BatchSize}", batchSize);

            var result = await migrationService.MigrateBatchAsync(batchSize, ct);

            logger.LogInformation(
                "✅ [Migration] Batch complete: Processed={Processed}, Success={Success}, Failed={Failed}, Skipped={Skipped}, Duration={Duration}ms",
                result.ProcessedCount, result.SuccessCount, result.FailureCount, result.SkippedCount, result.Duration.TotalMilliseconds);

            return Results.Ok(new
            {
                processedCount = result.ProcessedCount,
                successCount = result.SuccessCount,
                failureCount = result.FailureCount,
                skippedCount = result.SkippedCount,
                durationMs = (int)result.Duration.TotalMilliseconds
            });
        }).WithName("MigrateBatch");

        platformAdmin.MapPost("/migrate-credentials/{accountId:guid}", async (
            Guid accountId,
            MrWhoOidc.Auth.Services.IPasswordMigrationService migrationService,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("PasswordMigration");
            logger.LogInformation("🔄 [Migration] Migrating single account {AccountId}", accountId);

            var result = await migrationService.MigrateUserCredentialsAsync(accountId, ct);

            if (!result.Success)
            {
                logger.LogWarning("❌ [Migration] Failed for account {AccountId}: {Message}", accountId, result.Message);
                return Results.Problem(statusCode: 400, title: "Migration failed", detail: result.Message);
            }

            logger.LogInformation(
                "✅ [Migration] Account {AccountId} migrated. Skipped={Skipped}, Tenants={Tenants}",
                accountId, result.Skipped, result.AffectedTenants);

            return Results.Ok(new
            {
                success = true,
                skipped = result.Skipped,
                affectedTenants = result.AffectedTenants,
                message = result.Message
            });
        }).WithName("MigrateSingleAccount");
#pragma warning restore CS0618
    }

    private static void MapTenantResourceListEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/clients", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return Results.Problem(statusCode: 403, title: "No tenant context");
            }

            var rows = await db.Clients.AsNoTracking()
                .Where(c => c.TenantId == currentTenantId.Value)
                .Join(db.Tenants.AsNoTracking(), c => c.TenantId, t => t.Id, (c, t) => new { Client = c, Tenant = t })
                .Join(db.Realms.AsNoTracking(), x => x.Client.RealmId, r => r.Id, (x, r) => new
                {
                    x.Client.Id,
                    x.Client.ClientId,
                    x.Client.ClientName,
                    x.Client.RequirePkce,
                    x.Client.RequireConsent,
                    x.Client.RequirePar,
                    x.Client.IsSystemClient,
                    x.Client.GrantTypesJson,
                    x.Client.Scope,
                    x.Client.TenantId,
                    TenantSlug = x.Tenant.Slug,
                    TenantName = x.Tenant.Name,
                    RealmId = r.Id,
                    RealmName = r.Name,
                    HasJwks = !string.IsNullOrEmpty(x.Client.PublicJwksJson) || !string.IsNullOrEmpty(x.Client.PublicJwksUri)
                })
                .OrderBy(x => x.ClientId)
                .ToListAsync(ct);

            return Results.Ok(rows.Select(row => new
            {
                row.Id,
                row.ClientId,
                row.ClientName,
                row.TenantId,
                row.TenantSlug,
                row.TenantName,
                row.RealmId,
                row.RealmName,
                row.RequirePkce,
                row.RequireConsent,
                row.RequirePar,
                row.HasJwks,
                row.IsSystemClient,
                GrantTypes = ParseJsonArray(row.GrantTypesJson),
                Scopes = ParseScopeList(row.Scope)
            }));
        });

        group.MapGet("/scopes", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return Results.Problem(statusCode: 403, title: "No tenant context");
            }

            var rows = await db.Scopes.AsNoTracking()
                .Where(scope => scope.IsGlobal || scope.TenantId == currentTenantId.Value)
                .OrderBy(scope => scope.IsGlobal ? 0 : 1)
                .ThenBy(scope => scope.Name)
                .GroupJoin(
                    db.Tenants.AsNoTracking(),
                    scope => scope.TenantId,
                    tenant => (Guid?)tenant.Id,
                    (scope, tenants) => new
                    {
                        scope.Name,
                        scope.Description,
                        scope.IsExposed,
                        scope.IsGlobal,
                        scope.TenantId,
                        TenantSlug = tenants.Select(t => t.Slug).FirstOrDefault(),
                        TenantName = tenants.Select(t => t.Name).FirstOrDefault()
                    })
                .ToListAsync(ct);

            return Results.Ok(rows);
        });
    }

    // ── Realm CRUD ──────────────────────────────────────────────────────────

    private static void MapRealmEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/realms", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var list = await db.Realms.AsNoTracking()
                .Where(r => r.TenantId == currentTenantId.Value)
                .OrderBy(r => r.Name)
                .Select(r => new { r.Id, r.Name, r.DisplayName, r.AllowUnconfirmedLogin, r.CreatedAt })
                .ToListAsync(ct);
            return Results.Ok(list);
        });

        admin.MapGet("/realms/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var realm = await db.Realms.AsNoTracking()
                .Where(r => r.Id == id && r.TenantId == currentTenantId.Value)
                .Select(r => new { r.Id, r.Name, r.DisplayName, r.AllowUnconfirmedLogin, r.CreatedAt })
                .FirstOrDefaultAsync(ct);
            return realm is null
                ? Results.Problem(statusCode: 404, title: "Not Found")
                : Results.Ok(realm);
        });

        admin.MapPost("/realms", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            RealmInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "name is required");
            var nameVal = input.Name.Trim();
            var exists = await db.Realms.AnyAsync(r => r.TenantId == currentTenantId.Value && r.Name == nameVal, ct);
            if (exists)
                return Results.Problem(statusCode: 409, title: "Conflict", detail: "A realm with that name already exists");
            var realm = new Realm
            {
                TenantId = currentTenantId.Value,
                Name = nameVal,
                DisplayName = input.DisplayName?.Trim(),
                AllowUnconfirmedLogin = input.AllowUnconfirmedLogin ?? true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Realms.Add(realm);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/admin/api/realms/{realm.Id}", new { realm.Id, realm.Name, realm.DisplayName, realm.AllowUnconfirmedLogin });
        });

        admin.MapPut("/realms/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            RealmInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value, ct);
            if (realm is null)
                return Results.Problem(statusCode: 404, title: "Not Found");
            if (!string.IsNullOrWhiteSpace(input.Name))
            {
                var nameVal = input.Name.Trim();
                var conflict = await db.Realms.AnyAsync(r => r.TenantId == currentTenantId.Value && r.Name == nameVal && r.Id != id, ct);
                if (conflict)
                    return Results.Problem(statusCode: 409, title: "Conflict", detail: "A realm with that name already exists");
                realm.Name = nameVal;
            }
            if (input.DisplayName is not null) realm.DisplayName = input.DisplayName.Trim();
            if (input.AllowUnconfirmedLogin.HasValue) realm.AllowUnconfirmedLogin = input.AllowUnconfirmedLogin.Value;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapDelete("/realms/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value, ct);
            if (realm is null)
                return Results.Problem(statusCode: 404, title: "Not Found");
            var hasClients = await db.Clients.AnyAsync(c => c.RealmId == id, ct);
            if (hasClients)
                return Results.Problem(statusCode: 409, title: "Conflict", detail: "Realm has associated clients. Delete all clients in this realm first.");
            db.Realms.Remove(realm);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── Client single-GET, create, delete ────────────────────────────────────

    private static void MapClientMutationEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/clients/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var client = await db.Clients.AsNoTracking()
                .Where(c => c.Id == id && c.TenantId == currentTenantId.Value)
                .Join(db.Realms.AsNoTracking(), c => c.RealmId, r => r.Id, (c, r) => new
                {
                    c.Id,
                    c.ClientId,
                    c.ClientName,
                    c.RealmId,
                    RealmName = r.Name,
                    c.RequirePkce,
                    c.RequireConsent,
                    c.RequirePar,
                    c.AutoApprovalMode,
                    c.IsSystemClient,
                    c.Scope,
                    c.GrantTypesJson,
                    c.AllowedLoginRedirectUrisJson,
                    c.AllowedLogoutRedirectUrisJson,
                    c.TokenEndpointAuthMethod
                })
                .FirstOrDefaultAsync(ct);
            return client is null
                ? Results.Problem(statusCode: 404, title: "Not Found")
                : Results.Ok(client);
        });

        admin.MapPost("/clients", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IClientStore clientStore,
            CreateClientInput input,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            if (string.IsNullOrWhiteSpace(input.ClientId))
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "clientId is required");
            if (string.IsNullOrWhiteSpace(input.ClientName))
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "clientName is required");
            if (input.RealmId == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "realmId is required");
            var realm = await db.Realms.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == input.RealmId && r.TenantId == currentTenantId.Value, ct);
            if (realm is null)
                return Results.Problem(statusCode: 404, title: "Realm not found or does not belong to this tenant");
            var clientIdVal = input.ClientId.Trim();
            var exists = await db.Clients.AnyAsync(c => c.ClientId == clientIdVal, ct);
            if (exists)
                return Results.Problem(statusCode: 409, title: "Conflict", detail: "A client with that clientId already exists");
            var client = new Client
            {
                TenantId = currentTenantId.Value,
                ClientId = clientIdVal,
                ClientName = input.ClientName.Trim(),
                RealmId = input.RealmId,
                RequirePkce = input.RequirePkce ?? true,
                RequireConsent = input.RequireConsent ?? true,
                AutoApprovalMode = input.AutoApprovalMode ?? AutoApprovalMode.No,
                Scope = input.Scope,
                GrantTypesJson = input.GrantTypes is { Count: > 0 }
                    ? JsonSerializer.Serialize(input.GrantTypes)
                    : null,
                AllowedLoginRedirectUrisJson = input.AllowedLoginRedirectUris is { Count: > 0 }
                    ? JsonSerializer.Serialize(input.AllowedLoginRedirectUris)
                    : null,
                AllowedLogoutRedirectUrisJson = input.AllowedLogoutRedirectUris is { Count: > 0 }
                    ? JsonSerializer.Serialize(input.AllowedLogoutRedirectUris)
                    : null
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync(ct);

            string? generatedSecret = null;
            if (input.CreateInitialSecret == true)
            {
                generatedSecret = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                var username = httpContext.User.Identity?.Name ?? "system";
                var secret = await clientStore.CreateSecretAsync(
                    client.Id, generatedSecret, "Initial secret", username, null, ct);
                await clientStore.ActivateSecretAsync(secret.Id, username, ct);
            }

            return Results.Created($"/admin/api/clients/{client.Id}", new
            {
                client.Id,
                client.ClientId,
                client.ClientName,
                client.RealmId,
                client.AutoApprovalMode,
                InitialSecret = generatedSecret,
                Warning = generatedSecret != null ? "Save this secret now. It will not be shown again." : (string?)null
            });
        });

        admin.MapDelete("/clients/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == currentTenantId.Value, ct);
            if (client is null)
                return Results.Problem(statusCode: 404, title: "Not Found");
            if (client.IsSystemClient)
                return Results.Problem(statusCode: 403, title: "System clients cannot be deleted");
            db.Clients.Remove(client);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── Scope create, update, delete ─────────────────────────────────────────

    private static void MapScopeMutationEndpoints(RouteGroupBuilder admin)
    {
        admin.MapPost("/scopes", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            ScopeInput input,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "name is required");
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var nameVal = input.Name.Trim();
            var exists = await db.Scopes.AnyAsync(s => s.Name == nameVal && s.TenantId == currentTenantId.Value, ct);
            if (exists)
                return Results.Problem(statusCode: 409, title: "Conflict", detail: "A scope with that name already exists in this tenant");
            var scope = new Scope
            {
                Name = nameVal,
                TenantId = currentTenantId.Value,
                Description = input.Description?.Trim(),
                IsExposed = input.IsExposed ?? true,
                IsGlobal = false
            };
            db.Scopes.Add(scope);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/admin/api/scopes/{Uri.EscapeDataString(scope.Name)}", new { scope.Name });
        });

        admin.MapPut("/scopes/{name}", async (
            string name,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            ScopeInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var scope = await db.Scopes.FirstOrDefaultAsync(
                s => s.Name == name && s.TenantId == currentTenantId.Value && !s.IsGlobal, ct);
            if (scope is null)
                return Results.Problem(statusCode: 404, title: "Not Found or scope is global (not modifiable via this endpoint)");
            if (input.Description is not null) scope.Description = input.Description.Trim();
            if (input.IsExposed.HasValue) scope.IsExposed = input.IsExposed.Value;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapDelete("/scopes/{name}", async (
            string name,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var scope = await db.Scopes.FirstOrDefaultAsync(
                s => s.Name == name && s.TenantId == currentTenantId.Value && !s.IsGlobal, ct);
            if (scope is null)
                return Results.Problem(statusCode: 404, title: "Not Found or scope is global (not deletable via this endpoint)");
            db.Scopes.Remove(scope);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── User admin: list, get, create, delete ────────────────────────────────

    private static void MapUserAdminEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/users", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            [FromQuery] string? search,
            [FromQuery] int? skip,
            [FromQuery] int? take,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var query = db.Users.AsNoTracking().Where(u => u.TenantId == currentTenantId.Value);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(u => u.Username.Contains(term)
                    || (u.Email != null && u.Email.Contains(term))
                    || (u.Name != null && u.Name.Contains(term)));
            }
            var total = await query.CountAsync(ct);
            var users = await query
                .OrderBy(u => u.Username)
                .Skip(skip ?? 0)
                .Take(Math.Clamp(take ?? 50, 1, 500))
                .Select(u => new { u.Id, u.Username, u.Email, u.EmailVerified, u.Name, u.TotpEnabled, u.CreatedAt })
                .ToListAsync(ct);
            return Results.Ok(new { total, items = users });
        });

        admin.MapGet("/users/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Id == id && u.TenantId == currentTenantId.Value)
                .Select(u => new { u.Id, u.Username, u.Email, u.EmailVerified, u.Name, u.TotpEnabled, u.CreatedAt })
                .FirstOrDefaultAsync(ct);
            return user is null
                ? Results.Problem(statusCode: 404, title: "Not Found")
                : Results.Ok(user);
        });

        admin.MapPost("/users", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IPasswordHasher hasher,
            IUserAccountProvisioner accountProvisioner,
            CreateUserInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            if (string.IsNullOrWhiteSpace(input.Username))
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "username is required");
            var usernameVal = input.Username.Trim();
            var usernameExists = await db.Users.AnyAsync(
                u => u.TenantId == currentTenantId.Value && u.Username == usernameVal, ct);
            if (usernameExists)
                return Results.Problem(statusCode: 409, title: "Conflict", detail: "A user with that username already exists in this tenant");

            var password = string.IsNullOrWhiteSpace(input.Password)
                ? GenerateSecurePassword()
                : input.Password;

            var user = new User
            {
                TenantId = currentTenantId.Value,
                Username = usernameVal,
                Email = input.Email?.Trim(),
                NormalizedEmail = input.Email?.Trim().ToLowerInvariant(),
                Name = input.Name?.Trim(),
                EmailVerified = false,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            await accountProvisioner.EnsureAsync(user, currentTenantId.Value, null, false, ct, autoSave: false);

            var account = db.UserAccounts.Local.FirstOrDefault(a => a.Id == user.Id)
                ?? await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == user.Id, ct);
            if (account is null)
                return Results.Problem(statusCode: 500, title: "User account provisioning failed");

            account.PasswordHash = hasher.Hash(password);
            account.PasswordUpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Created($"/admin/api/users/{user.Id}", new
            {
                user.Id,
                user.Username,
                user.Email,
                user.Name,
                Password = password,
                Warning = "Save this password now. It will not be shown again."
            });
        });

        admin.MapDelete("/users/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.TenantId == currentTenantId.Value, ct);
            if (user is null)
                return Results.Problem(statusCode: 404, title: "Not Found");
            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static string GenerateSecurePassword()
    {
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string digits = "0123456789";
        const string special = "!@#$%^&*";
        var all = upper + lower + digits + special;
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var chars = new char[16];
        for (int i = 0; i < chars.Length; i++) chars[i] = all[bytes[i] % all.Length];
        // Ensure one of each character class
        chars[0] = upper[bytes[0] % upper.Length];
        chars[1] = lower[bytes[1] % lower.Length];
        chars[2] = digits[bytes[2] % digits.Length];
        chars[3] = special[bytes[3] % special.Length];
        return new string(chars);
    }

    // ── Tenant invitation management ────────────────────────────────────────

    private static void MapInvitationEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/invitations", async (
            ITenantAccessor tenantAccessor,
            ITenantEnrollmentService tenantEnrollment,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");

            var invitations = await tenantEnrollment.ListInvitationsAsync(currentTenantId.Value, ct).ConfigureAwait(false);
            return Results.Ok(invitations.Select(ToInvitationDto));
        });

        admin.MapPost("/invitations", async (
            ITenantAccessor tenantAccessor,
            ITenantEnrollmentService tenantEnrollment,
            HttpContext httpContext,
            CreateInvitationInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");

            var validDays = input.ValidDays ?? 7;
            if (validDays is < 1 or > 90)
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "validDays must be between 1 and 90.");

            try
            {
                var result = await tenantEnrollment.CreateInvitationAsync(
                    currentTenantId.Value,
                    input.Email ?? string.Empty,
                    input.DisplayName,
                    input.IsTenantAdmin,
                    TimeSpan.FromDays(validDays),
                    GetCurrentUserId(httpContext.User),
                    httpContext.User.Identity?.Name,
                    ct).ConfigureAwait(false);

                var invitation = result.Invitation;
                var dto = ToInvitationDto(new TenantInvitationListItem(
                    invitation.Id,
                    invitation.Email,
                    invitation.DisplayName,
                    invitation.Status,
                    invitation.IsTenantAdmin,
                    invitation.CreatedAt,
                    invitation.ExpiresAt,
                    invitation.AcceptedAt,
                    invitation.RevokedAt,
                    invitation.InvitedByUsername));

                return Results.Created($"/admin/api/invitations/{invitation.Id}", new
                {
                    invitation = dto,
                    token = result.Token,
                    invitationLink = BuildInvitationLink(httpContext, result.Token)
                });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DbUpdateException)
            {
                return Results.Problem(statusCode: 400, title: "Invitation could not be created", detail: ex.Message);
            }
        });

        admin.MapDelete("/invitations/{id:guid}", async (
            Guid id,
            ITenantAccessor tenantAccessor,
            ITenantEnrollmentService tenantEnrollment,
            HttpContext httpContext,
            [FromQuery] string? reason,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");

            var revoked = await tenantEnrollment.RevokeInvitationAsync(
                currentTenantId.Value,
                id,
                GetCurrentUserId(httpContext.User),
                string.IsNullOrWhiteSpace(reason) ? "Revoked by CLI or admin API" : reason.Trim(),
                ct).ConfigureAwait(false);

            return revoked
                ? Results.NoContent()
                : Results.Problem(statusCode: 404, title: "Not Found", detail: "Invitation was not found or is no longer pending.");
        });
    }

    private static object ToInvitationDto(TenantInvitationListItem invitation) => new
    {
        invitation.Id,
        invitation.Email,
        invitation.DisplayName,
        Status = invitation.Status.ToString(),
        invitation.IsTenantAdmin,
        invitation.CreatedAt,
        invitation.ExpiresAt,
        invitation.AcceptedAt,
        invitation.RevokedAt,
        invitation.InvitedByUsername
    };

    private static string BuildInvitationLink(HttpContext httpContext, string token)
    {
        var request = httpContext.Request;
        return $"{request.Scheme}://{request.Host}/invitations/{Uri.EscapeDataString(token)}";
    }

    private static Guid? GetCurrentUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    // ── Client update ────────────────────────────────────────────────────────

    private static void MapClientUpdateEndpoints(RouteGroupBuilder admin)
    {
        admin.MapPut("/clients/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            UpdateClientInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == currentTenantId.Value, ct);
            if (client is null)
                return Results.Problem(statusCode: 404, title: "Not Found");
            if (client.IsSystemClient)
                return Results.Problem(statusCode: 403, title: "System clients cannot be modified");

            if (input.ClientName is not null) client.ClientName = input.ClientName.Trim();
            if (input.RequirePkce.HasValue) client.RequirePkce = input.RequirePkce.Value;
            if (input.RequireConsent.HasValue) client.RequireConsent = input.RequireConsent.Value;
            if (input.RequirePar.HasValue) client.RequirePar = input.RequirePar.Value;
            if (input.AutoApprovalMode.HasValue) client.AutoApprovalMode = input.AutoApprovalMode.Value;
            if (input.Scope is not null) client.Scope = input.Scope.Trim();
            if (input.GrantTypes is not null)
                client.GrantTypesJson = input.GrantTypes.Count > 0 ? JsonSerializer.Serialize(input.GrantTypes) : null;
            if (input.AllowedLoginRedirectUris is not null)
                client.AllowedLoginRedirectUrisJson = input.AllowedLoginRedirectUris.Count > 0 ? JsonSerializer.Serialize(input.AllowedLoginRedirectUris) : null;
            if (input.AllowedLogoutRedirectUris is not null)
                client.AllowedLogoutRedirectUrisJson = input.AllowedLogoutRedirectUris.Count > 0 ? JsonSerializer.Serialize(input.AllowedLogoutRedirectUris) : null;
            if (input.BackChannelLogoutUri is not null) client.BackChannelLogoutUri = input.BackChannelLogoutUri.Trim();
            if (input.FrontChannelLogoutUri is not null) client.FrontChannelLogoutUri = input.FrontChannelLogoutUri.Trim();
            if (input.TokenEndpointAuthMethod is not null) client.TokenEndpointAuthMethod = input.TokenEndpointAuthMethod.Trim();
            if (input.OboEnabled.HasValue) client.OboEnabled = input.OboEnabled.Value;
            if (input.AllowLocalLogin.HasValue) client.AllowLocalLogin = input.AllowLocalLogin.Value;
            if (input.AllowExternalIdp.HasValue) client.AllowExternalIdp = input.AllowExternalIdp.Value;

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── User update ──────────────────────────────────────────────────────────

    private static void MapUserUpdateEndpoints(RouteGroupBuilder admin)
    {
        admin.MapPut("/users/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            UpdateUserInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.TenantId == currentTenantId.Value, ct);
            if (user is null)
                return Results.Problem(statusCode: 404, title: "Not Found");

            if (input.Name is not null) user.Name = input.Name.Trim();
            if (input.Email is not null)
            {
                var trimmedEmail = input.Email.Trim();
                if (!trimmedEmail.Contains('@') || trimmedEmail.Length < 3)
                    return Results.Problem(statusCode: 400, title: "Validation failed", detail: "Invalid email format");
                user.Email = trimmedEmail;
                user.NormalizedEmail = trimmedEmail.ToLowerInvariant();
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── Role CRUD ────────────────────────────────────────────────────────────

    private static void MapRoleEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/roles", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            [FromQuery] Guid? realmId,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var query = db.Roles.AsNoTracking().Where(r => r.TenantId == currentTenantId.Value);
            if (realmId.HasValue)
                query = query.Where(r => r.RealmId == realmId.Value);
            var roles = await query.OrderBy(r => r.Name)
                .Select(r => new { r.Id, r.Name, r.RealmId, r.IsActive })
                .ToListAsync(ct);
            return Results.Ok(roles);
        });

        admin.MapGet("/roles/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var role = await db.Roles.AsNoTracking()
                .Where(r => r.Id == id && r.TenantId == currentTenantId.Value)
                .Select(r => new { r.Id, r.Name, r.RealmId, r.IsActive })
                .FirstOrDefaultAsync(ct);
            return role is null
                ? Results.Problem(statusCode: 404, title: "Not Found")
                : Results.Ok(role);
        });

        admin.MapPost("/roles", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            RoleInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "name is required");
            if (!input.RealmId.HasValue || input.RealmId.Value == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "realmId is required");
            var realm = await db.Realms.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == input.RealmId.Value && r.TenantId == currentTenantId.Value, ct);
            if (realm is null)
                return Results.Problem(statusCode: 404, title: "Realm not found or does not belong to this tenant");
            var nameVal = input.Name.Trim();
            var exists = await db.Roles.AnyAsync(r => r.Name == nameVal && r.RealmId == input.RealmId.Value && r.TenantId == currentTenantId.Value, ct);
            if (exists)
                return Results.Problem(statusCode: 409, title: "Conflict", detail: "A role with that name already exists in this realm");
            var role = new Role
            {
                TenantId = currentTenantId.Value,
                Name = nameVal,
                RealmId = input.RealmId.Value,
                IsActive = true
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/admin/api/roles/{role.Id}", new { role.Id, role.Name, role.RealmId });
        });

        admin.MapPut("/roles/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            RoleInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value, ct);
            if (role is null)
                return Results.Problem(statusCode: 404, title: "Not Found");
            if (input.Name is not null) role.Name = input.Name.Trim();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapDelete("/roles/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value, ct);
            if (role is null)
                return Results.Problem(statusCode: 404, title: "Not Found");
            var hasAssignments = await db.UserRealmRoleAssignments.AnyAsync(a => a.RoleId == id, ct)
                || await db.UserClientRoleAssignments.AnyAsync(a => a.RoleId == id, ct);
            if (hasAssignments)
                return Results.Problem(statusCode: 409, title: "Conflict", detail: "Cannot delete role: it is still assigned to one or more users. Remove assignments first.");
            db.Roles.Remove(role);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── User ↔ Role assignments ──────────────────────────────────────────────

    private static void MapUserRoleEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/users/{userId:guid}/roles", async (
            Guid userId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == currentTenantId.Value, ct);
            if (user is null)
                return Results.Problem(statusCode: 404, title: "User not found");

            var realmRoles = await db.UserRealmRoleAssignments.AsNoTracking()
                .Where(a => a.UserId == userId)
                .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new
                {
                    r.Id,
                    r.Name,
                    r.RealmId,
                    a.IsActive,
                    Scope = "realm"
                })
                .ToListAsync(ct);

            var clientRoles = await db.UserClientRoleAssignments.AsNoTracking()
                .Where(a => a.UserId == userId)
                .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new
                {
                    r.Id,
                    r.Name,
                    ClientId = a.ClientId,
                    a.IsActive,
                    Scope = "client"
                })
                .ToListAsync(ct);

            return Results.Ok(new { realmRoles, clientRoles });
        });

        admin.MapPost("/users/{userId:guid}/roles", async (
            Guid userId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            UserRoleAssignInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == currentTenantId.Value, ct);
            if (user is null)
                return Results.Problem(statusCode: 404, title: "User not found");
            var role = await db.Roles.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == input.RoleId && r.TenantId == currentTenantId.Value, ct);
            if (role is null)
                return Results.Problem(statusCode: 404, title: "Role not found");

            var exists = await db.UserRealmRoleAssignments.AnyAsync(
                a => a.UserId == userId && a.RoleId == input.RoleId && a.RealmId == role.RealmId, ct);
            if (exists)
                return Results.Problem(statusCode: 409, title: "Conflict", detail: "Role already assigned to this user");

            db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment
            {
                UserId = userId,
                RoleId = input.RoleId,
                RealmId = role.RealmId,
                IsActive = true
            });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/admin/api/users/{userId}/roles", new { userId, roleId = input.RoleId, role.RealmId });
        });

        admin.MapDelete("/users/{userId:guid}/roles/{roleId:guid}", async (
            Guid userId,
            Guid roleId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == currentTenantId.Value, ct);
            if (user is null)
                return Results.Problem(statusCode: 404, title: "User not found");
            var assignment = await db.UserRealmRoleAssignments
                .FirstOrDefaultAsync(a => a.UserId == userId && a.RoleId == roleId, ct);
            if (assignment is null)
                return Results.Problem(statusCode: 404, title: "Role assignment not found");
            db.UserRealmRoleAssignments.Remove(assignment);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── Client ↔ Scope management ────────────────────────────────────────────

    private static void MapClientScopeEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/clients/{clientId:guid}/scopes", async (
            Guid clientId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var client = await db.Clients.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value, ct);
            if (client is null)
                return Results.Problem(statusCode: 404, title: "Client not found");
            var scopes = await db.ClientScopes.AsNoTracking()
                .Where(cs => cs.ClientId == clientId)
                .Select(cs => new { cs.ScopeName })
                .ToListAsync(ct);
            return Results.Ok(scopes);
        });

        admin.MapPost("/clients/{clientId:guid}/scopes", async (
            Guid clientId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            ClientScopeAssignInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            if (string.IsNullOrWhiteSpace(input.ScopeName))
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "scopeName is required");
            var client = await db.Clients.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value, ct);
            if (client is null)
                return Results.Problem(statusCode: 404, title: "Client not found");
            var scopeVal = input.ScopeName.Trim();
            var exists = await db.ClientScopes.AnyAsync(cs => cs.ClientId == clientId && cs.ScopeName == scopeVal, ct);
            if (exists)
                return Results.Problem(statusCode: 409, title: "Conflict", detail: "Scope already assigned to this client");
            db.ClientScopes.Add(new ClientScope { ClientId = clientId, ScopeName = scopeVal });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/admin/api/clients/{clientId}/scopes", new { clientId, scopeName = scopeVal });
        });

        admin.MapDelete("/clients/{clientId:guid}/scopes/{scopeName}", async (
            Guid clientId,
            string scopeName,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            var client = await db.Clients.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value, ct);
            if (client is null)
                return Results.Problem(statusCode: 404, title: "Client not found");
            var assignment = await db.ClientScopes
                .FirstOrDefaultAsync(cs => cs.ClientId == clientId && cs.ScopeName == scopeName, ct);
            if (assignment is null)
                return Results.Problem(statusCode: 404, title: "Scope assignment not found");
            db.ClientScopes.Remove(assignment);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── User ↔ Client assignments ───────────────────────────────────────────

    private static void MapUserClientEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/users/{userId:guid}/clients", async (
            Guid userId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");

            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == currentTenantId.Value, ct);
            if (user is null)
                return Results.Problem(statusCode: 404, title: "User not found");

            var assignments = await db.UserClientAssignments.AsNoTracking()
                .Where(a => a.UserId == userId)
                .Join(
                    db.Clients.AsNoTracking().Where(c => c.TenantId == currentTenantId.Value),
                    a => a.ClientId,
                    c => c.Id,
                    (a, c) => new { a, c })
                .Join(
                    db.Realms.AsNoTracking(),
                    ac => ac.c.RealmId,
                    r => r.Id,
                    (ac, r) => new
                    {
                        ac.c.Id,
                        ac.c.ClientId,
                        ac.c.ClientName,
                        ac.c.RealmId,
                        RealmName = r.Name,
                        ac.a.IsActive
                    })
                .ToListAsync(ct);

            return Results.Ok(assignments);
        });

        admin.MapPost("/users/{userId:guid}/clients", async (
            Guid userId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            UserClientAssignInput input,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");
            if (input.ClientId == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "clientId is required");

            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == currentTenantId.Value, ct);
            if (user is null)
                return Results.Problem(statusCode: 404, title: "User not found");

            var client = await db.Clients.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == input.ClientId && c.TenantId == currentTenantId.Value, ct);
            if (client is null)
                return Results.Problem(statusCode: 404, title: "Client not found");

            var existing = await db.UserClientAssignments
                .FirstOrDefaultAsync(a => a.UserId == userId && a.ClientId == client.Id && a.RealmId == client.RealmId, ct);

            if (existing is not null)
            {
                if (!existing.IsActive)
                {
                    existing.IsActive = true;
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(new { userId, clientId = client.Id, reactivated = true });
                }

                return Results.Problem(statusCode: 409, title: "Conflict", detail: "Client already assigned to this user");
            }

            db.UserClientAssignments.Add(new UserClientAssignment
            {
                UserId = userId,
                ClientId = client.Id,
                RealmId = client.RealmId,
                IsActive = true
            });

            await db.SaveChangesAsync(ct);
            return Results.Created($"/admin/api/users/{userId}/clients/{client.Id}", new { userId, clientId = client.Id });
        });

        admin.MapDelete("/users/{userId:guid}/clients/{clientId:guid}", async (
            Guid userId,
            Guid clientId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");

            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == currentTenantId.Value, ct);
            if (user is null)
                return Results.Problem(statusCode: 404, title: "User not found");

            var client = await db.Clients.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value, ct);
            if (client is null)
                return Results.Problem(statusCode: 404, title: "Client not found");

            var assignment = await db.UserClientAssignments
                .FirstOrDefaultAsync(a => a.UserId == userId && a.ClientId == client.Id && a.RealmId == client.RealmId, ct);
            if (assignment is null)
                return Results.Problem(statusCode: 404, title: "Client assignment not found");

            db.UserClientAssignments.Remove(assignment);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── Tenant CRUD (platform-admin) ─────────────────────────────────────────

    private static void MapTenantCrudEndpoints(RouteGroupBuilder platformAdmin)
    {
        platformAdmin.MapGet("/tenants/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            CancellationToken ct) =>
        {
            var tenant = await db.Tenants.AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => new
                {
                    t.Id,
                    t.Slug,
                    t.Name,
                    t.Description,
                    t.IssuerUri,
                    Status = t.Status.ToString(),
                    t.AdminEmail,
                    t.MaxUsers,
                    t.MaxClients,
                    t.CreatedAt,
                    UserCount = db.Users.Count(u => u.TenantId == t.Id),
                    ClientCount = db.Clients.Count(c => c.TenantId == t.Id)
                })
                .FirstOrDefaultAsync(ct);
            return tenant is null
                ? Results.Problem(statusCode: 404, title: "Tenant not found")
                : Results.Ok(tenant);
        });

        platformAdmin.MapPut("/tenants/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            UpdateTenantInput input,
            CancellationToken ct) =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tenant is null)
                return Results.Problem(statusCode: 404, title: "Tenant not found");
            if (input.Name is not null) tenant.Name = input.Name.Trim();
            if (input.Description is not null) tenant.Description = input.Description.Trim();
            if (input.AdminEmail is not null) tenant.AdminEmail = input.AdminEmail.Trim();
            if (input.MaxUsers.HasValue) tenant.MaxUsers = input.MaxUsers.Value;
            if (input.MaxClients.HasValue) tenant.MaxClients = input.MaxClients.Value;
            if (input.Status is not null)
            {
                if (!Enum.TryParse<TenantStatus>(input.Status, true, out var status))
                    return Results.Problem(statusCode: 400, title: "Validation failed", detail: $"Invalid status '{input.Status}'. Valid values: {string.Join(", ", Enum.GetNames<TenantStatus>())}");
                tenant.Status = status;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        platformAdmin.MapDelete("/tenants/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            CancellationToken ct) =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tenant is null)
                return Results.Problem(statusCode: 404, title: "Tenant not found");
            tenant.Status = TenantStatus.Deleted;
            tenant.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static void MapPlatformResourceListEndpoints(RouteGroupBuilder platformAdmin)
    {
        platformAdmin.MapGet("/tenants", async (
            AuthDbContext db,
            string? search,
            CancellationToken ct) =>
        {
            var query = db.Tenants.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(t =>
                    t.Slug.Contains(term) ||
                    t.Name.Contains(term) ||
                    (t.Description != null && t.Description.Contains(term)));
            }

            var tenants = await query
                .OrderBy(t => t.CreatedAt)
                .Select(t => new
                {
                    t.Id,
                    t.Slug,
                    t.Name,
                    t.Description,
                    t.IssuerUri,
                    t.Status,
                    t.MaxUsers,
                    t.MaxClients,
                    t.AdminEmail,
                    t.CreatedAt,
                    UserCount = db.Users.Count(u => u.TenantId == t.Id),
                    ClientCount = db.Clients.Count(c => c.TenantId == t.Id)
                })
                .ToListAsync(ct);

            return Results.Ok(tenants.Select(t => new
            {
                t.Id,
                t.Slug,
                t.Name,
                t.Description,
                t.IssuerUri,
                Status = t.Status.ToString(),
                t.MaxUsers,
                t.MaxClients,
                t.AdminEmail,
                t.CreatedAt,
                t.UserCount,
                t.ClientCount
            }));
        }).WithName("PlatformAdminListTenants");

        platformAdmin.MapGet("/clients", async (
            AuthDbContext db,
            string? tenant,
            CancellationToken ct) =>
        {
            var query = db.Clients.AsNoTracking()
                .Join(db.Tenants.AsNoTracking(), c => c.TenantId, t => t.Id, (c, t) => new { Client = c, Tenant = t })
                .Join(db.Realms.AsNoTracking(), x => x.Client.RealmId, r => r.Id, (x, r) => new
                {
                    x.Client.Id,
                    x.Client.ClientId,
                    x.Client.ClientName,
                    x.Client.RequirePkce,
                    x.Client.RequireConsent,
                    x.Client.RequirePar,
                    x.Client.IsSystemClient,
                    x.Client.GrantTypesJson,
                    x.Client.Scope,
                    x.Client.TenantId,
                    TenantSlug = x.Tenant.Slug,
                    TenantName = x.Tenant.Name,
                    RealmId = r.Id,
                    RealmName = r.Name,
                    HasJwks = !string.IsNullOrEmpty(x.Client.PublicJwksJson) || !string.IsNullOrEmpty(x.Client.PublicJwksUri)
                })
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(tenant))
            {
                var tenantSlug = tenant.Trim();
                query = query.Where(row => row.TenantSlug == tenantSlug);
            }

            var rows = await query
                .OrderBy(row => row.TenantSlug)
                .ThenBy(row => row.ClientId)
                .ToListAsync(ct);

            return Results.Ok(rows.Select(row => new
            {
                row.Id,
                row.ClientId,
                row.ClientName,
                row.TenantId,
                row.TenantSlug,
                row.TenantName,
                row.RealmId,
                row.RealmName,
                row.RequirePkce,
                row.RequireConsent,
                row.RequirePar,
                row.HasJwks,
                row.IsSystemClient,
                GrantTypes = ParseJsonArray(row.GrantTypesJson),
                Scopes = ParseScopeList(row.Scope)
            }));
        }).WithName("PlatformAdminListClients");

        platformAdmin.MapGet("/scopes", async (
            AuthDbContext db,
            string? tenant,
            CancellationToken ct) =>
        {
            var query = db.Scopes.AsNoTracking()
                .GroupJoin(
                    db.Tenants.AsNoTracking(),
                    scope => scope.TenantId,
                    tenantEntity => (Guid?)tenantEntity.Id,
                    (scope, tenants) => new
                    {
                        scope.Name,
                        scope.Description,
                        scope.IsExposed,
                        scope.IsGlobal,
                        scope.TenantId,
                        TenantSlug = tenants.Select(t => t.Slug).FirstOrDefault(),
                        TenantName = tenants.Select(t => t.Name).FirstOrDefault()
                    })
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(tenant))
            {
                var tenantSlug = tenant.Trim();
                query = query.Where(scope => scope.IsGlobal || scope.TenantSlug == tenantSlug);
            }

            var rows = await query
                .OrderBy(scope => scope.IsGlobal ? 0 : 1)
                .ThenBy(scope => scope.TenantSlug)
                .ThenBy(scope => scope.Name)
                .ToListAsync(ct);

            return Results.Ok(rows);
        }).WithName("PlatformAdminListScopes");
    }

    private static string[] ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string[] ParseScopeList(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Array.Empty<string>();
        }

        return scope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Helper method to validate that the current user has access to a provider based on tenant filtering.
    /// Platform admins can access all providers; tenant admins can only access providers in their tenant.
    /// </summary>
    private static async Task<bool> ValidateProviderAccessAsync(
        Guid providerId,
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        // Check if user is platform admin
        var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
        if (platformAdminResult.Succeeded)
        {
            // Platform admins can access all providers - just verify it exists
            return await db.IdentityProviders.AsNoTracking().AnyAsync(p => p.Id == providerId, ct);
        }

        // For tenant admins, check if provider belongs to their tenant
        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return false; // No tenant context
        }

        return await db.IdentityProviders.AsNoTracking()
            .AnyAsync(p => p.Id == providerId && p.TenantId == currentTenantId.Value, ct);
    }

    // ===== CLIENT SECRETS MANAGEMENT =====

    private static void MapClientSecretsEndpoints(RouteGroupBuilder admin)
    {
        // GET /admin/api/clients/{clientId}/secrets - List all secrets for a client
        admin.MapGet("/clients/{clientId:guid}/secrets", async (
            Guid clientId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
            var isPlatformAdmin = platformAdminResult.Succeeded;

            // Verify client exists and user has access
            var clientQuery = db.Clients.AsNoTracking().Where(c => c.Id == clientId);
            if (!isPlatformAdmin)
            {
                var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
                if (!currentTenantId.HasValue)
                {
                    return Results.Problem(statusCode: 403, title: "No tenant context");
                }
                clientQuery = clientQuery.Where(c => c.TenantId == currentTenantId.Value);
            }

            var client = await clientQuery.FirstOrDefaultAsync(ct);
            if (client is null)
            {
                return Results.Problem(statusCode: 404, title: "Client not found");
            }
            if (client.IsSystemClient)
            {
                return Results.Problem(statusCode: 403, title: "System client is read-only");
            }

            // Get all secrets for this client
            var secrets = await db.ClientSecrets
                .AsNoTracking()
                .Where(s => s.ClientId == clientId)
                .OrderByDescending(s => s.CreatedAtUtc)
                .Select(s => new
                {
                    s.Id,
                    s.Description,
                    s.CreatedAtUtc,
                    s.ActivatedAtUtc,
                    s.ExpiresAtUtc,
                    s.RevokedAtUtc,
                    s.IsPrimary,
                    s.CreatedBy,
                    s.ActivatedBy,
                    s.RevokedBy,
                    s.LastUsedAtUtc,
                    s.UsageCount,
                    Status = s.RevokedAtUtc != null ? "revoked" :
                             s.ExpiresAtUtc != null && s.ExpiresAtUtc < DateTime.UtcNow ? "expired" :
                             s.ActivatedAtUtc == null ? "inactive" :
                             s.IsPrimary ? "primary" : "active"
                })
                .ToListAsync(ct);

            return Results.Ok(new { clientId, clientName = client.ClientName, secrets });
        });

        // POST /admin/api/clients/{clientId}/secrets - Create new secret
        admin.MapPost("/clients/{clientId:guid}/secrets", async (
            Guid clientId,
            CreateSecretRequest request,
            AuthDbContext db,
            IClientStore clientStore,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
            var isPlatformAdmin = platformAdminResult.Succeeded;

            // Verify client exists and user has access
            var clientQuery = db.Clients.AsNoTracking().Where(c => c.Id == clientId);
            if (!isPlatformAdmin)
            {
                var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
                if (!currentTenantId.HasValue)
                {
                    return Results.Problem(statusCode: 403, title: "No tenant context");
                }
                clientQuery = clientQuery.Where(c => c.TenantId == currentTenantId.Value);
            }

            var client = await clientQuery.FirstOrDefaultAsync(ct);
            if (client is null)
            {
                return Results.Problem(statusCode: 404, title: "Client not found");
            }
            if (client.IsSystemClient)
            {
                return Results.Problem(statusCode: 403, title: "System client is read-only");
            }

            // Check max active secrets limit (3)
            var activeCount = await db.ClientSecrets
                .Where(s => s.ClientId == clientId && s.ActivatedAtUtc != null && s.RevokedAtUtc == null)
                .CountAsync(ct);

            if (activeCount >= 3)
            {
                return Results.Problem(
                    statusCode: 400,
                    title: "Maximum active secrets reached",
                    detail: "A client can have a maximum of 3 active secrets. Revoke an existing secret before creating a new one.");
            }

            // Generate secure random secret (32 bytes = 256 bits)
            var secretValue = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

            // Calculate expiry if requested
            DateTime? expiresAtUtc = null;
            if (request.ExpiresInDays.HasValue)
            {
                if (request.ExpiresInDays.Value < 1 || request.ExpiresInDays.Value > 730)
                {
                    return Results.Problem(
                        statusCode: 400,
                        title: "Invalid expiry period",
                        detail: "Expiry must be between 1 and 730 days (2 years).");
                }
                expiresAtUtc = DateTime.UtcNow.AddDays(request.ExpiresInDays.Value);
            }

            // Get current user
            var username = httpContext.User.Identity?.Name ?? "system";

            // Create secret
            var secret = await clientStore.CreateSecretAsync(
                clientId,
                secretValue,
                request.Description,
                username,
                expiresAtUtc,
                ct);

            // Activate immediately if requested
            if (request.ActivateImmediately)
            {
                await clientStore.ActivateSecretAsync(secret.Id, username, ct);
                secret.ActivatedAtUtc = DateTime.UtcNow;
                secret.ActivatedBy = username;
            }

            // Return secret value (ONLY time it's ever returned!)
            return Results.Ok(new
            {
                secretId = secret.Id,
                secretValue, // ⚠️ CRITICAL: Only returned once!
                description = secret.Description,
                createdAtUtc = secret.CreatedAtUtc,
                expiresAtUtc = secret.ExpiresAtUtc,
                activated = secret.ActivatedAtUtc != null,
                warning = "Save this secret now. You won't be able to see it again."
            });
        });

        // POST /admin/api/clients/{clientId}/secrets/{secretId}/activate - Activate a secret
        admin.MapPost("/clients/{clientId:guid}/secrets/{secretId:guid}/activate", async (
            Guid clientId,
            Guid secretId,
            AuthDbContext db,
            IClientStore clientStore,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (!await VerifyMutableClientAccess(clientId, db, tenantAccessor, authorizationService, httpContext, ct))
            {
                return Results.Problem(statusCode: 404, title: "Client not found or access denied");
            }

            var username = httpContext.User.Identity?.Name ?? "system";
            var success = await clientStore.ActivateSecretAsync(secretId, username, ct);

            if (!success)
            {
                return Results.Problem(statusCode: 404, title: "Secret not found");
            }

            await clientStore.InvalidateClientCacheAsync(
                (await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct))?.ClientId ?? string.Empty,
                tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty,
                ct);

            return Results.Ok(new { success = true, activatedAtUtc = DateTime.UtcNow });
        });

        // POST /admin/api/clients/{clientId}/secrets/{secretId}/set-primary - Set secret as primary
        admin.MapPost("/clients/{clientId:guid}/secrets/{secretId:guid}/set-primary", async (
            Guid clientId,
            Guid secretId,
            AuthDbContext db,
            IClientStore clientStore,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (!await VerifyMutableClientAccess(clientId, db, tenantAccessor, authorizationService, httpContext, ct))
            {
                return Results.Problem(statusCode: 404, title: "Client not found or access denied");
            }

            var username = httpContext.User.Identity?.Name ?? "system";
            var success = await clientStore.SetPrimarySecretAsync(secretId, username, ct);

            if (!success)
            {
                return Results.Problem(statusCode: 404, title: "Secret not found or not active");
            }

            await clientStore.InvalidateClientCacheAsync(
                (await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct))?.ClientId ?? string.Empty,
                tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty,
                ct);

            return Results.Ok(new { success = true });
        });

        // DELETE /admin/api/clients/{clientId}/secrets/{secretId} - Revoke a secret
        admin.MapDelete("/clients/{clientId:guid}/secrets/{secretId:guid}", async (
            Guid clientId,
            Guid secretId,
            AuthDbContext db,
            IClientStore clientStore,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (!await VerifyMutableClientAccess(clientId, db, tenantAccessor, authorizationService, httpContext, ct))
            {
                return Results.Problem(statusCode: 404, title: "Client not found or access denied");
            }

            var username = httpContext.User.Identity?.Name ?? "system";
            var success = await clientStore.RevokeSecretAsync(secretId, username, ct);

            if (!success)
            {
                return Results.Problem(
                    statusCode: 400,
                    title: "Cannot revoke secret",
                    detail: "Secret not found or cannot revoke the last active secret (would lock out client).");
            }

            await clientStore.InvalidateClientCacheAsync(
                (await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct))?.ClientId ?? string.Empty,
                tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty,
                ct);

            return Results.Ok(new { success = true, revokedAtUtc = DateTime.UtcNow });
        });
    }

    private static async Task<bool> VerifyClientAccess(
        Guid clientId,
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
        var isPlatformAdmin = platformAdminResult.Succeeded;

        var clientQuery = db.Clients.AsNoTracking().Where(c => c.Id == clientId);
        if (!isPlatformAdmin)
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue) return false;
            clientQuery = clientQuery.Where(c => c.TenantId == currentTenantId.Value);
        }

        return await clientQuery.AnyAsync(ct);
    }

    private static async Task<bool> VerifyMutableClientAccess(
        Guid clientId,
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
        var isPlatformAdmin = platformAdminResult.Succeeded;

        var clientQuery = db.Clients.AsNoTracking().Where(c => c.Id == clientId && !c.IsSystemClient);
        if (!isPlatformAdmin)
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue) return false;
            clientQuery = clientQuery.Where(c => c.TenantId == currentTenantId.Value);
        }

        return await clientQuery.AnyAsync(ct);
    }

    public record CreateSecretRequest(
        string? Description = null,
        int? ExpiresInDays = null,
        bool ActivateImmediately = false
    );

    public record SeedTenantRequest(
        string TenantSlug,
        string TenantName,
        string? AdminEmail = null,
        string? AdminPassword = null
    );

    /// <summary>
    /// Request to migrate user credentials in batches.
    /// </summary>
    public record MigrateBatchRequest(
        int BatchSize = 100,
        int Skip = 0
    );

    private static void MapTenantIconEndpoints(RouteGroupBuilder admin)
    {
        // GET /admin/api/tenants/{tenantId}/icon - Serve tenant icon
        admin.MapGet("/tenants/{tenantId:guid}/icon", async (
            Guid tenantId,
            MrWhoOidc.Auth.Services.ITenantIconService iconService,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("TenantIcon");
            logger.LogInformation("GET tenant icon request for tenant {TenantId} from user {UserId}",
                tenantId, httpContext.User.Identity?.Name ?? "anonymous");

            // Check if user is platform admin
            var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
            var isPlatformAdmin = platformAdminResult.Succeeded;
            logger.LogDebug("User is platform admin: {IsPlatformAdmin}", isPlatformAdmin);

            // Tenant access validation for non-platform admins
            if (!isPlatformAdmin)
            {
                var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
                if (!currentTenantId.HasValue || currentTenantId.Value != tenantId)
                {
                    logger.LogWarning("Access denied: User not authorized for tenant {TenantId}, current tenant: {CurrentTenantId}",
                        tenantId, currentTenantId);
                    return Results.Problem(statusCode: 403, title: "Access denied");
                }
            }

            var icon = await iconService.GetTenantIconAsync(tenantId, ct);
            if (icon == null)
            {
                logger.LogDebug("No icon found for tenant {TenantId}", tenantId);
                return Results.NotFound();
            }

            logger.LogDebug("Serving icon {IconId} for tenant {TenantId}", icon.Id, tenantId);
            return Results.File(icon.FileData, icon.ContentType, icon.FileName);
        });

        // POST /admin/api/tenants/{tenantId}/icon - Upload tenant icon
        admin.MapPost("/tenants/{tenantId:guid}/icon", async (
            Guid tenantId,
            MrWhoOidc.Auth.Services.ITenantIconService iconService,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("TenantIcon");
            logger.LogInformation("POST tenant icon upload request for tenant {TenantId} from user {UserId}",
                tenantId, httpContext.User.Identity?.Name ?? "anonymous");

            try
            {
                // Check if user is platform admin
                var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
                var isPlatformAdmin = platformAdminResult.Succeeded;
                logger.LogDebug("User is platform admin: {IsPlatformAdmin}", isPlatformAdmin);

                // Tenant access validation for non-platform admins
                if (!isPlatformAdmin)
                {
                    var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
                    if (!currentTenantId.HasValue || currentTenantId.Value != tenantId)
                    {
                        logger.LogWarning("Access denied: User not authorized for tenant {TenantId}, current tenant: {CurrentTenantId}",
                            tenantId, currentTenantId);
                        return Results.Problem(statusCode: 403, title: "Access denied");
                    }
                }

                // Check if request has file
                if (!httpContext.Request.HasFormContentType)
                {
                    logger.LogWarning("Invalid content type for tenant {TenantId}", tenantId);
                    return Results.Problem(statusCode: 400, title: "Invalid content type", detail: "Expected multipart/form-data");
                }

                var form = await httpContext.Request.ReadFormAsync(ct);
                var file = form.Files.FirstOrDefault();
                if (file == null || file.Length == 0)
                {
                    logger.LogWarning("No file provided for tenant {TenantId}", tenantId);
                    return Results.Problem(statusCode: 400, title: "No file provided");
                }

                logger.LogDebug("Processing file upload for tenant {TenantId}: {FileName}, Size: {FileSize}, ContentType: {ContentType}",
                    tenantId, file.FileName, file.Length, file.ContentType);

                using var stream = file.OpenReadStream();
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream, ct);
                var fileData = memoryStream.ToArray();

                var iconId = await iconService.UploadIconAsync(tenantId, file.FileName ?? "icon", file.ContentType ?? "image/png", fileData, ct);

                logger.LogInformation("Successfully uploaded icon {IconId} for tenant {TenantId}", iconId, tenantId);
                return Results.Created($"/admin/api/tenants/{tenantId}/icon", new { id = iconId, fileName = file.FileName });
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "Invalid file upload for tenant {TenantId}: {ErrorMessage}", tenantId, ex.Message);
                return Results.Problem(statusCode: 400, title: "Invalid file", detail: ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error uploading icon for tenant {TenantId}: {ErrorMessage}", tenantId, ex.Message);
                return Results.Problem(statusCode: 500, title: "Upload failed", detail: "An error occurred while uploading the icon");
            }
        });

        // DELETE /admin/api/tenants/{tenantId}/icon - Delete tenant icon
        admin.MapDelete("/tenants/{tenantId:guid}/icon", async (
            Guid tenantId,
            MrWhoOidc.Auth.Services.ITenantIconService iconService,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("TenantIcon");
            logger.LogInformation("DELETE tenant icon request for tenant {TenantId} from user {UserId}",
                tenantId, httpContext.User.Identity?.Name ?? "anonymous");

            // Check if user is platform admin
            var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
            var isPlatformAdmin = platformAdminResult.Succeeded;
            logger.LogDebug("User is platform admin: {IsPlatformAdmin}", isPlatformAdmin);

            // Tenant access validation for non-platform admins
            if (!isPlatformAdmin)
            {
                var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
                if (!currentTenantId.HasValue || currentTenantId.Value != tenantId)
                {
                    logger.LogWarning("Access denied: User not authorized for tenant {TenantId}, current tenant: {CurrentTenantId}",
                        tenantId, currentTenantId);
                    return Results.Problem(statusCode: 403, title: "Access denied");
                }
            }

            var success = await iconService.DeleteTenantIconAsync(tenantId, ct);
            if (!success)
            {
                logger.LogWarning("Icon not found for tenant {TenantId}", tenantId);
                return Results.NotFound();
            }

            logger.LogInformation("Successfully deleted icon for tenant {TenantId}", tenantId);
            return Results.NoContent();
        });
    }
}
