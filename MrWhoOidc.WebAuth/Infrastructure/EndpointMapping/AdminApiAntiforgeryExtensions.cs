using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using MrWhoOidc.WebAuth.Security.ApiBearer;

namespace MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;

/// <summary>
/// Marks an endpoint as protected by the admin API antiforgery policy. The marker is endpoint metadata, so the
/// endpoint surface snapshot shows which endpoints carry the policy.
/// </summary>
internal sealed class AdminApiAntiforgeryMetadata;

internal static class AdminApiAntiforgeryExtensions
{
    /// <summary>
    /// M-CSRF: applies the antiforgery policy to every unsafe-method endpoint of the group.
    /// <para>
    /// The policy used to be attached to four endpoints only (the client-secret ones), while the admin API
    /// has dozens of mutating endpoints. A cookie-authenticated admin could therefore be cross-site requested
    /// into creating a client, changing a role or deleting a user. The check is applied to every unsafe
    /// method (POST, PUT, PATCH, DELETE); safe methods stay untouched.
    /// </para>
    /// <para>
    /// A request that authenticates with an API bearer token (the CLI and the MCP server) is exempt: it does
    /// not rely on the ambient browser cookie. A request that only carries a bearer header which does not
    /// authenticate is NOT exempt - it is a cookie-authenticated request and must pass the antiforgery check.
    /// </para>
    /// </summary>
    internal static RouteGroupBuilder RequireCookieAntiforgeryForUnsafeMethods(this RouteGroupBuilder group)
    {
        return group
            .WithMetadata(new AdminApiAntiforgeryMetadata())
            .AddEndpointFilter(async (context, next) =>
            {
                if (IsSafeMethod(context.HttpContext.Request.Method))
                {
                    return await next(context).ConfigureAwait(false);
                }

                var error = await ValidateCookieMutationAsync(context.HttpContext).ConfigureAwait(false);
                return error is null ? await next(context).ConfigureAwait(false) : error;
            });
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method)
        || HttpMethods.IsHead(method)
        || HttpMethods.IsOptions(method)
        || HttpMethods.IsTrace(method);

    internal static async Task<IResult?> ValidateCookieMutationAsync(HttpContext httpContext)
    {
        // Only an AUTHENTICATED api-bearer request is exempt. A bearer header that does not authenticate
        // (invalid, expired or not an admin API token) leaves the request cookie-authenticated, and it must
        // then pass the antiforgery check.
        var bearerAuthentication = await httpContext.AuthenticateAsync(ApiTokenAuthHandler.SchemeName).ConfigureAwait(false);
        if (bearerAuthentication.Succeeded)
        {
            return null;
        }

        var antiforgery = httpContext.RequestServices.GetRequiredService<IAntiforgery>();
        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MrWhoOidc.AdminApiAntiforgery");

        var valid = false;
        try
        {
            valid = await antiforgery.IsRequestValidAsync(httpContext).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException ex)
        {
            // The antiforgery service THROWS when the token is missing or invalid. A rejected write must be a
            // clean 400, not an unhandled 500.
            logger.LogWarning(ex, "Admin API write rejected for {Path}: {Reason}", httpContext.Request.Path, ex.Message);
        }

        if (valid)
        {
            return null;
        }

        logger.LogWarning(
            "Admin API write rejected because antiforgery validation failed for {Path} (authenticated={Authenticated})",
            httpContext.Request.Path,
            httpContext.User?.Identity?.IsAuthenticated);

        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid antiforgery token");
    }
}
