using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Security.Admin;
using MrWhoOidc.WebAuth.Security.ApiBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.Auth.Services.SupportAccess;
using Microsoft.AspNetCore.WebUtilities;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Phase 2 extraction (narrow): cookie authentication schemes + admin authorization policy/handler.
/// Intentionally does NOT register AuthOptions, metrics, or JWKS cache to minimize blast radius.
/// </summary>
public static class AuthenticationAuthorizationExtensions
{
    public static IServiceCollection AddMrWhoOidcAuthAndAdmin(this IServiceCollection services, IConfiguration _)
    {
        // Authentication schemes:
        // - "auto" policy scheme: auto-routes to "api-bearer" when Authorization: Bearer is present,
        //   otherwise falls back to cookies. This lets CLI/API clients use Bearer tokens while
        //   all existing browser/cookie flows continue unchanged.
        // - "api-bearer": validates JWTs issued by this server using ITokenValidator.
        // - "Cookies" / "preauth": existing session cookie schemes.
        services.AddAuthentication("auto")
            .AddPolicyScheme("auto", "auto", policyOptions =>
            {
                static string PickScheme(HttpContext context)
                {
                    var auth = context.Request.Headers.Authorization.ToString();
                    return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? ApiTokenAuthHandler.SchemeName
                        : CookieAuthenticationDefaults.AuthenticationScheme;
                }
                policyOptions.ForwardDefaultSelector = PickScheme;
            })
            .AddScheme<ApiTokenAuthOptions, ApiTokenAuthHandler>(ApiTokenAuthHandler.SchemeName, _ => { })
            .AddCookie(options =>
            {
                options.Cookie.Name = "__Host-mrwhooidc-auth";
                options.Cookie.Path = "/";
                options.LoginPath = "/login";
                options.LogoutPath = "/logout";
                options.SlidingExpiration = true;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;

                // Handle tenant-aware redirects for unauthorized/unauthenticated requests
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = async context =>
                    {
                        // Get tenant context from current request
                        var tenantAccessor = context.HttpContext.RequestServices.GetService<ITenantAccessor>();
                        var multiTenancyOptions = context.HttpContext.RequestServices.GetService<IMultiTenancyOptions>();
                        var continuationStore = context.HttpContext.RequestServices.GetService<ILoginContinuationStore>();

                        var currentTenant = tenantAccessor?.CurrentTenant;
                        var loginPath = currentTenant != null && multiTenancyOptions?.Enabled == true
                            ? $"/t/{currentTenant.Slug}/login"
                            : "/login";

                        // Default: tenant-aware login redirect.
                        // Improvement: move large ReturnUrl out of the query string to avoid Kestrel 400s
                        // caused by oversized request lines when posting back to /login.
                        var redirectUri = BuildLoginRedirectUri(context.RedirectUri, loginPath, continuationStore, context.HttpContext.RequestAborted);
                        context.Response.Redirect(await redirectUri);
                        return;
                    },
                    OnRedirectToAccessDenied = context =>
                    {
                        // Get tenant context from current request
                        var tenantAccessor = context.HttpContext.RequestServices.GetService<ITenantAccessor>();
                        var multiTenancyOptions = context.HttpContext.RequestServices.GetService<IMultiTenancyOptions>();

                        var currentTenant = tenantAccessor?.CurrentTenant;
                        var accessDeniedPath = currentTenant != null && multiTenancyOptions?.Enabled == true
                            ? $"/t/{currentTenant.Slug}/account/access-denied"
                            : "/account/access-denied";

                        // Build redirect with returnUrl
                        var returnUrl = context.Request.Path + context.Request.QueryString;
                        context.Response.Redirect($"{accessDeniedPath}?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
                        return Task.CompletedTask;
                    }
                };
            })
            .AddCookie("preauth", options =>
            {
                options.Cookie.Name = "__Host-mrwhooidc-preauth";
                options.Cookie.Path = "/";
                options.LoginPath = "/login";
                options.SlidingExpiration = true;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);

                // Keep MFA redirect-to-login safe even when ReturnUrl is large.
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = async context =>
                    {
                        var tenantAccessor = context.HttpContext.RequestServices.GetService<ITenantAccessor>();
                        var multiTenancyOptions = context.HttpContext.RequestServices.GetService<IMultiTenancyOptions>();
                        var continuationStore = context.HttpContext.RequestServices.GetService<ILoginContinuationStore>();

                        var currentTenant = tenantAccessor?.CurrentTenant;
                        var loginPath = currentTenant != null && multiTenancyOptions?.Enabled == true
                            ? $"/t/{currentTenant.Slug}/login"
                            : "/login";

                        var redirectUri = BuildLoginRedirectUri(context.RedirectUri, loginPath, continuationStore, context.HttpContext.RequestAborted);
                        context.Response.Redirect(await redirectUri);
                    }
                };
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

        // Tenant Support Access Store (durable session persistence for platform-admin support access)
        services.AddScoped<ITenantSupportAccessStore, TenantSupportAccessStore>();

        // EffectiveAccessContext accessor — resolves immutable request-level context per AD-1.
        // Evaluates Tenant Support Access, Delegated Access Grant, and normal fallback in priority order.
        services.AddScoped<IEffectiveAccessContextAccessor>(sp => new EffectiveAccessContextAccessor(
            sp.GetRequiredService<IHttpContextAccessor>(),
            sp.GetRequiredService<AuthDbContext>(),
            sp.GetRequiredService<IOptions<AuthOptions>>(),
            sp.GetRequiredService<ILogger<EffectiveAccessContextAccessor>>()));

        return services;
    }

    private static async Task<string> BuildLoginRedirectUri(
        string originalRedirectUri,
        string loginPath,
        ILoginContinuationStore? continuationStore,
        CancellationToken cancellationToken)
    {
        // Cookie auth typically emits an absolute redirect like:
        //   https://host/login?ReturnUrl=%2Fauthorize%3F...
        // We preserve scheme/authority, replace path with tenant-aware loginPath,
        // and replace ReturnUrl with a short ctx key when possible.

        if (!Uri.TryCreate(originalRedirectUri, UriKind.Absolute, out var uri))
        {
            // Fallback: best-effort string replace
            return originalRedirectUri.Replace("/login", loginPath, StringComparison.OrdinalIgnoreCase);
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        string? returnUrl = null;
        if (query.TryGetValue("ReturnUrl", out var ru) && ru.Count > 0)
        {
            returnUrl = ru[0];
        }

        var baseUri = uri.GetLeftPart(UriPartial.Authority) + loginPath;

        // Preserve any other query params, but remove ReturnUrl.
        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in query)
        {
            if (string.Equals(kvp.Key, "ReturnUrl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            parameters[kvp.Key] = kvp.Value.Count > 0 ? kvp.Value[0] : null;
        }

        if (!string.IsNullOrEmpty(returnUrl) && continuationStore is not null)
        {
            var ctxKey = await continuationStore.StoreAsync(returnUrl, cancellationToken);
            parameters["ctx"] = ctxKey;
        }
        else if (!string.IsNullOrEmpty(returnUrl))
        {
            // Last resort: keep ReturnUrl in query.
            parameters["ReturnUrl"] = returnUrl;
        }

        return parameters.Count == 0 ? baseUri : QueryHelpers.AddQueryString(baseUri, parameters);
    }
}
