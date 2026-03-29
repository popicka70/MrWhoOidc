using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Admin.Dto;
using MrWhoOidc.WebAuth.Background;
using MrWhoOidc.WebAuth.Admin.Helpers;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Security;

namespace MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;

/// <summary>
/// Provider CRUD endpoints (tenant-aware) plus BCL outbox admin endpoints.
/// Extracted from AdminApiEndpointMappingExtensions to allow registration on
/// both the /admin/api and /t/{slug}/admin/api route groups.
/// </summary>
internal static class ProviderAndBclEndpoints
{
    internal static void MapProviderEndpoints(RouteGroupBuilder group)
    {
        // Providers CRUD (tenant-aware)
        group.MapGet("/providers", async (
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

        group.MapGet("/providers/{id:guid}", async (
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

        group.MapPost("/providers", async (
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

        group.MapPut("/providers/{id:guid}", async (
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

        group.MapDelete("/providers/{id:guid}", async (
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
        group.MapGet("/clients/{clientId:guid}/providers", async (Guid clientId, AuthDbContext db, CancellationToken ct) =>
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

        group.MapPost("/clients/{clientId:guid}/providers", async (Guid clientId, AuthDbContext db, MappingInput input, CancellationToken ct) =>
        {
            if (input is null || input.IdentityProviderId == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "Invalid input");

            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
            if (client?.IsSystemClient == true)
                return Results.Problem(statusCode: 403, title: "System client is read-only");

            var clientExists = client is not null;
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

        group.MapPut("/clients/{clientId:guid}/providers/{identityProviderId:guid}", async (Guid clientId, Guid identityProviderId, AuthDbContext db, MappingInput input, CancellationToken ct) =>
        {
            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
            if (client?.IsSystemClient == true)
                return Results.Problem(statusCode: 403, title: "System client is read-only");

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

        group.MapDelete("/clients/{clientId:guid}/providers/{identityProviderId:guid}", async (Guid clientId, Guid identityProviderId, AuthDbContext db, CancellationToken ct) =>
        {
            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
            if (client?.IsSystemClient == true)
                return Results.Problem(statusCode: 403, title: "System client is read-only");

            var entity = await db.ClientIdentityProviders.FindAsync(new object[] { clientId, identityProviderId }, ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
            db.ClientIdentityProviders.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Claim mappings CRUD (with tenant validation)
        group.MapGet("/providers/{providerId:guid}/claim-mappings", async (
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

        group.MapPost("/providers/{providerId:guid}/claim-mappings", async (
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

        group.MapPut("/providers/{providerId:guid}/claim-mappings/{id:guid}", async (
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

        group.MapDelete("/providers/{providerId:guid}/claim-mappings/{id:guid}", async (
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
        group.MapGet("/providers/{providerId:guid}/keys", async (
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

        group.MapPost("/providers/{providerId:guid}/keys", async (
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

        group.MapPut("/providers/{providerId:guid}/keys/{id:guid}", async (
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

        group.MapDelete("/providers/{providerId:guid}/keys/{id:guid}", async (
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
        group.MapGet("/clients/{clientId:guid}/keys", async (Guid clientId, AuthDbContext db, CancellationToken ct) =>
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

        group.MapPut("/clients/{clientId:guid}/keys", async (Guid clientId, AuthDbContext db, ClientKeysInput input, IPublicJwksCache jwksCache, CancellationToken ct) =>
        {
            var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
            if (client is null) return Results.Problem(statusCode: 404, title: "Client not found");
            if (client.IsSystemClient) return Results.Problem(statusCode: 403, title: "System client is read-only");
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

    }

    internal static void MapBclOutboxEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/bcl/alerts/snapshot", (IBackchannelAlertDiagnostics diag) => Results.Ok(diag.GetSnapshot()));
        group.MapGet("/bcl/outbox", async (AuthDbContext db, IAuditSink audit, HttpContext httpContext, int? take, string? status, CancellationToken ct) =>
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
        group.MapPost("/bcl/outbox/{id:guid}/retry", async (Guid id, AuthDbContext db, IAuditSink audit, HttpContext httpContext, CancellationToken ct) =>
        {
            var n = await db.BackchannelLogoutNotifications.FirstOrDefaultAsync(n => n.Id == id, ct);
            if (n is null) return Results.Problem(statusCode: 404, title: "Not Found");
            n.Status = "pending";
            n.NextAttemptAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            audit.Emit("bcl.admin.outbox.retry", new { id = n.Id, client_id = n.ClientId, target = new Uri(n.TargetUri).Host, ip = httpContext.Connection.RemoteIpAddress?.ToString() });
            return Results.NoContent();
        });

    }

    private static async Task<bool> ValidateProviderAccessAsync(
        Guid providerId,
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var platformAdminResult = await authorizationService.AuthorizeAsync(httpContext.User, "platform-admin");
        if (platformAdminResult.Succeeded)
        {
            return await db.IdentityProviders.AsNoTracking().AnyAsync(p => p.Id == providerId, ct);
        }

        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return false;
        }

        return await db.IdentityProviders.AsNoTracking()
            .AnyAsync(p => p.Id == providerId && p.TenantId == currentTenantId.Value, ct);
    }
}
