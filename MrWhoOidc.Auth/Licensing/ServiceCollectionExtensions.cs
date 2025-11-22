using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MrWhoOidc.Auth.Licensing.Repositories;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Licensing.Validators;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.Auth.Licensing;

public static class LicensingServiceCollectionExtensions
{
    public static IServiceCollection AddMrWhoOidcLicensing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();
        services.TryAddScoped<ILicenseRepository, LicenseRepository>();
        services.TryAddScoped<IFeatureUsageRepository, FeatureUsageRepository>();
        services.TryAddScoped<IDefaultTenantContext, DefaultTenantContext>();
        services.TryAddScoped<ILicenseService, LicenseService>();
        services.TryAddScoped<IFeatureService, FeatureService>();
        services.TryAddScoped<ILimitService, LimitService>();
        services.TryAddScoped<ILicenseAnalyticsService, LicenseAnalyticsService>();
        services.TryAddScoped<ILicenseValidator, LicenseValidator>();

        return services;
    }
}
