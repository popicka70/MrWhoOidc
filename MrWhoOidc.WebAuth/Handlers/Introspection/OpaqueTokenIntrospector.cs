using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Introspects opaque access tokens stored in the database.
/// </summary>
public sealed class OpaqueTokenIntrospector(
    AuthDbContext db,
    DPoPValidator dpopValidator,
    AudiencePolicy audiencePolicy,
    ResponseShaper responseShaper,
    ILogger<OpaqueTokenIntrospector> logger)
{
    public async Task<(Dictionary<string, object?>? Response, IResult? ErrorResult)> IntrospectAsync(
        IntrospectionContext context)
    {
        var tokenHash = context.Request.Token.ComputeTokenHash();
        var entity = await db.Tokens
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.Type == "access" && t.TokenHash == tokenHash,
                context.HttpContext.RequestAborted
            );

        if (entity is null)
        {
            return (null, null); // Token not found
        }

        // Check audience policy
        if (!audiencePolicy.IsClientAllowedForAudience(context.Client, entity.Audience))
        {
            IntrospectionAuditor.LogAudit(
                logger,
                context.Request.ClientId,
                context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                "forbidden",
                entity.Audience
            );
            return (new Dictionary<string, object?> { ["active"] = false }, null);
        }

        // Check if token is active
        var isActive = entity.RevokedAt is null && entity.ExpiresAt > DateTimeOffset.UtcNow;
        if (!isActive)
        {
            IntrospectionAuditor.LogAudit(
                logger,
                context.Request.ClientId,
                context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                "inactive",
                entity.Audience
            );
            return (new Dictionary<string, object?> { ["active"] = false }, null);
        }

        // Validate DPoP if token is bound
        if (!string.IsNullOrEmpty(entity.CnfJkt))
        {
            var (valid, errorResult) = await dpopValidator.ValidateAsync(
                context.HttpContext,
                context.Endpoint,
                context.Request.Token,
                entity.CnfJkt
            );

            if (errorResult is not null)
            {
                return (null, errorResult);
            }

            if (!valid)
            {
                IntrospectionAuditor.LogAudit(
                    logger,
                    context.Request.ClientId,
                    context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                    "inactive",
                    entity.Audience
                );
                return (new Dictionary<string, object?> { ["active"] = false }, null);
            }
        }

        var response = BuildOpaqueResponse(entity, context.Issuer);
        response = responseShaper.ShapeResponse(response, context.Client);

        IntrospectionAuditor.LogAudit(
            logger,
            context.Request.ClientId,
            context.HttpContext.Connection.RemoteIpAddress?.ToString(),
            "active",
            entity.Audience
        );

        return (response, null);
    }

    private static Dictionary<string, object?> BuildOpaqueResponse(Token entity, string issuer)
    {
        var scopes = JsonSerializer.Deserialize<string[]>(entity.ScopesJson) ?? Array.Empty<string>();
        
        var response = new Dictionary<string, object?>
        {
            ["active"] = true,
            ["token_type"] = "Bearer",
            ["scope"] = string.Join(' ', scopes),
            ["sub"] = entity.UserId.ToString(),
            ["username"] = entity.UserId.ToString(),
            ["aud"] = entity.Audience,
            ["iss"] = issuer,
            ["exp"] = entity.ExpiresAt.ToUnixTimeSeconds(),
            ["jti"] = entity.Jti,
            ["client_id"] = entity.ClientId
        };

        if (!string.IsNullOrEmpty(entity.CnfJkt))
        {
            response["cnf"] = new { jkt = entity.CnfJkt };
        }

        // Include act claim if stored
        if (!string.IsNullOrEmpty(entity.ActJson))
        {
            try
            {
                using var actDoc = JsonDocument.Parse(entity.ActJson);
                response["act"] = actDoc.RootElement.Clone();
            }
            catch
            {
                // Ignore parse errors
            }
        }

        return response;
    }
}
