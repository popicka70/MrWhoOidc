using System.Security.Claims;
using System.Text.Json;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Introspects JWT access tokens.
/// </summary>
public sealed class JwtTokenIntrospector(
    ITokenValidator tokenValidator,
    DPoPValidator dpopValidator,
    AudiencePolicy audiencePolicy,
    ResponseShaper responseShaper,
    ILogger<JwtTokenIntrospector> logger)
{
    public async Task<(Dictionary<string, object?>? Response, IResult? ErrorResult)> IntrospectAsync(
        IntrospectionContext context)
    {
        var (isValid, principal, _) = tokenValidator.Validate(context.Request.Token, context.Issuer);

        if (!isValid || principal is null)
        {
            return (null, null); // Not a valid JWT, try opaque token
        }

        var audience = principal.FindFirst("aud")?.Value;

        // Check audience policy
        if (!audiencePolicy.IsClientAllowedForAudience(context.Client, audience))
        {
            IntrospectionAuditor.LogAudit(
                logger,
                context.Request.ClientId,
                context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                "forbidden",
                audience
            );
            return (new Dictionary<string, object?> { ["active"] = false }, null);
        }

        // Validate DPoP if token is bound
        var cnfJkt = ExtractCnfJkt(principal);
        if (!string.IsNullOrEmpty(cnfJkt))
        {
            var (valid, errorResult) = await dpopValidator.ValidateAsync(
                context.HttpContext,
                context.Endpoint,
                context.Request.Token,
                cnfJkt
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
                    audience
                );
                return (new Dictionary<string, object?> { ["active"] = false }, null);
            }
        }

        var response = BuildJwtResponse(principal, context.Issuer);
        response = responseShaper.ShapeResponse(response, context.Client);

        IntrospectionAuditor.LogAudit(
            logger,
            context.Request.ClientId,
            context.HttpContext.Connection.RemoteIpAddress?.ToString(),
            "active",
            audience
        );

        return (response, null);
    }

    private static string? ExtractCnfJkt(ClaimsPrincipal principal)
    {
        var cnfRaw = principal.FindFirst("cnf")?.Value;
        if (string.IsNullOrEmpty(cnfRaw))
        {
            return null;
        }

        try
        {
            using var cnfDoc = JsonDocument.Parse(cnfRaw);
            if (cnfDoc.RootElement.TryGetProperty("jkt", out var jktProp))
            {
                return jktProp.GetString();
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return null;
    }

    private static Dictionary<string, object?> BuildJwtResponse(ClaimsPrincipal principal, string issuer)
    {
        var scope = principal.FindFirst("scope")?.Value;
        var sub = principal.FindFirst("sub")?.Value;
        var iss = principal.FindFirst("iss")?.Value ?? issuer;
        var iat = principal.FindFirst("iat")?.Value.ToLongOrNull();
        var nbf = principal.FindFirst("nbf")?.Value.ToLongOrNull();
        var exp = principal.FindFirst("exp")?.Value.ToLongOrNull();
        var jti = principal.FindFirst("jti")?.Value;
        var cnfRaw = principal.FindFirst("cnf")?.Value;
        var actRaw = principal.FindFirst("act")?.Value;

        // Parse cnf claim if present
        object? cnf = null;
        if (!string.IsNullOrEmpty(cnfRaw))
        {
            try
            {
                cnf = JsonDocument.Parse(cnfRaw).RootElement;
            }
            catch
            {
                // Ignore parse errors
            }
        }

        // Support multiple audiences
        var audClaims = principal.Claims
            .Where(c => c.Type == "aud")
            .Select(c => c.Value)
            .Distinct()
            .ToArray();

        object? audValue = audClaims.Length switch
        {
            > 1 => audClaims,
            1 => audClaims[0],
            _ => null
        };

        var response = new Dictionary<string, object?>
        {
            ["active"] = true,
            ["token_type"] = "Bearer",
            ["scope"] = scope,
            ["sub"] = sub,
            ["username"] = sub,
            ["aud"] = audValue,
            ["iss"] = iss,
            ["iat"] = iat,
            ["nbf"] = nbf,
            ["exp"] = exp,
            ["jti"] = jti
        };

        if (cnf is not null)
        {
            response["cnf"] = cnf;
        }

        // Include act claim (delegation) if present
        if (!string.IsNullOrEmpty(actRaw))
        {
            try
            {
                using var actDoc = JsonDocument.Parse(actRaw);
                response["act"] = actDoc.RootElement.Clone();
            }
            catch
            {
                // If not JSON, include as raw string
                response["act"] = actRaw;
            }
        }

        return response;
    }
}
