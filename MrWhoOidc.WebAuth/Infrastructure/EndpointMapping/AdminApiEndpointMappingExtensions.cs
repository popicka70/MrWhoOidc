using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

        // Client Secrets Management (Phase 2: Secret Rotation)
        MapClientSecretsEndpoints(admin);
        MapClientSecretsEndpoints(tenantAdmin);

        // Tenant Icon Endpoints (mapped to both admin groups)
        MapTenantIconEndpoints(admin);
        MapTenantIconEndpoints(tenantAdmin);

        // Providers CRUD (tenant-aware)
        admin.MapGet("/providers", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // Check if user is platform admin
            var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
            var isPlatformAdmin = platformAdminResult.Succeeded;

            var query = db.IdentityProviders.AsNoTracking();

            // Tenant filtering: regular tenant admins see only their tenant's providers
            if (!isPlatformAdmin)
            {
                var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
                if (!currentTenantId.HasValue)
                {
                    return Results.Problem(statusCode: 403, title: "No tenant context");
                }
                query = query.Where(p => p.TenantId == currentTenantId.Value);
            }

            var list = await query
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.DisplayName,
                    p.Type,
                    p.Enabled,
                    p.IsDefault,
                    p.LogoUrl,
                    p.SortOrder,
                    p.TenantId,
                    p.CreatedAt,
                    p.UpdatedAt
                }).ToListAsync(ct);
            return Results.Ok(list);
        });

        admin.MapGet("/providers/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // Check if user is platform admin
            var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
            var isPlatformAdmin = platformAdminResult.Succeeded;

            var query = db.IdentityProviders.AsNoTracking().Where(p => p.Id == id);

            // Tenant filtering for non-platform admins
            if (!isPlatformAdmin)
            {
                var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
                if (!currentTenantId.HasValue)
                {
                    return Results.Problem(statusCode: 403, title: "No tenant context");
                }
                query = query.Where(p => p.TenantId == currentTenantId.Value);
            }

            var p = await query.FirstOrDefaultAsync(ct);
            return p is null ? Results.Problem(statusCode: 404, title: "Not Found") : Results.Ok(p);
        });

        admin.MapPost("/providers", async (
            AuthDbContext db,
            IIdentityProviderValidator validator,
            ITenantAccessor tenantAccessor,
            IdentityProvider input,
            CancellationToken ct) =>
        {
            // Get current tenant ID - required for all providers
            var currentTenant = tenantAccessor.CurrentTenant;
            if (currentTenant == null)
            {
                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "Unable to determine current tenant context");
            }

            input.Id = Guid.NewGuid();
            input.TenantId = currentTenant.TenantId; // Always assign to current tenant
            input.CreatedAt = DateTimeOffset.UtcNow;
            input.UpdatedAt = DateTimeOffset.UtcNow;
            var (ok, error) = await validator.ValidateAsync(input, ct);
            if (!ok) return Results.Problem(statusCode: 400, title: "Validation failed", detail: error);

            db.IdentityProviders.Add(input);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/admin/api/providers/{input.Id}", new { input.Id });
        });

        admin.MapPut("/providers/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            IIdentityProviderValidator validator,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            IdentityProvider input,
            CancellationToken ct) =>
        {
            // Check if user is platform admin
            var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
            var isPlatformAdmin = platformAdminResult.Succeeded;

            var query = db.IdentityProviders.Where(p => p.Id == id);

            // Tenant filtering for non-platform admins
            if (!isPlatformAdmin)
            {
                var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
                if (!currentTenantId.HasValue)
                {
                    return Results.Problem(statusCode: 403, title: "No tenant context");
                }
                query = query.Where(p => p.TenantId == currentTenantId.Value);
            }

            var entity = await query.FirstOrDefaultAsync(ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");

            entity.Name = input.Name;
            entity.DisplayName = input.DisplayName;
            entity.Type = input.Type;
            entity.Enabled = input.Enabled;
            entity.IsDefault = input.IsDefault;
            entity.LogoUrl = input.LogoUrl;
            entity.SortOrder = input.SortOrder;
            entity.ConfigJson = input.ConfigJson;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            // Note: TenantId is NOT updated - providers cannot be moved between tenants

            var (ok, error) = await validator.ValidateAsync(entity, ct);
            if (!ok) return Results.Problem(statusCode: 400, title: "Validation failed", detail: error);

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapDelete("/providers/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // Check if user is platform admin
            var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
            var isPlatformAdmin = platformAdminResult.Succeeded;

            var query = db.IdentityProviders.Where(p => p.Id == id);

            // Tenant filtering for non-platform admins
            if (!isPlatformAdmin)
            {
                var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
                if (!currentTenantId.HasValue)
                {
                    return Results.Problem(statusCode: 403, title: "No tenant context");
                }
                query = query.Where(p => p.TenantId == currentTenantId.Value);
            }

            var entity = await query.FirstOrDefaultAsync(ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
            db.IdentityProviders.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Client ⇄ Providers mapping CRUD
        admin.MapGet("/clients/{clientId:guid}/providers", async (Guid clientId, AuthDbContext db, CancellationToken ct) =>
        {
            var list = await db.ClientIdentityProviders.AsNoTracking()
                .Where(m => m.ClientId == clientId)
                .Join(db.IdentityProviders.AsNoTracking(), m => m.IdentityProviderId, p => p.Id, (m, p) => new
                {
                    m.ClientId,
                    m.IdentityProviderId,
                    p.Name,
                    p.DisplayName,
                    m.Enabled,
                    m.IsDefaultForClient,
                    m.AutoRedirectIfSingle,
                    m.RequiredAcr,
                    m.Order
                })
                .OrderBy(x => x.Order).ToListAsync(ct);
            return Results.Ok(list);
        });

        admin.MapPost("/clients/{clientId:guid}/providers", async (Guid clientId, AuthDbContext db, MappingInput input, CancellationToken ct) =>
        {
            if (input is null || input.IdentityProviderId == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "Invalid input");

            var clientExists = await db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId, ct);
            var providerExists = await db.IdentityProviders.AsNoTracking().AnyAsync(p => p.Id == input.IdentityProviderId, ct);
            if (!clientExists || !providerExists)
                return Results.Problem(statusCode: 404, title: "Client or Provider not found");

            var entity = await db.ClientIdentityProviders.FindAsync(new object[] { clientId, input.IdentityProviderId }, ct);
            if (entity is null)
            {
                entity = new ClientIdentityProvider
                {
                    ClientId = clientId,
                    IdentityProviderId = input.IdentityProviderId,
                    Enabled = input.Enabled,
                    IsDefaultForClient = input.IsDefaultForClient,
                    AutoRedirectIfSingle = input.AutoRedirectIfSingle,
                    RequiredAcr = input.RequiredAcr,
                    Order = input.Order
                };
                db.ClientIdentityProviders.Add(entity);
            }
            else
            {
                entity.Enabled = input.Enabled;
                entity.IsDefaultForClient = input.IsDefaultForClient;
                entity.AutoRedirectIfSingle = input.AutoRedirectIfSingle;
                entity.RequiredAcr = input.RequiredAcr;
                entity.Order = input.Order;
            }

            if (input.IsDefaultForClient)
            {
                var others = await db.ClientIdentityProviders.Where(m => m.ClientId == clientId && m.IdentityProviderId != input.IdentityProviderId).ToListAsync(ct);
                foreach (var o in others) o.IsDefaultForClient = false;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        admin.MapPut("/clients/{clientId:guid}/providers/{identityProviderId:guid}", async (Guid clientId, Guid identityProviderId, AuthDbContext db, MappingInput input, CancellationToken ct) =>
        {
            var entity = await db.ClientIdentityProviders.FindAsync(new object[] { clientId, identityProviderId }, ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");

            entity.Enabled = input.Enabled;
            entity.IsDefaultForClient = input.IsDefaultForClient;
            entity.AutoRedirectIfSingle = input.AutoRedirectIfSingle;
            entity.RequiredAcr = input.RequiredAcr;
            entity.Order = input.Order;

            if (input.IsDefaultForClient)
            {
                var others = await db.ClientIdentityProviders.Where(m => m.ClientId == clientId && m.IdentityProviderId != identityProviderId).ToListAsync(ct);
                foreach (var o in others) o.IsDefaultForClient = false;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapDelete("/clients/{clientId:guid}/providers/{identityProviderId:guid}", async (Guid clientId, Guid identityProviderId, AuthDbContext db, CancellationToken ct) =>
        {
            var entity = await db.ClientIdentityProviders.FindAsync(new object[] { clientId, identityProviderId }, ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
            db.ClientIdentityProviders.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Claim mappings CRUD (with tenant validation)
        admin.MapGet("/providers/{providerId:guid}/claim-mappings", async (
            Guid providerId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // Validate provider access
            if (!await ValidateProviderAccessAsync(providerId, db, tenantAccessor, authorizationService, httpContext, ct))
                return Results.Problem(statusCode: 404, title: "Provider not found");

            var list = await db.IdentityProviderClaimMappings.AsNoTracking()
                .Where(m => m.IdentityProviderId == providerId)
                .OrderBy(m => m.Order)
                .Select(m => new { m.Id, m.IdentityProviderId, m.ExternalClaim, m.LocalClaim, m.Transform, m.Order })
                .ToListAsync(ct);
            return Results.Ok(list);
        });

        admin.MapPost("/providers/{providerId:guid}/claim-mappings", async (
            Guid providerId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            ClaimMappingInput input,
            CancellationToken ct) =>
        {
            if (input is null || string.IsNullOrWhiteSpace(input.ExternalClaim) || string.IsNullOrWhiteSpace(input.LocalClaim))
                return Results.Problem(statusCode: 400, title: "Invalid input");

            // Validate provider access
            if (!await ValidateProviderAccessAsync(providerId, db, tenantAccessor, authorizationService, httpContext, ct))
                return Results.Problem(statusCode: 404, title: "Provider not found");

            var entity = new IdentityProviderClaimMapping
            {
                IdentityProviderId = providerId,
                ExternalClaim = input.ExternalClaim!,
                LocalClaim = input.LocalClaim!,
                Transform = input.Transform,
                Order = input.Order
            };
            db.IdentityProviderClaimMappings.Add(entity);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/admin/api/providers/{providerId}/claim-mappings/{entity.Id}", new { entity.Id });
        });

        admin.MapPut("/providers/{providerId:guid}/claim-mappings/{id:guid}", async (
            Guid providerId,
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            ClaimMappingInput input,
            CancellationToken ct) =>
        {
            // Validate provider access
            if (!await ValidateProviderAccessAsync(providerId, db, tenantAccessor, authorizationService, httpContext, ct))
                return Results.Problem(statusCode: 404, title: "Provider not found");

            var entity = await db.IdentityProviderClaimMappings.FirstOrDefaultAsync(m => m.Id == id && m.IdentityProviderId == providerId, ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
            if (string.IsNullOrWhiteSpace(input.ExternalClaim) || string.IsNullOrWhiteSpace(input.LocalClaim))
                return Results.Problem(statusCode: 400, title: "Invalid input");

            entity.ExternalClaim = input.ExternalClaim!;
            entity.LocalClaim = input.LocalClaim!;
            entity.Transform = input.Transform;
            entity.Order = input.Order;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapDelete("/providers/{providerId:guid}/claim-mappings/{id:guid}", async (
            Guid providerId,
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // Validate provider access
            if (!await ValidateProviderAccessAsync(providerId, db, tenantAccessor, authorizationService, httpContext, ct))
                return Results.Problem(statusCode: 404, title: "Provider not found");

            var entity = await db.IdentityProviderClaimMappings.FirstOrDefaultAsync(m => m.Id == id && m.IdentityProviderId == providerId, ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
            db.IdentityProviderClaimMappings.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Provider keys CRUD (with tenant validation)
        admin.MapGet("/providers/{providerId:guid}/keys", async (
            Guid providerId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // Validate provider access
            if (!await ValidateProviderAccessAsync(providerId, db, tenantAccessor, authorizationService, httpContext, ct))
                return Results.Problem(statusCode: 404, title: "Provider not found");

            var list = await db.IdentityProviderKeys.AsNoTracking()
                .Where(k => k.IdentityProviderId == providerId)
                .OrderByDescending(k => k.CreatedAt)
                .Select(k => new { k.Id, k.Purpose, k.Alg, k.Kid, k.Active, k.CreatedAt, k.ExpiresAt })
                .ToListAsync(ct);
            return Results.Ok(list);
        });

        admin.MapPost("/providers/{providerId:guid}/keys", async (
            Guid providerId,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            ProviderKeyInput input,
            IPublicJwksCache jwksCache,
            CancellationToken ct) =>
        {
            if (input is null || string.IsNullOrWhiteSpace(input.JwkJson) || string.IsNullOrWhiteSpace(input.Alg))
                return Results.Problem(statusCode: 400, title: "Invalid input");
            try { using var _ = JsonDocument.Parse(input.JwkJson!); }
            catch (Exception ex) { return Results.Problem(statusCode: 400, title: "Invalid JWK JSON", detail: ex.Message); }

            // Validate provider access
            if (!await ValidateProviderAccessAsync(providerId, db, tenantAccessor, authorizationService, httpContext, ct))
                return Results.Problem(statusCode: 404, title: "Provider not found");

            if (!string.IsNullOrWhiteSpace(input.Kid))
            {
                var kidExists = await db.IdentityProviderKeys.AnyAsync(k => k.IdentityProviderId == providerId && k.Kid == input.Kid, ct);
                if (kidExists) return Results.Problem(statusCode: 409, title: "Duplicate kid", detail: "Key ID already exists for this provider.");
            }

            var entity = new IdentityProviderKey
            {
                IdentityProviderId = providerId,
                Purpose = input.Purpose,
                Jwk = input.JwkJson!,
                Alg = input.Alg!,
                Active = input.Active,
                Kid = string.IsNullOrWhiteSpace(input.Kid) ? Guid.NewGuid().ToString("N") : input.Kid,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = input.ExpiresAt
            };
            db.IdentityProviderKeys.Add(entity);

            if (entity.Active)
            {
                var others = await db.IdentityProviderKeys.Where(k => k.IdentityProviderId == providerId && k.Purpose == entity.Purpose && k.Id != entity.Id).ToListAsync(ct);
                foreach (var o in others) o.Active = false;
            }
            await db.SaveChangesAsync(ct);
            var providerName = await db.IdentityProviders.Where(p => p.Id == providerId).Select(p => p.Name).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(providerName)) await jwksCache.InvalidateProviderAsync(providerName!, ct);
            return Results.Created($"/admin/api/providers/{providerId}/keys/{entity.Id}", new { entity.Id });
        });

        admin.MapPut("/providers/{providerId:guid}/keys/{id:guid}", async (
            Guid providerId,
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            ProviderKeyInput input,
            IPublicJwksCache jwksCache,
            CancellationToken ct) =>
        {
            // Validate provider access
            if (!await ValidateProviderAccessAsync(providerId, db, tenantAccessor, authorizationService, httpContext, ct))
                return Results.Problem(statusCode: 404, title: "Provider not found");

            var entity = await db.IdentityProviderKeys.FirstOrDefaultAsync(k => k.Id == id && k.IdentityProviderId == providerId, ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
            if (input is null || string.IsNullOrWhiteSpace(input.Alg)) return Results.Problem(statusCode: 400, title: "Invalid input");

            if (!string.IsNullOrWhiteSpace(input.JwkJson))
            {
                try { using var _ = JsonDocument.Parse(input.JwkJson!); }
                catch (Exception ex) { return Results.Problem(statusCode: 400, title: "Invalid JWK JSON", detail: ex.Message); }
                entity.Jwk = input.JwkJson!;
            }
            if (!string.IsNullOrWhiteSpace(input.Kid) && !string.Equals(input.Kid, entity.Kid, StringComparison.Ordinal))
            {
                var kidExists = await db.IdentityProviderKeys.AnyAsync(k => k.IdentityProviderId == providerId && k.Kid == input.Kid, ct);
                if (kidExists) return Results.Problem(statusCode: 409, title: "Duplicate kid", detail: "Key ID already exists for this provider.");
                entity.Kid = input.Kid;
            }
            entity.Purpose = input.Purpose;
            entity.Alg = input.Alg!;
            entity.Active = input.Active;
            entity.ExpiresAt = input.ExpiresAt;
            if (entity.Active)
            {
                var others = await db.IdentityProviderKeys.Where(k => k.IdentityProviderId == providerId && k.Purpose == entity.Purpose && k.Id != entity.Id).ToListAsync(ct);
                foreach (var o in others) o.Active = false;
            }
            await db.SaveChangesAsync(ct);
            var providerName = await db.IdentityProviders.Where(p => p.Id == providerId).Select(p => p.Name).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(providerName)) await jwksCache.InvalidateProviderAsync(providerName!, ct);
            return Results.NoContent();
        });

        admin.MapDelete("/providers/{providerId:guid}/keys/{id:guid}", async (
            Guid providerId,
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            IPublicJwksCache jwksCache,
            CancellationToken ct) =>
        {
            // Validate provider access
            if (!await ValidateProviderAccessAsync(providerId, db, tenantAccessor, authorizationService, httpContext, ct))
                return Results.Problem(statusCode: 404, title: "Provider not found");

            var entity = await db.IdentityProviderKeys.FirstOrDefaultAsync(k => k.Id == id && k.IdentityProviderId == providerId, ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
            db.IdentityProviderKeys.Remove(entity);
            await db.SaveChangesAsync(ct);
            var providerName = await db.IdentityProviders.Where(p => p.Id == providerId).Select(p => p.Name).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(providerName)) await jwksCache.InvalidateProviderAsync(providerName!, ct);
            return Results.NoContent();
        });

        // Client keys (JWKS) read/update
        admin.MapGet("/clients/{clientId:guid}/keys", async (Guid clientId, AuthDbContext db, CancellationToken ct) =>
        {
            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
            if (client is null) return Results.Problem(statusCode: 404, title: "Client not found");
            var history = await db.ClientJwksHistories.AsNoTracking()
                .Where(h => h.ClientId == clientId)
                .OrderByDescending(h => h.CreatedAt)
                .Select(h => new { h.Id, h.CreatedAt, h.Source, h.Hash })
                .ToListAsync(ct);
            return Results.Ok(new { client.PublicJwksJson, client.PublicJwksUri, History = history });
        });

        admin.MapPut("/clients/{clientId:guid}/keys", async (Guid clientId, AuthDbContext db, ClientKeysInput input, IPublicJwksCache jwksCache, CancellationToken ct) =>
        {
            var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
            if (client is null) return Results.Problem(statusCode: 404, title: "Client not found");
            if (!string.IsNullOrWhiteSpace(input.PublicJwksJson))
            {
                var status = AdminApiHelpers.ComputeJwksStatus(input.PublicJwksJson);
                if (status is { Ok: false })
                    return Results.Problem(statusCode: 400, title: "Invalid JWKS", detail: status!.Value.Message);
                client.PublicJwksJson = input.PublicJwksJson;
                db.ClientJwksHistories.Add(new ClientJwksHistory
                {
                    ClientId = client.Id,
                    JwksJson = client.PublicJwksJson!,
                    Source = "manual",
                    Hash = AdminApiHelpers.ComputeSha256Hex(AdminApiHelpers.CompactJson(client.PublicJwksJson!))
                });
            }
            else
            {
                client.PublicJwksJson = null;
            }
            client.PublicJwksUri = string.IsNullOrWhiteSpace(input.PublicJwksUri) ? null : input.PublicJwksUri;
            await db.SaveChangesAsync(ct);
            if (!string.IsNullOrEmpty(client.ClientId)) await jwksCache.InvalidateClientAsync(client.ClientId, ct);
            return Results.NoContent();
        });

        // BCL outbox admin endpoints
        admin.MapGet("/bcl/alerts/snapshot", (IBackchannelAlertDiagnostics diag) => Results.Ok(diag.GetSnapshot()));
        admin.MapGet("/bcl/outbox", async (AuthDbContext db, IAuditSink audit, HttpContext httpContext, int? take, string? status, CancellationToken ct) =>
        {
            var q = db.BackchannelLogoutNotifications.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(n => n.Status == status);
            var list = await q.OrderByDescending(n => n.CreatedAt)
                .Take(Math.Clamp(take ?? 100, 1, 1000))
                .Select(n => new { n.Id, n.ClientId, n.TargetUri, n.Status, n.AttemptCount, n.MaxAttempts, n.LastHttpStatus, n.LastError, n.CreatedAt, n.LastAttemptAt, n.NextAttemptAt })
                .ToListAsync(ct);
            var backlog = await db.BackchannelLogoutNotifications.CountAsync(n => n.Status == "pending", ct);
            audit.Emit("bcl.admin.outbox.list", new { count = list.Count, backlog, ip = httpContext.Connection.RemoteIpAddress?.ToString() });
            return Results.Ok(new { backlog, items = list });
        });
        admin.MapPost("/bcl/outbox/{id:guid}/retry", async (Guid id, AuthDbContext db, IAuditSink audit, HttpContext httpContext, CancellationToken ct) =>
        {
            var n = await db.BackchannelLogoutNotifications.FirstOrDefaultAsync(n => n.Id == id, ct);
            if (n is null) return Results.Problem(statusCode: 404, title: "Not Found");
            n.Status = "pending";
            n.NextAttemptAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            audit.Emit("bcl.admin.outbox.retry", new { id = n.Id, client_id = n.ClientId, target = new Uri(n.TargetUri).Host, ip = httpContext.Connection.RemoteIpAddress?.ToString() });
            return Results.NoContent();
        });

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
            var clientsWithSecrets = await db.Clients
                .AsNoTracking()
                .Include(c => c.ClientSecrets)
                .Where(c => c.ClientSecrets.Any())
                .ToListAsync(ct);
            
            var criticalClients = clientsWithSecrets
                .Where(c => !c.ClientSecrets.Any(s => 
                    s.ActivatedAtUtc != null 
                    && s.RevokedAtUtc == null 
                    && (s.ExpiresAtUtc == null || s.ExpiresAtUtc > now)))
                .Select(c => new { clientId = c.ClientId, tenantId = c.TenantId })
                .ToList();
            
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

    LicenseEndpoints.MapLicenseEndpoints(admin, tenantAdmin, platformAdmin);

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
            if (!await VerifyClientAccess(clientId, db, tenantAccessor, authorizationService, httpContext, ct))
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
            if (!await VerifyClientAccess(clientId, db, tenantAccessor, authorizationService, httpContext, ct))
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
            if (!await VerifyClientAccess(clientId, db, tenantAccessor, authorizationService, httpContext, ct))
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
