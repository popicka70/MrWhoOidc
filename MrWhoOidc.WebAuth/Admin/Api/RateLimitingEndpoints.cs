using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAdmin.Services;
using MrWhoOidc.WebAuth.Admin.Dto;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Admin.Api;

internal static class RateLimitingEndpoints
{
    public static void MapRateLimitingEndpoints(RouteGroupBuilder? adminGroup, RouteGroupBuilder? platformAdminGroup = null)
    {
        ArgumentNullException.ThrowIfNull(adminGroup);

        MapGroup(adminGroup, null);

        if (platformAdminGroup is not null)
        {
            MapGroup(platformAdminGroup, "Platform");
        }
    }

    private static void MapGroup(RouteGroupBuilder group, string? nameSuffix)
    {
        var suffix = string.IsNullOrEmpty(nameSuffix) ? string.Empty : $"_{nameSuffix}";

        // Overview endpoint - shows all rate limiting policies and their current status
        group.MapGet("/rate-limits/overview", GetRateLimitingOverviewAsync)
            .WithName($"RateLimits_Overview{suffix}")
            .Produces<RateLimitingOverviewDto>(StatusCodes.Status200OK);

        // Detailed client-level rate limit usage
        group.MapGet("/rate-limits/client/{clientId}", GetClientRateLimitsAsync)
            .WithName($"RateLimits_Client{suffix}")
            .Produces<ClientRateLimitDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        // Real-time events feed - recent rate limit events
        group.MapGet("/rate-limits/events", GetRecentEventsAsync)
            .WithName($"RateLimits_Events{suffix}")
            .Produces<RateLimitEventsResponseDto>(StatusCodes.Status200OK);

        // Metrics export for Prometheus/Grafana integration
        group.MapGet("/rate-limits/metrics", ExportMetricsAsync)
            .WithName($"RateLimits_Metrics{suffix}")
            .Produces<IResult>();
    }

    private static async Task<IResult> GetRateLimitingOverviewAsync(
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        ITenantAccessor tenantAccessor,
        [FromQuery] Guid? tenantId,
        IRateLimitingMetricsService metricsService,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveTenantAsync(httpContext, tenantAccessor, authorizationService, tenantId, cancellationToken);
        if (resolution.Error is not null)
            return resolution.Error;

        var overview = await metricsService.GetOverviewAsync(cancellationToken);
        return Results.Ok(overview);
    }

    private static async Task<IResult> GetClientRateLimitsAsync(
        string clientId,
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        ITenantAccessor tenantAccessor,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveTenantAsync(httpContext, tenantAccessor, authorizationService, null, cancellationToken);
        if (resolution.Error is not null)
            return resolution.Error;

        // TODO: Query actual client rate limit data from cache/database
        var dto = new ClientRateLimitDto(
            clientId,
            "Sample Client",
            Guid.Empty,
            Array.Empty<PolicyUsageDto>(),
            DateTimeOffset.UtcNow,
            false);

        return Results.Ok(dto);
    }

    private static async Task<IResult> GetRecentEventsAsync(
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        ITenantAccessor tenantAccessor,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? clientFilter = null)
    {
        if (page <= 0 || pageSize <= 0 || pageSize > 100)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid pagination parameters");

        var resolution = await ResolveTenantAsync(httpContext, tenantAccessor, authorizationService, null, cancellationToken);
        if (resolution.Error is not null)
            return resolution.Error;

        // TODO: Query actual rate limit events from database/cache
        var events = Array.Empty<RateLimitEventDto>();

        var response = new RateLimitEventsResponseDto(events, 0, page, pageSize);
        return Results.Ok(response);
    }

    private static async Task<IResult> ExportMetricsAsync(
        HttpContext httpContext,
        IOidcMetrics metrics)
    {
        // Return metrics in OpenTelemetry/JSON format for Grafana/Prometheus integration
        // Note: Counter<long> values require OpenTelemetry/Prometheus client to read properly.
        // Placeholder values shown here until proper metric aggregation is implemented.
        var data = new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            metrics = new[]
            {
                new { name = "token_exchange_rate_limit_blocked", value = 0L },
                new { name = "token_exchange_rate_limit_allowed", value = 0L }
            }
        };

        return Results.Json(data, contentType: "application/json");
    }

    private static async Task<(Guid? TenantId, IResult? Error)> ResolveTenantAsync(
        HttpContext httpContext,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
        Guid? requestedTenantId,
        CancellationToken cancellationToken)
    {
        var authResult = await authorizationService.AuthorizeAsync(httpContext.User, null, "platform-admin");
        if (authResult.Succeeded)
        {
            return (requestedTenantId ?? tenantAccessor.CurrentTenant?.TenantId, null);
        }

        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
            return (null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "No tenant context"));

        if (requestedTenantId.HasValue && requestedTenantId.Value != currentTenantId.Value)
            return (null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Cannot access another tenant data"));

        return (currentTenantId, null);
    }
}
