using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Security.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.MultiTenancy;

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
                
                // Handle tenant-aware redirects for unauthorized/unauthenticated requests
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = context =>
                    {
                        // Get tenant context from current request
                        var tenantAccessor = context.HttpContext.RequestServices.GetService<ITenantAccessor>();
                        var multiTenancyOptions = context.HttpContext.RequestServices.GetService<IMultiTenancyOptions>();
                        
                        var currentTenant = tenantAccessor?.CurrentTenant;
                        var loginPath = currentTenant != null && multiTenancyOptions?.Enabled == true
                            ? $"/t/{currentTenant.Slug}/login"
                            : "/login";
                        
                        var redirectUri = context.RedirectUri.Replace("/login", loginPath);
                        context.Response.Redirect(redirectUri);
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = context =>
                    {
                        // Get tenant context from current request
                        var tenantAccessor = context.HttpContext.RequestServices.GetService<ITenantAccessor>();
                        var multiTenancyOptions = context.HttpContext.RequestServices.GetService<IMultiTenancyOptions>();
                        
                        var currentTenant = tenantAccessor?.CurrentTenant;
                        var accessDeniedPath = currentTenant != null && multiTenancyOptions?.Enabled == true
                            ? $"/t/{currentTenant.Slug}/Account/AccessDenied"
                            : "/Account/AccessDenied";
                        
                        // Build redirect with returnUrl
                        var returnUrl = context.Request.Path + context.Request.QueryString;
                        context.Response.Redirect($"{accessDeniedPath}?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
                        return Task.CompletedTask;
                    }
                };
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
            options.AddPolicy("platform-admin", policy => policy.Requirements.Add(new PlatformAdminRequirement()));
            options.AddPolicy("tenant-admin", policy => policy.Requirements.Add(new TenantAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, TenantAdminAuthorizationHandler>();

        return services;
    }
}
