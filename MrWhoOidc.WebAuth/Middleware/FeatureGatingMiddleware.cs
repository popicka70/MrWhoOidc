using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Middleware;

public sealed class FeatureGatingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<FeatureGatingMiddleware> _logger;

    public FeatureGatingMiddleware(RequestDelegate next, ILogger<FeatureGatingMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var requirements = endpoint.Metadata.GetOrderedMetadata<RequireLicenseFeatureAttribute>();
        if (requirements is null || requirements.Count == 0)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var featureService = context.RequestServices.GetRequiredService<IFeatureService>();
        var tenantAccessor = context.RequestServices.GetService<ITenantAccessor>();
        var tenantId = tenantAccessor?.CurrentTenant?.TenantId;

        var enabledFeatures = await featureService.GetEnabledFeaturesAsync(tenantId, context.RequestAborted).ConfigureAwait(false);
        foreach (var requirement in requirements)
        {
            if (enabledFeatures.Contains(requirement.FeatureName))
            {
                continue;
            }

            _logger.LogWarning(
                "Feature gating denied request. Feature={Feature} Path={Path} Tenant={Tenant}",
                requirement.FeatureName,
                context.Request.Path,
                tenantId?.ToString() ?? "platform");

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync($"{{\"error\":\"feature_disabled\",\"feature\":\"{requirement.FeatureName}\"}}", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
