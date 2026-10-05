using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using MrWhoOidc.WebAuth.Security.ApiBearer;

namespace MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;

internal static class AdminApiAntiforgeryExtensions
{
    internal static RouteHandlerBuilder RequireCookieAntiforgery(this RouteHandlerBuilder endpoint)
    {
        return endpoint.AddEndpointFilter(async (context, next) =>
        {
            var error = await ValidateCookieMutationAsync(context.HttpContext).ConfigureAwait(false);
            return error is null ? await next(context).ConfigureAwait(false) : error;
        });
    }

    internal static async Task<IResult?> ValidateCookieMutationAsync(HttpContext httpContext)
    {
        var bearerAuthentication = await httpContext.AuthenticateAsync(ApiTokenAuthHandler.SchemeName).ConfigureAwait(false);
        if (bearerAuthentication.Succeeded)
        {
            return null;
        }

        var antiforgery = httpContext.RequestServices.GetRequiredService<IAntiforgery>();
        if (await antiforgery.IsRequestValidAsync(httpContext).ConfigureAwait(false))
        {
            return null;
        }

        httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MrWhoOidc.AdminApiAntiforgery")
            .LogWarning("Admin API write rejected because antiforgery validation failed for {Path}", httpContext.Request.Path);

        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid antiforgery token");
    }
}
