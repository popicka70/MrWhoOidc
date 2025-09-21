using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MrWhoOidc.Web.DPoP;

namespace MrWhoOidc.Web;

public static class AuthWithDPoP
{
    public static IServiceCollection AddDPoPAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<DPoPKeyStore>();
        services.PostConfigureAll<OpenIdConnectOptions>(o =>
        {
            o.Events.OnAuthorizationCodeReceived = async ctx =>
            {
                // Inject DPoP header into the backchannel token request
                var services = ctx.HttpContext.RequestServices;
                var keyStore = services.GetRequiredService<DPoPKeyStore>();
                var (key, jwk) = keyStore.GetOrCreateKey();

                var authority = ctx.Options.Authority?.TrimEnd('/') ?? string.Empty;
                var tokenEndpoint = string.IsNullOrEmpty(authority) ? "/token" : authority + "/token";
                var proof = DPoPProof.Create(key, jwk, "POST", tokenEndpoint);

                // Attach header on the Backchannel used by OIDC handler
                var backchannel = ctx.Options.Backchannel;
                backchannel.DefaultRequestHeaders.Remove("DPoP");
                backchannel.DefaultRequestHeaders.Add("DPoP", proof);

                await Task.CompletedTask;
            };
        });
        return services;
    }
}
