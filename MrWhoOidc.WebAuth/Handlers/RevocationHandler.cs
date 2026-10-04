using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.Auth.Services.Authentication;
using MrWhoOidc.WebAuth.Services;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IRevocationHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class RevocationHandler(
    IRevocationService revocations,
    IClientStore clients,
    IAuditSink audit,
    OidcEndpointMetrics metrics,
    IClientAssertionValidator assertions,
    IOptions<AuthOptions> authOptions,
    IMtlsThumbprintResolver mtlsThumbprintResolver,
    OidcOptions options,
    IClientAuthenticator? clientAuthenticator = null) : IRevocationHandler
{
    private readonly IClientAuthenticator _clientAuthenticator =
        clientAuthenticator ?? ClientAuthenticator.Compose(clients, assertions, authOptions, mtlsThumbprintResolver);

    public async Task<IResult> HandleAsync(HttpContext http)
    {
        http.Response.Headers["Cache-Control"] = "no-store";
        http.Response.Headers["Pragma"] = "no-cache";

        metrics.RevocationRequests.Add(1);

        if (!http.Request.HasFormContentType)
        {
            audit.Emit("revocation.request.invalid", new
            {
                reason = "invalid_content_type",
                ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
            });
            return ErrorResults.InvalidRequest("Content-Type must be application/x-www-form-urlencoded");
        }

        var (clientIdHeader, _) = ReadClientCredentials(http);

        var form = await http.Request.ReadFormAsync();
        var token = form[OAuthConstants.Parameters.Token].ToString();
        var hint = form[OAuthConstants.Parameters.TokenTypeHint].ToString();
        var clientId = !string.IsNullOrEmpty(clientIdHeader) ? clientIdHeader : form[OAuthConstants.Parameters.ClientId].ToString();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId))
        {
            audit.Emit("revocation.request.invalid", new
            {
                reason = "missing_token_or_client",
                client_id = clientId,
                ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
            });
            return ErrorResults.InvalidRequest("token and client_id are required");
        }

        // Shared client authentication: single method per request, Basic/form client_id match, the
        // registered token_endpoint_auth_method, and the AuthOptions.RevocationMtlsCertificates allow-list
        // (when configured for the client, mTLS is required and sufficient). Public clients may revoke
        // their own tokens (RFC 7009 §2.1).
        var issuer = http.GetIssuer(options);
        var auth = await _clientAuthenticator.AuthenticateAsync(http, new ClientAuthenticationContext
        {
            Usage = ClientAuthenticationUsage.Revocation,
            AdditionalAudiences = [issuer + "/revoke", issuer]
        });

        if (!auth.IsSuccess || auth.Client is null)
        {
            audit.Emit("revocation.client_auth.failed", new
            {
                client_id = clientId,
                method = DescribeAttemptedMethod(http, form, clientId),
                reason = "invalid_credentials",
                ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
            });
            return auth.ErrorResult ?? ErrorResults.InvalidClient(http, "Client authentication failed");
        }

        var ip = http.Connection.RemoteIpAddress?.ToString();
        await revocations.RevokeAsync(token, hint, auth.Client.ClientId, ip);
        audit.Emit("revocation.success", new
        {
            client_id = auth.Client.ClientId,
            token_type_hint = string.IsNullOrWhiteSpace(hint) ? "none" : hint,
            method = DescribeMethod(auth.Method),
            ip_hash = audit.HashValue(ip)
        });
        return Results.Ok();
    }

    private string DescribeAttemptedMethod(HttpContext http, IFormCollection form, string clientId)
    {
        if (string.Equals(form[OAuthConstants.Parameters.ClientAssertionType].ToString(), OAuthConstants.ClientAssertionTypes.JwtBearer, StringComparison.Ordinal))
        {
            return "private_key_jwt";
        }
        return authOptions.Value.RevocationMtlsCertificates?.ContainsKey(clientId) == true ? "mtls" : "client_secret";
    }

    private static string DescribeMethod(ClientAuthenticationMethod method) => method switch
    {
        ClientAuthenticationMethod.PrivateKeyJwt => "private_key_jwt",
        ClientAuthenticationMethod.Mtls => "mtls",
        ClientAuthenticationMethod.None => "none",
        _ => "client_secret"
    };

    static (string? clientId, string? clientSecret) ReadClientCredentials(HttpContext http)
    {
        return MrWhoOidc.WebAuth.Infrastructure.BasicClientCredentialsParser.ReadFromAuthorizationHeader(http.Request.Headers.Authorization.ToString());
    }
}
