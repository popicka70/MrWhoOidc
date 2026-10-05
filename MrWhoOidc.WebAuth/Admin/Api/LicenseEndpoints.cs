using Microsoft.AspNetCore.Http;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Admin.Api;

internal static class LicenseEndpoints
{
    public static void MapLicenseEndpoints(RouteGroupBuilder? adminGroup, RouteGroupBuilder? tenantAdminGroup = null, RouteGroupBuilder? platformAdminGroup = null)
    {
        ArgumentNullException.ThrowIfNull(adminGroup);

        MapGroup(adminGroup, null, tenantAdmin: true);

        if (tenantAdminGroup is not null)
        {
            MapGroup(tenantAdminGroup, "Tenant", tenantAdmin: true);
        }

        if (platformAdminGroup is not null)
        {
            MapGroup(platformAdminGroup, "Platform", tenantAdmin: false);
        }
    }

    private static void MapGroup(RouteGroupBuilder group, string? nameSuffix, bool tenantAdmin)
    {
        var suffix = string.IsNullOrEmpty(nameSuffix) ? string.Empty : $"_{nameSuffix}";

        // The operation marker is a tenant-admin requirement; the platform-admin group must not carry it.
        RouteHandlerBuilder Mark(RouteHandlerBuilder route, TenantAdminOperationKind kind)
            => tenantAdmin ? route.WithTenantAdminOperation(kind) : route;

        Mark(group.MapGet("/license", () => CreateDeprecatedResult("license lookup"))
            .WithName($"License_Get{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone), TenantAdminOperationKind.Read);

        Mark(group.MapPost("/license", () => CreateDeprecatedResult("license installation"))
            .WithName($"License_Install{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone), TenantAdminOperationKind.Write);

        Mark(group.MapPost("/license/validate", () => CreateDeprecatedResult("license validation"))
            .WithName($"License_Validate{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone), TenantAdminOperationKind.Read);

        Mark(group.MapGet("/license/history", () => CreateDeprecatedResult("license history"))
            .WithName($"License_History{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone), TenantAdminOperationKind.Read);

        Mark(group.MapGet("/license/usage", () => CreateDeprecatedResult("license usage analytics"))
            .WithName($"License_Usage{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone), TenantAdminOperationKind.Read);

        Mark(group.MapGet("/license/limits", () => CreateDeprecatedResult("license limit reporting"))
            .WithName($"License_Limits{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone), TenantAdminOperationKind.Read);

        Mark(group.MapGet("/license/tiers", () => CreateDeprecatedResult("license tier discovery"))
            .WithName($"License_Tiers{suffix}")
            .ProducesProblem(StatusCodes.Status410Gone), TenantAdminOperationKind.Read);
    }

    private static IResult CreateDeprecatedResult(string surface)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status410Gone,
            title: "Licensing removed from WebAuth",
            detail: $"The {surface} endpoint is no longer available in MrWhoOidc.WebAuth. Use the standalone licensing service instead.");
    }
}