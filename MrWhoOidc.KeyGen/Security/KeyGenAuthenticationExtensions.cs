using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MrWhoOidc.KeyGen.Configuration;

namespace MrWhoOidc.KeyGen.Security;

/// <summary>
/// Wires OIDC sign-in (code flow + PKCE, cookie session) and a fallback authorization policy
/// that requires <see cref="KeyGenAuthOptions.RequiredRole"/> on every endpoint that is not
/// explicitly <c>[AllowAnonymous]</c>.
/// </summary>
public static class KeyGenAuthenticationExtensions
{
    public const string CookieName = "__Host-MrWhoKeyGen";

    /// <summary>
    /// Registers authentication and authorization. Throws <see cref="InvalidOperationException"/>
    /// (refusing to start) when the configuration is not usable in the current environment.
    /// </summary>
    public static KeyGenAuthOptions AddKeyGenAuthentication(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(KeyGenAuthOptions.SectionName);
        var authOptions = section.Get<KeyGenAuthOptions>() ?? new KeyGenAuthOptions();

        var errors = authOptions.Validate(builder.Environment.IsDevelopment());
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "KeyGen authentication is not configured; refusing to start. " +
                string.Join(" ", errors) +
                $" Configure OIDC under '{KeyGenAuthOptions.SectionName}' (env: KeyGen__Auth__Authority, KeyGen__Auth__ClientId)," +
                $" or in Development only set KeyGen__Auth__DisableInDevelopment=true.");
        }

        builder.Services.Configure<KeyGenAuthOptions>(section);

        if (authOptions.DisableInDevelopment)
        {
            builder.Services
                .AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
                    DevelopmentAuthenticationHandler.SchemeName, _ => { });
        }
        else
        {
            AddOidc(builder.Services, authOptions);
        }

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim(authOptions.RoleClaimType, authOptions.RequiredRole)
                .Build());

        return authOptions;
    }

    private static void AddOidc(IServiceCollection services, KeyGenAuthOptions authOptions)
    {
        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                options.SlidingExpiration = true;
                options.AccessDeniedPath = "/AccessDenied";
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    if (IsApiRequest(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = authOptions.Authority;
                options.ClientId = authOptions.ClientId;
                options.ClientSecret = string.IsNullOrWhiteSpace(authOptions.ClientSecret) ? null : authOptions.ClientSecret;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.RequireHttpsMetadata = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;

                // MrWhoOidc gates PAR behind a license; the demo client disables it for the same reason.
                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;

                options.Scope.Clear();
                foreach (var scope in authOptions.Scopes.Append("openid").Distinct(StringComparer.Ordinal))
                {
                    options.Scope.Add(scope);
                }

                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = authOptions.RoleClaimType;

                // Roles may arrive only from userinfo; keep them (arrays become one claim per role).
                options.ClaimActions.MapJsonKey(authOptions.RoleClaimType, authOptions.RoleClaimType);

                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    // API callers get a plain 401 instead of a redirect to the IdP login page.
                    if (IsApiRequest(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.HandleResponse();
                    }

                    return Task.CompletedTask;
                };
            });
    }

    private static bool IsApiRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
}
