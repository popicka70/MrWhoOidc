using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.Auth.Services;
using System.Net.Http.Headers;
using System.Text;

namespace MrWhoOidc.WebAuth.Handlers;

public interface ITokenHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class TokenHandler(OidcOptions options, ITokenService tokens, IClientStore clients) : ITokenHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        if (!http.Request.HasFormContentType)
            return ErrorResults.InvalidRequest();

        // Client authentication: client_secret_basic or client_secret_post (optional)
        var (clientId, clientSecret) = ReadClientCredentials(http);

        var form = await http.Request.ReadFormAsync();
        var grantType = form["grant_type"].ToString();

        // Allow client_id/secret from body if not provided via Authorization header
        if (string.IsNullOrEmpty(clientId)) clientId = form["client_id"].ToString();
        if (string.IsNullOrEmpty(clientSecret)) clientSecret = form["client_secret"].ToString();

        if (string.IsNullOrWhiteSpace(clientId))
            return ErrorResults.InvalidRequest("Missing client_id");

        var validClient = await clients.ValidateClientSecretAsync(clientId, clientSecret);
        if (!validClient)
            return ErrorResults.UnauthorizedClient();

        if (string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
        {
            var code = form["code"].ToString();
            var redirectUri = form["redirect_uri"].ToString();
            var codeVerifier = form["code_verifier"].ToString();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(redirectUri))
                return ErrorResults.InvalidRequest();

            var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
            var (ok, payload, _, status) = await tokens.ExchangeAuthorizationCodeAsync(code, redirectUri, clientId, codeVerifier, issuer);
            return Results.Json(payload!, statusCode: status);
        }

        if (string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
        {
            var refresh = form["refresh_token"].ToString();
            if (string.IsNullOrWhiteSpace(refresh))
                return ErrorResults.InvalidRequest();

            var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
            var (ok, payload, _, status) = await tokens.ExchangeRefreshTokenAsync(refresh, clientId, issuer);
            return Results.Json(payload!, statusCode: status);
        }

        return ErrorResults.UnsupportedGrant();
    }

    static (string? clientId, string? clientSecret) ReadClientCredentials(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header)) return (null, null);
        if (!header.StartsWith("Basic ", StringComparison.Ordinal)) return (null, null);
        try
        {
            var raw = header.Substring("Basic ".Length).Trim();
            var bytes = Convert.FromBase64String(raw);
            var pair = Encoding.UTF8.GetString(bytes);
            var idx = pair.IndexOf(':');
            if (idx < 0) return (null, null);
            var id = pair[..idx];
            var secret = pair[(idx + 1)..];
            return (id, secret);
        }
        catch
        {
            return (null, null);
        }
    }
}
