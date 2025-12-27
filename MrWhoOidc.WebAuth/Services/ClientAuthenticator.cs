using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authentication;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Extensions;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Services;

public class ClientAuthenticationContext
{
    public ClientAuthenticationUsage Usage { get; set; }
    public string? GrantType { get; set; } // Only for TokenEndpoint
}

public enum ClientAuthenticationMethod
{
    None,
    ClientSecretBasic,
    ClientSecretPost,
    PrivateKeyJwt,
    Mtls
}

public record ClientAuthenticationResult(bool IsSuccess, Client? Client, ClientAuthenticationMethod Method, IResult? ErrorResult);

public interface IClientAuthenticator
{
    Task<ClientAuthenticationResult> AuthenticateAsync(HttpContext http, ClientAuthenticationContext context);
}

public class ClientAuthenticator(
    IClientAuthenticationService authService,
    IMtlsThumbprintResolver mtlsResolver,
    ILogger<ClientAuthenticator> logger) : IClientAuthenticator
{
    public async Task<ClientAuthenticationResult> AuthenticateAsync(HttpContext http, ClientAuthenticationContext context)
    {
        logger.LogDebug("Starting client authentication for usage {Usage}", context.Usage);

        // 1. Extract Credentials
        string? clientId = null;
        string? clientSecret = null;
        string? clientAssertionType = null;
        string? clientAssertion = null;

        // Check Authorization Header (Basic)
        var (basicId, basicSecret) = ReadBasicAuth(http);
        bool usedBasic = false;
        if (!string.IsNullOrEmpty(basicId))
        {
            clientId = basicId;
            clientSecret = basicSecret;
            usedBasic = true;
        }

        // Check Form
        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync();
            if (string.IsNullOrEmpty(clientId))
            {
                clientId = form[OAuthConstants.Parameters.ClientId].ToString();
            }
            if (string.IsNullOrEmpty(clientSecret) && !usedBasic)
            {
                clientSecret = form[OAuthConstants.Parameters.ClientSecret].ToString();
            }
            clientAssertionType = form[OAuthConstants.Parameters.ClientAssertionType].ToString();
            clientAssertion = form[OAuthConstants.Parameters.ClientAssertion].ToString();
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new ClientAuthenticationResult(false, null, ClientAuthenticationMethod.None, Results.BadRequest(new { error = "invalid_request", error_description = "Missing client_id" }));
        }

        // 2. Get mTLS thumbprint if available
        var cert = await http.Connection.GetClientCertificateAsync();
        string? mtlsThumbprint = mtlsResolver.ResolveThumbprint(cert);

        // 3. Delegate to Auth Service
        var input = new ClientCredentialInput(
            ClientId: clientId,
            Usage: context.Usage,
            GrantType: context.GrantType,
            ClientSecret: clientSecret,
            ClientAssertionType: clientAssertionType,
            ClientAssertion: clientAssertion,
            MtlsThumbprint: mtlsThumbprint,
            EndpointUrl: http.GetEndpointUrl()
        );

        var result = await authService.AuthenticateAsync(input, http.RequestAborted);

        if (!result.IsSuccess)
        {
            if (result.Error == "invalid_client" && result.ErrorDescription == "mtls_required")
            {
                http.Response.Headers["WWW-Authenticate"] = "Bearer error=invalid_client, error_description=mtls_required";
                return new ClientAuthenticationResult(false, result.Client, ClientAuthenticationMethod.Mtls, Results.Unauthorized());
            }

            return new ClientAuthenticationResult(false, result.Client, ClientAuthenticationMethod.None, Results.BadRequest(new { error = result.Error ?? "unauthorized_client", error_description = result.ErrorDescription }));
        }

        // 4. Determine method for WebAuth result
        var method = ClientAuthenticationMethod.None;
        if (!string.IsNullOrEmpty(clientAssertion)) method = ClientAuthenticationMethod.PrivateKeyJwt;
        else if (usedBasic) method = ClientAuthenticationMethod.ClientSecretBasic;
        else if (!string.IsNullOrEmpty(clientSecret)) method = ClientAuthenticationMethod.ClientSecretPost;
        else if (!string.IsNullOrEmpty(mtlsThumbprint)) method = ClientAuthenticationMethod.Mtls;

        return new ClientAuthenticationResult(true, result.Client, method, null);
    }

    private static (string? clientId, string? clientSecret) ReadBasicAuth(HttpContext http)
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
