using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Registers the OIDC CORS policy (policy name: "oidc"). Extracted from Program.cs.
/// </summary>
public static class CorsExtensions
{
    public static IServiceCollection AddOidcCorsPolicy(this IServiceCollection services, OidcOptions oidcOptions)
    {
        services.AddCors(options =>
        {
            options.AddPolicy("oidc", policy =>
            {
                if (oidcOptions.AllowedCorsOrigins is { Length: > 0 })
                {
                    policy.WithOrigins(oidcOptions.AllowedCorsOrigins)
                          .WithMethods("GET", "POST", "OPTIONS")
                          .WithHeaders("authorization", "content-type")
                          .DisallowCredentials();
                }
                else
                {
                    policy.SetIsOriginAllowed(_ => false);
                }
            });
        });
        return services;
    }
}
