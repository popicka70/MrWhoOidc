using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.WebAuth.Handlers;

/// <summary>
/// Factory methods for creating standardized OAuth/OIDC error responses.
/// </summary>
public sealed class ErrorResults
{
    public static IResult InvalidRequest(string? description = null, string? state = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.InvalidRequest, description, state, correlationId, 400);

    public static IResult InvalidGrant(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.InvalidGrant, description, null, correlationId, 400);

    public static IResult UnauthorizedClient(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.UnauthorizedClient, description, null, correlationId, 401);

    public static IResult UnsupportedGrantType(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.UnsupportedGrantType,
            description ?? "The authorization grant type is not supported by the authorization server.",
            null, correlationId, 400);

    public static IResult InvalidToken(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.InvalidToken, description, null, correlationId, 401);

    public static IResult AccessDenied(string? description = null, string? state = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.AccessDenied, description, state, correlationId, 403);

    public static IResult ServerError(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.ServerError, description, null, correlationId, 500);

    public static IResult UnsupportedResponseType(string? description = null, string? state = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.UnsupportedResponseType, description, state, correlationId, 400);

    public static IResult InvalidScope(string? description = null, string? state = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.InvalidScope, description, state, correlationId, 400);

    public static IResult InvalidTarget(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.InvalidTarget, description, null, correlationId, 400);

    public static IResult InvalidRequestObject(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.InvalidRequestObject, description, null, correlationId, 400);

    public static IResult RateLimitExceeded(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.RateLimitExceeded, description, null, correlationId, 429);

    public static IResult TooManyRequests(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.SlowDown, description, null, correlationId, 429);

    private static IResult Create(string code, string? description, string? state, string? correlationId, int statusCode)
    {
        var payload = new Dictionary<string, object?>
        {
            ["error"] = code
        };

        if (!string.IsNullOrEmpty(description))
            payload["error_description"] = description;
        if (!string.IsNullOrEmpty(state))
            payload["state"] = state;
        if (!string.IsNullOrEmpty(correlationId))
            payload["correlation_id"] = correlationId;

        return Results.Json(payload, statusCode: statusCode);
    }
}
