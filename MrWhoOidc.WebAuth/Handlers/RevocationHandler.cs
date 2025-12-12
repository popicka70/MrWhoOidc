using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IRevocationHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class RevocationHandler(IRevocationService revocations, IClientStore clients, OidcMetrics metrics, IClientAssertionValidator assertions, OidcOptions options) : IRevocationHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        http.Response.Headers["Cache-Control"] = "no-store";
        http.Response.Headers["Pragma"] = "no-cache";

        metrics.RevocationRequests.Add(1);

        if (!http.Request.HasFormContentType)
            return ErrorResults.InvalidRequest("Content-Type must be application/x-www-form-urlencoded");

        var (clientIdHeader, clientSecretHeader) = ReadClientCredentials(http);

        var form = await http.Request.ReadFormAsync();
        var token = form[OAuthConstants.Parameters.Token].ToString();
        var hint = form[OAuthConstants.Parameters.TokenTypeHint].ToString();
        var clientId = !string.IsNullOrEmpty(clientIdHeader) ? clientIdHeader : form[OAuthConstants.Parameters.ClientId].ToString();
        var clientSecret = !string.IsNullOrEmpty(clientSecretHeader) ? clientSecretHeader : form[OAuthConstants.Parameters.ClientSecret].ToString();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId))
            return ErrorResults.InvalidRequest("token and client_id are required");

        // private_key_jwt support
        var clientAssertionType = form[OAuthConstants.Parameters.ClientAssertionType].ToString();
        var clientAssertion = form[OAuthConstants.Parameters.ClientAssertion].ToString();
        var revocationEndpoint = http.GetIssuer(options) + "/revoke";

        bool authenticated = false;
        if (string.Equals(clientAssertionType, OAuthConstants.ClientAssertionTypes.JwtBearer, StringComparison.Ordinal) && !string.IsNullOrEmpty(clientAssertion))
        {
            authenticated = await assertions.ValidateAsync(clientId, clientAssertion, revocationEndpoint);
        }
        else
        {
            authenticated = await clients.ValidateClientSecretAsync(clientId, clientSecret);
        }

        if (!authenticated)
            return ErrorResults.UnauthorizedClient("Client authentication failed");

        var ip = http.Connection.RemoteIpAddress?.ToString();
        await revocations.RevokeAsync(token, hint, clientId, ip);
        return Results.Ok();
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
            var pair = System.Text.Encoding.UTF8.GetString(bytes);
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
