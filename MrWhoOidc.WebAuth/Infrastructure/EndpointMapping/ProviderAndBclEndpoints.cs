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
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;

/// <summary>
/// Provider CRUD endpoints (tenant-aware) plus BCL outbox admin endpoints.
/// Extracted from AdminApiEndpointMappingExtensions to allow registration on
/// both the /admin/api and /t/{slug}/admin/api route groups.
/// </summary>
internal static class ProviderAndBclEndpoints
{
    /// <summary>
    /// Attaches a TenantAdminOperationRequirement to a RouteGroupBuilder route.
    /// </summary>
    private static RouteGroupBuilder WithOperation(this RouteGroupBuilder routes, TenantAdminOperationKind kind)
    {
        routes.AttachRequirement(new TenantAdminOperationRequirement { Kind = kind });
        return routes;
    }

    /// <summary>
    /// Attaches a TenantAdminOperationRequirement to a RouteHandlerBuilder route.
    /// </summary>
    private static RouteHandlerBuilder WithOperation(this RouteHandlerBuilder routes, TenantAdminOperationKind kind)
    {
        routes.AttachRequirement(new TenantAdminOperationRequirement { Kind = kind });
        return routes;
    }

    /// <summary>
    /// Attaches an IAuthorizationRequirement to a RouteGroupBuilder.
    /// </summary>
    private static RouteGroupBuilder AttachRequirement(this RouteGroupBuilder routes, IAuthorizationRequirement requirement)
    {
           routes.WithMetadata(requirement);
        return routes;
    }

