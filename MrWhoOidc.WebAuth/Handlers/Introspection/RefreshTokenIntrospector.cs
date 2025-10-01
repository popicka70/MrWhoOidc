using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Introspects refresh tokens (owner-only).
/// </summary>
public sealed class RefreshTokenIntrospector(
    AuthDbContext db,
    IClientStore clientStore,
    ResponseShaper responseShaper,
    IOptions<AuthOptions> authOptions,
    ILogger<RefreshTokenIntrospector> logger)
{
    public async Task<(Dictionary<string, object?>? Response, IResult? ErrorResult)> IntrospectAsync(
        IntrospectionContext context)
    {
        if (!authOptions.Value.AllowRefreshTokenIntrospection)
        {
            return (new Dictionary<string, object?> { ["active"] = false }, null);
        }

        var tokenHash = context.Request.Token.ComputeTokenHash();
        var entity = await db.Tokens
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.Type == "refresh" && t.TokenHash == tokenHash,
                context.HttpContext.RequestAborted
            );

        if (entity is null)
        {
            return (null, null); // Not a refresh token
        }

        // Only the issuing client can introspect its refresh token
        if (!string.Equals(entity.ClientId, context.Request.ClientId, StringComparison.Ordinal))
        {
            IntrospectionAuditor.LogAudit(
                logger,
                context.Request.ClientId,
                context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                "forbidden",
                null
            );
            return (new Dictionary<string, object?> { ["active"] = false }, null);
        }

        var isActive = entity.RevokedAt is null && entity.ExpiresAt > DateTimeOffset.UtcNow;
        if (!isActive)
        {
            IntrospectionAuditor.LogAudit(
                logger,
                context.Request.ClientId,
                context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                "inactive",
                null
            );
            return (new Dictionary<string, object?> { ["active"] = false }, null);
        }

        var scopes = JsonSerializer.Deserialize<string[]>(entity.ScopesJson) ?? Array.Empty<string>();
        var response = new Dictionary<string, object?>
        {
            ["active"] = true,
            ["token_type"] = "refresh_token",
            ["scope"] = string.Join(' ', scopes),
            ["sub"] = entity.UserId.ToString(),
            ["username"] = entity.UserId.ToString(),
            ["iss"] = context.Issuer,
            ["exp"] = entity.ExpiresAt.ToUnixTimeSeconds(),
            ["client_id"] = context.Request.ClientId
        };

        // Apply privacy shaping
        var client = await clientStore.FindByClientIdAsync(context.Request.ClientId);
        if (client is not null)
        {
            response = responseShaper.ShapeResponse(response, client);
        }

        IntrospectionAuditor.LogAudit(
            logger,
            context.Request.ClientId,
            context.HttpContext.Connection.RemoteIpAddress?.ToString(),
            "active",
            null
        );

        return (response, null);
    }
}
