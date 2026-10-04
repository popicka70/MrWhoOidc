using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authentication;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Handlers;
using System.Security.Cryptography;
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
        string? formClientId = null;
        string? formClientSecret = null;
        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync();
            formClientId = form[OAuthConstants.Parameters.ClientId].ToString();
            formClientSecret = form[OAuthConstants.Parameters.ClientSecret].ToString();
            if (string.IsNullOrEmpty(clientId))
            {
                clientId = formClientId;
            }
            if (string.IsNullOrEmpty(clientSecret) && !usedBasic)
            {
                clientSecret = formClientSecret;
            }
            clientAssertionType = form[OAuthConstants.Parameters.ClientAssertionType].ToString();
            clientAssertion = form[OAuthConstants.Parameters.ClientAssertion].ToString();
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new ClientAuthenticationResult(false, null, ClientAuthenticationMethod.None, Results.BadRequest(new { error = "invalid_request", error_description = "Missing client_id" }));
        }

        // RFC 6749 §2.3: a client MUST NOT use more than one authentication method per request.
        var hasAssertion = !string.IsNullOrEmpty(clientAssertion);
        if ((usedBasic && !string.IsNullOrEmpty(formClientSecret)) ||
            (hasAssertion && !string.IsNullOrEmpty(clientSecret)))
        {
            return Fail(http, "multiple client authentication methods");
        }
        if (usedBasic && !string.IsNullOrEmpty(formClientId) && !string.Equals(formClientId, clientId, StringComparison.Ordinal))
        {
            return Fail(http, "client_id mismatch");
        }

        // Diagnostics (never log secrets/assertions): help troubleshoot token endpoint failures.
        if (context.Usage == ClientAuthenticationUsage.TokenEndpoint)
        {
            logger.LogInformation(
                "Token client authentication input: client={ClientIdHash}, usedBasic={UsedBasic}, hasSecret={HasSecret}, hasAssertion={HasAssertion}, grant_type={GrantType}, path={Path}",
                Bucketization.Bucket(clientId),
                usedBasic,
                !string.IsNullOrWhiteSpace(clientSecret),
                !string.IsNullOrWhiteSpace(clientAssertion),
                context.GrantType,
                http.Request.Path.Value);
        }

        // 2. Get mTLS thumbprint if available
        var cert = await http.Connection.GetClientCertificateAsync();
        string? mtlsThumbprint = mtlsResolver.ResolveThumbprint(cert);
        string? mtlsThumbprintHex = cert?.GetCertHashString(HashAlgorithmName.SHA256);

        // 3. Delegate to Auth Service
        var input = new ClientCredentialInput(
            ClientId: clientId,
            Usage: context.Usage,
            GrantType: context.GrantType,
            ClientSecret: clientSecret,
            ClientAssertionType: clientAssertionType,
            ClientAssertion: clientAssertion,
            MtlsThumbprint: mtlsThumbprint,
            MtlsThumbprintHexSha256: mtlsThumbprintHex,
            EndpointUrl: http.GetEndpointUrl(),
            // RequestServices is always set in the pipeline; guarded for handler-level unit tests.
            Issuer: http.RequestServices is null ? null : http.GetIssuer()
        );

        var result = await authService.AuthenticateAsync(input, http.RequestAborted);

        if (!result.IsSuccess)
        {
            if (result.Error == "invalid_client" && result.ErrorDescription == "mtls_required")
            {
                http.Response.Headers["WWW-Authenticate"] = "Bearer error=invalid_client, error_description=mtls_required";
                return new ClientAuthenticationResult(false, result.Client, ClientAuthenticationMethod.Mtls, Results.Unauthorized());
            }

            return Fail(http, result.ErrorDescription, result.Client);
        }

        // 4. Determine method for WebAuth result
        var method = ClientAuthenticationMethod.None;
        if (hasAssertion) method = ClientAuthenticationMethod.PrivateKeyJwt;
        else if (usedBasic) method = ClientAuthenticationMethod.ClientSecretBasic;
        else if (!string.IsNullOrEmpty(clientSecret)) method = ClientAuthenticationMethod.ClientSecretPost;
        else if (!string.IsNullOrEmpty(mtlsThumbprint)) method = ClientAuthenticationMethod.Mtls;

        // 5. Enforce the client's registered authentication method (RFC 7591 token_endpoint_auth_method).
        if (!IsMethodAllowed(result.Client!, method))
        {
            logger.LogWarning("Client authentication rejected: method {Method} not allowed for client {ClientIdHash}", method, Bucketization.Bucket(clientId));
            return Fail(http, "authentication method not allowed for this client", result.Client);
        }

        return new ClientAuthenticationResult(true, result.Client, method, null);
    }

    /// <summary>
    /// The <c>token_endpoint_auth_method</c> values this server can actually enforce (see
    /// <see cref="IsMethodAllowed"/>). Single source for discovery
    /// (<c>token_endpoint_auth_methods_supported</c>) and dynamic client registration.
    /// <c>tls_client_auth</c> is omitted: only certificate thumbprints are matched, not subject DNs.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedTokenEndpointAuthMethods =
    [
        "none",
        "client_secret_basic",
        "client_secret_post",
        "private_key_jwt",
        "self_signed_tls_client_auth"
    ];

    internal static bool IsMethodAllowed(Client client, ClientAuthenticationMethod method)
    {
        var registered = client.TokenEndpointAuthMethod;
        if (!string.IsNullOrEmpty(registered))
        {
            return registered switch
            {
                "none" => method == ClientAuthenticationMethod.None,
                "client_secret_basic" => method == ClientAuthenticationMethod.ClientSecretBasic,
                "client_secret_post" => method == ClientAuthenticationMethod.ClientSecretPost,
                "private_key_jwt" => method == ClientAuthenticationMethod.PrivateKeyJwt,
                "self_signed_tls_client_auth" or "tls_client_auth" => method == ClientAuthenticationMethod.Mtls,
                _ => false,
            };
        }

        // No explicit registration (admin-created clients): honour the per-method toggles.
        return method switch
        {
            ClientAuthenticationMethod.ClientSecretBasic => client.AllowClientSecretBasic,
            ClientAuthenticationMethod.ClientSecretPost => client.AllowClientSecretPost,
            ClientAuthenticationMethod.PrivateKeyJwt => client.AllowPrivateKeyJwt,
            _ => true,
        };
    }

    /// <summary>
    /// RFC 6749 §5.2: client authentication failures are <c>401 invalid_client</c>, with a
    /// <c>WWW-Authenticate: Basic</c> challenge when HTTP Basic was attempted (see <see cref="ErrorResults.InvalidClient"/>).
    /// </summary>
    private static ClientAuthenticationResult Fail(HttpContext http, string? description, Client? client = null)
        => new(false, client, ClientAuthenticationMethod.None, ErrorResults.InvalidClient(http, description));

    private static (string? clientId, string? clientSecret) ReadBasicAuth(HttpContext http)
    {
        return MrWhoOidc.WebAuth.Infrastructure.BasicClientCredentialsParser.ReadFromAuthorizationHeader(http.Request.Headers.Authorization.ToString());
    }
}
