using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Admin.Dto;

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

        group.MapGet("/license", GetLicenseAsync)
            .WithName($"License_Get{suffix}")
            .Produces<LicenseInfoDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/license", InstallLicenseAsync)
            .WithName($"License_Install{suffix}")
            .Produces<LicenseInfoDto>(StatusCodes.Status200OK)
            .Produces<LicenseValidationErrorDto>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/license/validate", ValidateLicenseAsync)
            .WithName($"License_Validate{suffix}")
            .Produces<LicenseValidationResponseDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/license/history", GetLicenseHistoryAsync)
            .WithName($"License_History{suffix}")
            .Produces<LicenseHistoryResponseDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/license/usage", GetLicenseUsageAsync)
            .WithName($"License_Usage{suffix}")
            .Produces<FeatureUsageReportDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/license/limits", GetLicenseLimitsAsync)
            .WithName($"License_Limits{suffix}")
            .Produces<UsageLimitsReportDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/license/tiers", GetLicenseTiersAsync)
            .WithName($"License_Tiers{suffix}")
            .Produces<IReadOnlyList<LicenseTierDescriptorDto>>(StatusCodes.Status200OK);
    }

    private static async Task<IResult> GetLicenseAsync(
        HttpContext httpContext,
        [FromQuery] Guid? tenantId,
        ILicenseService licenseService,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveTenantAsync(httpContext, tenantAccessor, authorizationService, tenantId, cancellationToken);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var license = await licenseService.GetCurrentLicenseAsync(resolution.TenantId, cancellationToken).ConfigureAwait(false);
        if (license is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "License not found");
        }

        var dto = LicenseDtoMapper.ToDto(license, timeProvider.GetUtcNow());
        return Results.Ok(dto);
    }

    private static async Task<IResult> InstallLicenseAsync(
        HttpContext httpContext,
        [FromBody] InstallLicenseRequest request,
        [FromQuery] Guid? tenantId,
        ILicenseService licenseService,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
    TimeProvider timeProvider,
    ILogger<LicenseEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.LicenseKey))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "License key is required");
        }

        var resolution = await ResolveTenantAsync(httpContext, tenantAccessor, authorizationService, tenantId, cancellationToken);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var userId = GetUserId(httpContext.User);
        var trimmedNotes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();

        var result = await licenseService.InstallLicenseAsync(
            request.LicenseKey,
            resolution.TenantId,
            userId,
            trimmedNotes,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsValid || result.LicenseInfo is null)
        {
            var errorDto = LicenseDtoMapper.ToErrorDto(result);
            return Results.Json(errorDto, statusCode: StatusCodes.Status400BadRequest);
        }

        var dto = LicenseDtoMapper.ToDto(result.LicenseInfo, timeProvider.GetUtcNow());
        logger.LogInformation("License installed for tenant {Tenant}", resolution.TenantId?.ToString() ?? "platform");
        return Results.Ok(dto);
    }

    private static async Task<IResult> ValidateLicenseAsync(
        HttpContext httpContext,
        [FromBody] ValidateLicenseRequest request,
        ILicenseService licenseService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.LicenseKey))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "License key is required");
        }

        var result = await licenseService.ValidateLicenseKeyAsync(request.LicenseKey, cancellationToken).ConfigureAwait(false);
        var dto = LicenseDtoMapper.ToDto(result, timeProvider.GetUtcNow());
        return Results.Ok(dto);
    }

    private static async Task<IResult> GetLicenseHistoryAsync(
        HttpContext httpContext,
        [FromQuery] Guid? tenantId,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromQuery] string? action,
        ILicenseService licenseService,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (page <= 0)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Page must be at least 1");
        }

        if (pageSize <= 0 || pageSize > 100)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Page size must be between 1 and 100");
        }

        var resolution = await ResolveTenantAsync(httpContext, tenantAccessor, authorizationService, tenantId, cancellationToken);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var history = await licenseService.GetLicenseHistoryAsync(
            resolution.TenantId,
            page,
            pageSize,
            string.IsNullOrWhiteSpace(action) ? null : action,
            cancellationToken).ConfigureAwait(false);

        var dto = LicenseDtoMapper.ToDto(history);
        return Results.Ok(dto);
    }

    private static async Task<IResult> GetLicenseUsageAsync(
        HttpContext httpContext,
        [FromQuery] Guid? tenantId,
        [FromQuery] string? feature,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        ILicenseAnalyticsService analyticsService,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
        ILogger<LicenseEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await ResolveTenantAsync(httpContext, tenantAccessor, authorizationService, tenantId, cancellationToken).ConfigureAwait(false);
            if (resolution.Error is not null)
            {
                return resolution.Error;
            }

            var report = await analyticsService
                .GetFeatureUsageAsync(resolution.TenantId, feature, from, to, cancellationToken)
                .ConfigureAwait(false);

            var dto = LicenseDtoMapper.ToDto(report);
            return Results.Ok(dto);
        }
        catch (ArgumentException ex)
        {
            logger.LogDebug(ex, "Invalid arguments supplied for license usage analytics.");
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve license usage analytics.");
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unable to retrieve usage analytics");
        }
    }

    private static async Task<IResult> GetLicenseLimitsAsync(
        HttpContext httpContext,
        [FromQuery] Guid? tenantId,
        ILicenseAnalyticsService analyticsService,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
        TimeProvider timeProvider,
        ILogger<LicenseEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await ResolveTenantAsync(httpContext, tenantAccessor, authorizationService, tenantId, cancellationToken).ConfigureAwait(false);
            if (resolution.Error is not null)
            {
                return resolution.Error;
            }

            var report = await analyticsService
                .GetUsageLimitsAsync(resolution.TenantId, cancellationToken)
                .ConfigureAwait(false);

            var dto = LicenseDtoMapper.ToDto(report, timeProvider.GetUtcNow());
            return Results.Ok(dto);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "No license data available while retrieving limit analytics.");
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: ex.Message);
        }
        catch (ArgumentException ex)
        {
            logger.LogDebug(ex, "Invalid arguments supplied for license limit analytics.");
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve license limit analytics.");
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unable to retrieve limit analytics");
        }
    }

    private static async Task<IResult> GetLicenseTiersAsync(
        ILicenseAnalyticsService analyticsService,
        ILogger<LicenseEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var descriptors = await analyticsService.GetLicenseTiersAsync(cancellationToken).ConfigureAwait(false);
            var dto = LicenseDtoMapper.ToDto(descriptors);
            return Results.Ok(dto);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve license tier descriptors.");
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unable to retrieve tier catalog");
        }
    }

    private static async Task<(Guid? TenantId, IResult? Error)> ResolveTenantAsync(
        HttpContext httpContext,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService,
        Guid? requestedTenantId,
        CancellationToken cancellationToken)
    {
        var authResult = await authorizationService.AuthorizeAsync(httpContext.User, null, "platform-admin");
        var isPlatformAdmin = authResult.Succeeded;

        if (isPlatformAdmin)
        {
            var tenantId = requestedTenantId ?? tenantAccessor.CurrentTenant?.TenantId;
            return (tenantId, null);
        }

        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "No tenant context"));
        }

        if (requestedTenantId.HasValue && requestedTenantId.Value != currentTenantId.Value)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Cannot access another tenant license"));
        }

        return (currentTenantId, null);
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private sealed class LicenseEndpointsLogger
    {
    }
}
