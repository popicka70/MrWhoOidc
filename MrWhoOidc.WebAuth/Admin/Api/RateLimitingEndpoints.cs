using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.WebAuth.Admin.Api;

/// <summary>
/// The rate-limit inspection API used to return hard-coded placeholder data (zeros, sample clients, empty lists).
/// Rate-limit state is not aggregated server-side, so these routes - still called by <c>mrwho-cli rate-limits</c> -
/// now answer 501 with a pointer to the OpenTelemetry metrics instead of misleading data.
/// </summary>
internal static class RateLimitingEndpoints
{
    internal const string NotImplementedDetail =
        "Rate-limit statistics are not implemented by the server. Use the OpenTelemetry metrics " +
        "(e.g. oidc.token_exchange.ratelimit.allowed / .blocked) exported to your metrics backend instead.";

    public static void MapRateLimitingEndpoints(RouteGroupBuilder? adminGroup, RouteGroupBuilder? tenantAdminGroup = null, RouteGroupBuilder? platformAdminGroup = null)
    {
        ArgumentNullException.ThrowIfNull(adminGroup);

        MapGroup(adminGroup, null);

        if (tenantAdminGroup is not null)
        {
            MapGroup(tenantAdminGroup, "Tenant");
        }

        if (platformAdminGroup is not null)
        {
            MapGroup(platformAdminGroup, "Platform");
        }
    }

    private static void MapGroup(RouteGroupBuilder group, string? nameSuffix)
    {
        var suffix = string.IsNullOrEmpty(nameSuffix) ? string.Empty : $"_{nameSuffix}";

        group.MapGet("/rate-limits/overview", NotImplemented)
            .WithName($"RateLimits_Overview{suffix}")
            .Produces(StatusCodes.Status501NotImplemented);

        group.MapGet("/rate-limits/client/{clientId}", NotImplemented)
            .WithName($"RateLimits_Client{suffix}")
            .Produces(StatusCodes.Status501NotImplemented);

        group.MapGet("/rate-limits/events", NotImplemented)
            .WithName($"RateLimits_Events{suffix}")
            .Produces(StatusCodes.Status501NotImplemented);
    }

    private static IResult NotImplemented()
        => Results.Problem(
            statusCode: StatusCodes.Status501NotImplemented,
            title: "Not implemented",
            detail: NotImplementedDetail);
}
