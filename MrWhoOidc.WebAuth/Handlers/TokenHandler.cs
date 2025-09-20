using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers;

public interface ITokenHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class TokenHandler(OidcOptions options, ITokenService tokens) : ITokenHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        if (!http.Request.HasFormContentType)
            return ErrorResults.InvalidRequest();

        var form = await http.Request.ReadFormAsync();
        var grantType = form["grant_type"].ToString();

        if (string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
        {
            var code = form["code"].ToString();
            var redirectUri = form["redirect_uri"].ToString();
            var clientId = form["client_id"].ToString();
            var codeVerifier = form["code_verifier"].ToString();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(redirectUri) || string.IsNullOrWhiteSpace(clientId))
                return ErrorResults.InvalidRequest();

            var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
            var (ok, payload, _, status) = await tokens.ExchangeAuthorizationCodeAsync(code, redirectUri, clientId, codeVerifier, issuer);
            return Results.Json(payload!, statusCode: status);
        }

        if (string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
        {
            var refresh = form["refresh_token"].ToString();
            var clientId = form["client_id"].ToString();
            if (string.IsNullOrWhiteSpace(refresh) || string.IsNullOrWhiteSpace(clientId))
                return ErrorResults.InvalidRequest();

            var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
            var (ok, payload, _, status) = await tokens.ExchangeRefreshTokenAsync(refresh, clientId, issuer);
            return Results.Json(payload!, statusCode: status);
        }

        return ErrorResults.UnsupportedGrant();
    }
}
