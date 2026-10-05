using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Introspects opaque access tokens stored in the database.
/// </summary>
public sealed class OpaqueTokenIntrospector(
    AuthDbContext db,
    DPoPValidator dpopValidator,
    AudiencePolicy audiencePolicy,
    ResponseShaper responseShaper,
    ILogger<OpaqueTokenIntrospector> logger,
    IPairwiseSubjectService pairwiseSubjects)
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
            ).ConfigureAwait(false);

        if (entity is null)
        {
            return (null, null); // Token not found
        }

        // Check audience policy
        if (!audiencePolicy.IsClientAllowed(context.Client, AudiencePolicy.ParseStoredAudience(entity.Audience), entity.ClientId))
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
            ).ConfigureAwait(false);

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

        var subject = await ResolveSubjectAsync(db, pairwiseSubjects, entity, context.HttpContext.RequestAborted).ConfigureAwait(false);
        var response = BuildOpaqueResponse(entity, subject, context.Issuer);
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

    /// <summary>
    /// The token's sub as its client sees it: the pairwise subject for pairwise clients (as in the JWT
    /// form of the same token), otherwise the user id. Tokens without a user keep the stored value.
    /// </summary>
    internal static async Task<string> ResolveSubjectAsync(AuthDbContext db, IPairwiseSubjectService pairwiseSubjects, Token entity, CancellationToken ct)
    {
        if (entity.UserId == Guid.Empty || string.IsNullOrEmpty(entity.ClientId))
        {
            return entity.UserId.ToString();
        }

        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == entity.ClientId, ct)
            .ConfigureAwait(false);
        return client is null
            ? entity.UserId.ToString()
            : await pairwiseSubjects.GetSubjectAsync(client, entity.UserId, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, object?> BuildOpaqueResponse(Token entity, string subject, string issuer)
    {
        var scopes = JsonSerializer.Deserialize<string[]>(entity.ScopesJson) ?? Array.Empty<string>();

        var response = new Dictionary<string, object?>
        {
            ["active"] = true,
            ["token_type"] = "Bearer",
            ["scope"] = string.Join(' ', scopes),
            ["sub"] = subject,
            ["username"] = subject,
            ["aud"] = entity.Audience,
            ["iss"] = issuer,
            ["exp"] = entity.ExpiresAt.ToUnixTimeSeconds(),
            ["jti"] = entity.Jti,
            ["client_id"] = entity.ClientId
        };

        // RFC 7800 confirmation: jkt for DPoP (RFC 9449 §6.2), x5t#S256 for mTLS (RFC 8705 §3.2).
        var cnf = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(entity.CnfJkt))
        {
            cnf["jkt"] = entity.CnfJkt;
        }

        if (!string.IsNullOrEmpty(entity.CnfX5tS256))
        {
            cnf["x5t#S256"] = entity.CnfX5tS256;
        }

        if (cnf.Count > 0)
        {
            response["cnf"] = cnf;
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
