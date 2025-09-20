using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.WebAuth.Handlers;

public sealed class ErrorResults
{
    public static IResult InvalidRequest(string? description = null, string? state = null)
        => Create("invalid_request", description, state);
    public static IResult UnsupportedGrant(string? description = null)
        => Results.Json(new { error = "unsupported_grant_type", error_description = description ?? "The authorization grant type is not supported by the authorization server." }, statusCode: 400);
    public static IResult InvalidGrant()
        => Results.Json(new { error = "invalid_grant" }, statusCode: 400);
    public static IResult UnauthorizedClient()
        => Results.Json(new { error = "unauthorized_client" }, statusCode: 400);

    public static IResult TooManyRequests() => Results.Json(new { error = "slow_down" }, statusCode: 429);

    static IResult Create(string code, string? description, string? state)
    {
        var payload = new Dictionary<string, object?>
        {
            ["error"] = code,
            ["error_description"] = description
        };
        if (!string.IsNullOrEmpty(state)) payload["state"] = state;
        return Results.Json(payload, statusCode: 400);
    }
}
