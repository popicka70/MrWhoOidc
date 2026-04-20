using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.WebAuth.Infrastructure.Http;
using MrWhoOidc.WebAuth.Models.Auth;

namespace MrWhoOidc.WebAuth.Handlers;

public static class AuthorizeLocalErrorResults
{
    public static IResult Create(HttpContext http, string? errorCode, string? description, string? correlationId = null)
    {
        var normalizedErrorCode = string.IsNullOrWhiteSpace(errorCode)
            ? OAuthConstants.ErrorCodes.InvalidRequest
            : errorCode;

        var model = new AuthorizeErrorPageModel(
            normalizedErrorCode,
            string.IsNullOrWhiteSpace(description)
                ? "The authorization request could not be completed."
                : description,
            correlationId,
            http.Request.Path.Value);

        return Results.Extensions.RazorPage(
            "~/Pages/Auth/_AuthorizeError.cshtml",
            model,
            statusCode: GetStatusCode(normalizedErrorCode));
    }

    private static int GetStatusCode(string errorCode)
        => errorCode switch
        {
            OAuthConstants.ErrorCodes.AccessDenied => StatusCodes.Status403Forbidden,
            OAuthConstants.ErrorCodes.UnauthorizedClient => StatusCodes.Status401Unauthorized,
            OAuthConstants.ErrorCodes.ServerError => StatusCodes.Status500InternalServerError,
            OAuthConstants.ErrorCodes.RateLimitExceeded or OAuthConstants.ErrorCodes.SlowDown => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status400BadRequest
        };
}