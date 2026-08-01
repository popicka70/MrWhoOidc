using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
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
    AuthDbContext db,
    ILogger<JwtTokenIntrospector> logger)
{
    public async Task<(Dictionary<string, object?>? Response, IResult? ErrorResult)> IntrospectAsync(
        IntrospectionContext context)
    {
        var (isValid, principal, _) = await tokenValidator.ValidateAsync(context.Request.Token, context.Issuer).ConfigureAwait(false);

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

        if (!await IsDelegatedGrantActiveAsync(principal).ConfigureAwait(false))
        {
            IntrospectionAuditor.LogAudit(
                logger,
                context.Request.ClientId,
                context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                "inactive_delegated_grant",
                audience);
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

        var delegatedGrant = await LoadDelegatedGrantAsync(principal).ConfigureAwait(false);
        var response = BuildJwtResponse(principal, context.Issuer, delegatedGrant);
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

    private async Task<DelegatedAccessGrant?> LoadDelegatedGrantAsync(ClaimsPrincipal principal)
    {
        var delegationId = principal.FindFirst("delegation_id")?.Value;
        if (!Guid.TryParse(delegationId, out var grantId))
        {
            return null;
        }

        return await db.DelegatedAccessGrants
            .AsNoTracking()
            .SingleOrDefaultAsync(grant => grant.Id == grantId)
            .ConfigureAwait(false);
    }

    private async Task<bool> IsDelegatedGrantActiveAsync(ClaimsPrincipal principal)
    {
        var delegationId = principal.FindFirst("delegation_id")?.Value;
        if (string.IsNullOrWhiteSpace(delegationId))
        {
            return true;
        }

        if (!Guid.TryParse(delegationId, out var grantId)
            || !Guid.TryParse(principal.FindFirst("sub")?.Value, out var subjectId)
            || !Guid.TryParse(ParseActorSubject(principal.FindFirst("act")?.Value), out var actorId))
        {
            return false;
        }

        var tokenClientId = principal.FindFirst("client_id")?.Value
            ?? principal.FindFirst("azp")?.Value;
        if (string.IsNullOrWhiteSpace(tokenClientId))
        {
            return false;
        }

        var grant = await db.DelegatedAccessGrants
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == grantId)
            .ConfigureAwait(false);
        if (grant is null
            || grant.ClientId is null
            || grant.DelegatorUserAccountId != subjectId
            || grant.DelegateUserAccountId != actorId
            || grant.Status != DelegatedAccessGrantStatus.Active
            || grant.StartsAt is not null && grant.StartsAt > DateTimeOffset.UtcNow
            || grant.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        var clientMatches = await db.Clients.AsNoTracking()
            .AnyAsync(client => client.Id == grant.ClientId.Value
                && client.TenantId == grant.TenantId
                && client.ClientId == tokenClientId)
            .ConfigureAwait(false);
        if (!clientMatches)
        {
            return false;
        }

        var tenantIsActive = await db.Tenants.AsNoTracking()
            .AnyAsync(tenant => tenant.Id == grant.TenantId && tenant.Status == TenantStatus.Active)
            .ConfigureAwait(false);
        if (!tenantIsActive)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var activeMembershipCount = await db.UserTenantMemberships.AsNoTracking()
            .CountAsync(membership => membership.TenantId == grant.TenantId
                && (membership.UserAccountId == grant.DelegatorUserAccountId
                    || membership.UserAccountId == grant.DelegateUserAccountId)
                && membership.Status == TenantMembershipStatus.Active
                && (membership.ExpiresAt == null || membership.ExpiresAt > now))
            .ConfigureAwait(false);

        return activeMembershipCount == 2;
    }

    private static string? ParseActorSubject(string? rawAct)
    {
        if (string.IsNullOrWhiteSpace(rawAct))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawAct);
            return document.RootElement.TryGetProperty("sub", out var subject)
                ? subject.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static Dictionary<string, object?> BuildJwtResponse(
        ClaimsPrincipal principal,
        string issuer,
        DelegatedAccessGrant? delegatedGrant)
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

        var clientId = principal.FindFirst("client_id")?.Value
            ?? principal.FindFirst("azp")?.Value;
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            response["client_id"] = clientId;
            response["azp"] = clientId;
        }

        var delegationId = principal.FindFirst("delegation_id")?.Value;
        if (!string.IsNullOrWhiteSpace(delegationId))
        {
            response["delegation_id"] = delegationId;
        }

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

        if (delegatedGrant is not null)
        {
            try
            {
                using var resources = JsonDocument.Parse(delegatedGrant.ResourceConstraintsJson);
                response["delegated_resources"] = resources.RootElement.Clone();
            }
            catch (JsonException)
            {
                response["delegated_resources"] = new Dictionary<string, object?>();
            }
        }

        return response;
    }
}