    /// <summary>
    /// Attaches an IAuthorizationRequirement to a RouteHandlerBuilder.
    /// </summary>
    private static RouteHandlerBuilder AttachRequirement(this RouteHandlerBuilder routes, IAuthorizationRequirement requirement)
    {
           routes.WithMetadata(requirement);
        return routes;
    }
    internal static void MapProviderEndpoints(RouteGroupBuilder group)
    {
        // Providers CRUD (tenant-aware)
        group.MapGet("/providers", async (
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return Results.Problem(statusCode: 403, title: "No tenant context");
            }

            var list = await db.IdentityProviders.AsNoTracking()
                .Where(p => p.TenantId == currentTenantId.Value)
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.DisplayName,
                    p.Type,
                    p.Enabled,
                    p.IsDefault,
                    p.AllowRegistration,
                    p.LogoUrl,
                    p.SortOrder,
                    p.TenantId,
                    p.CreatedAt,
                    p.UpdatedAt
                })
                .ToListAsync(ct);
            return Results.Ok(list);
        });

        group.MapGet("/providers/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return Results.Problem(statusCode: 403, title: "No tenant context");
            }

            var p = await db.IdentityProviders.AsNoTracking()
                .Where(p => p.Id == id && p.TenantId == currentTenantId.Value)
                .FirstOrDefaultAsync(ct);
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
        })
            .WithOperation(TenantAdminOperationKind.Write);

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
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return Results.Problem(statusCode: 403, title: "No tenant context");
            }

            var query = db.IdentityProviders.Where(p => p.Id == id && p.TenantId == currentTenantId.Value);

            var entity = await query.FirstOrDefaultAsync(ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");

            entity.Name = input.Name;
            entity.DisplayName = input.DisplayName;
            entity.Type = input.Type;
            entity.Enabled = input.Enabled;
            entity.IsDefault = input.IsDefault;
            entity.AllowRegistration = input.AllowRegistration;
            entity.LogoUrl = input.LogoUrl;
            entity.LogoStorageType = string.IsNullOrWhiteSpace(input.LogoUrl)
                ? entity.LogoStorageType
                : IdentityProviderLogoStorageType.ExternalUrl;
            entity.SortOrder = input.SortOrder;
            entity.ConfigJson = input.ConfigJson;
            entity.ButtonBackgroundColor = input.ButtonBackgroundColor;
            entity.ButtonTextColor = input.ButtonTextColor;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            // Note: TenantId is NOT updated - providers cannot be moved between tenants

            var (ok, error) = await validator.ValidateAsync(entity, ct);
            if (!ok) return Results.Problem(statusCode: 400, title: "Validation failed", detail: error);

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
            .WithOperation(TenantAdminOperationKind.Write);

        group.MapDelete("/providers/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            ITenantAccessor tenantAccessor,
            CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return Results.Problem(statusCode: 403, title: "No tenant context");
            }

            var query = db.IdentityProviders.Where(p => p.Id == id && p.TenantId == currentTenantId.Value);

            var entity = await query.FirstOrDefaultAsync(ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
            db.IdentityProviders.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
            .WithOperation(TenantAdminOperationKind.Write);

        // Client ⇄ Providers mapping CRUD
        group.MapGet("/clients/{clientId:guid}/providers", async (Guid clientId, AuthDbContext db, ITenantAccessor tenantAccessor, CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");

            var clientExists = await db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value, ct);
            if (!clientExists) return Results.Problem(statusCode: 404, title: "Client not found");

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

        group.MapPost("/clients/{clientId:guid}/providers", async (Guid clientId, AuthDbContext db, ITenantAccessor tenantAccessor, MappingInput input, CancellationToken ct) =>
        {
            if (input is null || input.IdentityProviderId == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "Invalid input");

            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");

            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value, ct);
            if (client?.IsSystemClient == true)
                return Results.Problem(statusCode: 403, title: "System client is read-only");

            var clientExists = client is not null;
            var providerExists = await db.IdentityProviders.AsNoTracking().AnyAsync(p => p.Id == input.IdentityProviderId && p.TenantId == client!.TenantId, ct);
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
        })
            .WithOperation(TenantAdminOperationKind.Write);

        group.MapPut("/clients/{clientId:guid}/providers/{identityProviderId:guid}", async (Guid clientId, Guid identityProviderId, AuthDbContext db, ITenantAccessor tenantAccessor, MappingInput input, CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");

            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value, ct);
            if (client?.IsSystemClient == true)
                return Results.Problem(statusCode: 403, title: "System client is read-only");
            if (client is null) return Results.Problem(statusCode: 404, title: "Client not found");

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
        })
            .WithOperation(TenantAdminOperationKind.Write);

        group.MapDelete("/clients/{clientId:guid}/providers/{identityProviderId:guid}", async (Guid clientId, Guid identityProviderId, AuthDbContext db, ITenantAccessor tenantAccessor, CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");

            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value, ct);
            if (client?.IsSystemClient == true)
                return Results.Problem(statusCode: 403, title: "System client is read-only");
            if (client is null) return Results.Problem(statusCode: 404, title: "Client not found");

            var entity = await db.ClientIdentityProviders.FindAsync(new object[] { clientId, identityProviderId }, ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");
            db.ClientIdentityProviders.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
            .WithOperation(TenantAdminOperationKind.Write);

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
        })
            .WithOperation(TenantAdminOperationKind.Write);

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
        })
            .WithOperation(TenantAdminOperationKind.Write);

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
        })
            .WithOperation(TenantAdminOperationKind.Write);

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
        })
            .WithOperation(TenantAdminOperationKind.SecuritySensitiveWrite);

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
        })
            .WithOperation(TenantAdminOperationKind.SecuritySensitiveWrite);

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
        })
            .WithOperation(TenantAdminOperationKind.SecuritySensitiveWrite);

        // Client keys (JWKS) read/update
        group.MapGet("/clients/{clientId:guid}/keys", async (Guid clientId, AuthDbContext db, ITenantAccessor tenantAccessor, CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");

            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value, ct);
            if (client is null) return Results.Problem(statusCode: 404, title: "Client not found");
            var history = await db.ClientJwksHistories.AsNoTracking()
                .Where(h => h.ClientId == clientId)
                .OrderByDescending(h => h.CreatedAt)
                .Select(h => new { h.Id, h.CreatedAt, h.Source, h.Hash })
                .ToListAsync(ct);
            return Results.Ok(new { client.PublicJwksJson, client.PublicJwksUri, History = history });
        });

        group.MapPut("/clients/{clientId:guid}/keys", async (Guid clientId, AuthDbContext db, ITenantAccessor tenantAccessor, ClientKeysInput input, IPublicJwksCache jwksCache, CancellationToken ct) =>
        {
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
                return Results.Problem(statusCode: 403, title: "No tenant context");

            var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value, ct);
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
        })
            .WithOperation(TenantAdminOperationKind.SecuritySensitiveWrite);

    }

    internal static void MapPlatformProviderEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/providers", async (AuthDbContext db, CancellationToken ct) =>
        {
            var list = await db.IdentityProviders.AsNoTracking()
                .Where(p => p.TenantId == null)
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.DisplayName,
                    p.Type,
                    p.Enabled,
                    p.IsDefault,
                    p.AllowRegistration,
                    p.LogoUrl,
                    p.SortOrder,
                    p.TenantId,
                    p.CreatedAt,
                    p.UpdatedAt
                })
                .ToListAsync(ct);

            return Results.Ok(list);
        });

        group.MapGet("/providers/{id:guid}", async (Guid id, AuthDbContext db, CancellationToken ct) =>
        {
            var provider = await db.IdentityProviders.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == null, ct);

            return provider is null ? Results.Problem(statusCode: 404, title: "Not Found") : Results.Ok(provider);
        });

        group.MapPost("/providers", async (
            AuthDbContext db,
            IIdentityProviderValidator validator,
            IdentityProvider input,
            CancellationToken ct) =>
        {
            input.Id = Guid.NewGuid();
            input.TenantId = null;
            input.CreatedAt = DateTimeOffset.UtcNow;
            input.UpdatedAt = DateTimeOffset.UtcNow;

            if (!string.IsNullOrWhiteSpace(input.LogoUrl))
            {
                input.LogoStorageType = IdentityProviderLogoStorageType.ExternalUrl;
            }

            var (ok, error) = await validator.ValidateAsync(input, ct);
            if (!ok) return Results.Problem(statusCode: 400, title: "Validation failed", detail: error);

            db.IdentityProviders.Add(input);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/platform-admin/api/providers/{input.Id}", new { input.Id, input.Name });
        });

        group.MapPut("/providers/{id:guid}", async (
            Guid id,
            AuthDbContext db,
            IIdentityProviderValidator validator,
            IdentityProvider input,
            CancellationToken ct) =>
        {
            var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == null, ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");

            entity.Name = input.Name;
            entity.DisplayName = input.DisplayName;
            entity.Type = input.Type;
            entity.Enabled = input.Enabled;
            entity.IsDefault = input.IsDefault;
            entity.AllowRegistration = input.AllowRegistration;
            entity.LogoUrl = input.LogoUrl;
            entity.LogoStorageType = string.IsNullOrWhiteSpace(input.LogoUrl)
                ? entity.LogoStorageType
                : IdentityProviderLogoStorageType.ExternalUrl;
            entity.SortOrder = input.SortOrder;
            entity.ConfigJson = input.ConfigJson;
            entity.ButtonBackgroundColor = input.ButtonBackgroundColor;
            entity.ButtonTextColor = input.ButtonTextColor;
            entity.UpdatedAt = DateTimeOffset.UtcNow;

            var (ok, error) = await validator.ValidateAsync(entity, ct);
            if (!ok) return Results.Problem(statusCode: 400, title: "Validation failed", detail: error);

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/providers/{id:guid}", async (Guid id, AuthDbContext db, CancellationToken ct) =>
        {
            var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == null, ct);
            if (entity is null) return Results.Problem(statusCode: 404, title: "Not Found");

            db.IdentityProviders.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    internal static void MapBclOutboxEndpoints(RouteGroupBuilder group, bool isPlatformAdmin = false)
    {
        group.MapGet("/bcl/alerts/snapshot", (IBackchannelAlertDiagnostics diag) => Results.Ok(diag.GetSnapshot()));
        group.MapGet("/bcl/outbox", async (AuthDbContext db, IAuditSink audit, HttpContext httpContext, ITenantAccessor tenantAccessor, int? take, string? status, CancellationToken ct) =>
        {
            var q = db.BackchannelLogoutNotifications.AsNoTracking();
            if (!isPlatformAdmin)
            {
                var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
                if (!currentTenantId.HasValue)
                    return Results.Problem(statusCode: 403, title: "No tenant context");
                q = q.Where(n => n.TenantId == currentTenantId.Value);
            }
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(n => n.Status == status);
            var list = await q.OrderByDescending(n => n.CreatedAt)
                .Take(Math.Clamp(take ?? 100, 1, 1000))
                .Select(n => new { n.Id, n.ClientId, n.TargetUri, n.Status, n.AttemptCount, n.MaxAttempts, n.LastHttpStatus, n.LastError, n.CreatedAt, n.LastAttemptAt, n.NextAttemptAt })
                .ToListAsync(ct);
            var backlog = await q.CountAsync(n => n.Status == "pending", ct);
            audit.Emit("bcl.admin.outbox.list", new { count = list.Count, backlog, ip = httpContext.Connection.RemoteIpAddress?.ToString() });
            return Results.Ok(new { backlog, items = list });
        });
        group.MapPost("/bcl/outbox/{id:guid}/retry", async (Guid id, AuthDbContext db, IAuditSink audit, HttpContext httpContext, ITenantAccessor tenantAccessor, CancellationToken ct) =>
        {
            var q = db.BackchannelLogoutNotifications.AsNoTracking();
            if (!isPlatformAdmin)
            {
                var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
                if (!currentTenantId.HasValue)
                    return Results.Problem(statusCode: 403, title: "No tenant context");
                q = q.Where(n => n.Id == id && n.TenantId == currentTenantId.Value);
            }
            else
            {
                q = q.Where(n => n.Id == id);
            }
            var n = await q.FirstOrDefaultAsync(ct);
            if (n is null) return Results.Problem(statusCode: 404, title: "Not Found");
            n.Status = "pending";
            n.NextAttemptAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            audit.Emit("bcl.admin.outbox.retry", new { id = n.Id, client_id = n.ClientId, target = new Uri(n.TargetUri).Host, ip = httpContext.Connection.RemoteIpAddress?.ToString() });
            return Results.NoContent();
        })
            .WithOperation(TenantAdminOperationKind.Write);

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
