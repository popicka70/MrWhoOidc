using System;
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
