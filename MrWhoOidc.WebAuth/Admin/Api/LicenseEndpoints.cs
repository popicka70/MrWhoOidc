using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.WebAuth.Admin.Api;

internal static class LicenseEndpoints
{
    public static void MapLicenseEndpoints(RouteGroupBuilder? adminGroup, RouteGroupBuilder? tenantAdminGroup = null, RouteGroupBuilder? platformAdminGroup = null)
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

        group.MapGet("/license", () => CreateDeprecatedResult("license lookup"))
            .WithName($"License_Get{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone);

        group.MapPost("/license", () => CreateDeprecatedResult("license installation"))
            .WithName($"License_Install{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone);

        group.MapPost("/license/validate", () => CreateDeprecatedResult("license validation"))
            .WithName($"License_Validate{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone);

        group.MapGet("/license/history", () => CreateDeprecatedResult("license history"))
            .WithName($"License_History{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone);

        group.MapGet("/license/usage", () => CreateDeprecatedResult("license usage analytics"))
            .WithName($"License_Usage{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone);

        group.MapGet("/license/limits", () => CreateDeprecatedResult("license limit reporting"))
            .WithName($"License_Limits{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone);

        group.MapGet("/license/tiers", () => CreateDeprecatedResult("license tier discovery"))
            .WithName($"License_Tiers{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone);
    }

    private static IResult CreateDeprecatedResult(string surface)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status410Gone,
            title: "Licensing removed from WebAuth",
            detail: $"The {surface} endpoint is no longer available in MrWhoOidc.WebAuth. Use the standalone licensing service instead.");
    }
}