using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Security.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Phase 2 extraction (narrow): cookie authentication schemes + admin authorization policy/handler.
/// Intentionally does NOT register AuthOptions, metrics, or JWKS cache to minimize blast radius.
/// </summary>
public static class AuthenticationAuthorizationExtensions
{
    public static IServiceCollection AddMrWhoOidcAuthAndAdmin(this IServiceCollection services, IConfiguration _)
    {
        // Cookie auth schemes (mirrors original Program.cs configuration for parity)
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = ".mrwhooidc.auth";
                options.LoginPath = "/login";
                options.LogoutPath = "/logout";
                options.SlidingExpiration = true;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
            })
            .AddCookie("preauth", options =>
            {
                options.Cookie.Name = ".mrwhooidc.preauth";
                options.LoginPath = "/login";
                options.SlidingExpiration = true;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            });

        // Admin policy + handler (scope per request like original)
        services.AddAuthorization(options =>
        {
            options.AddPolicy("admin", policy => policy.Requirements.Add(new AdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();

        return services;
    }
}
