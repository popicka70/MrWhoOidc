using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Extensions;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Services;

public enum ClientAuthenticationUsage
{
    TokenEndpoint,
    Introspection,
    Revocation
}

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
    IClientStore clientStore,
    IClientAssertionValidator assertionValidator,
    IOptions<AuthOptions> authOptions,
    ILogger<ClientAuthenticator> logger) : IClientAuthenticator
{
    public async Task<ClientAuthenticationResult> AuthenticateAsync(HttpContext http, ClientAuthenticationContext context)
    {
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

        // 2. Load Client
        var client = await clientStore.FindByClientIdAsync(clientId);
        if (client is null)
        {
            return new ClientAuthenticationResult(false, null, ClientAuthenticationMethod.None, Results.BadRequest(new { error = "unauthorized_client", error_description = "Unknown client" }));
        }

        // 3. mTLS Checks
        if (ShouldCheckMtls(context, client))
        {
            var allowedThumbprints = GetAllowedMtlsThumbprints(context, client, clientId);
            if (allowedThumbprints is { Length: > 0 })
            {
                var cert = await http.Connection.GetClientCertificateAsync();
                var presented = cert?.Thumbprint;
                var ok = !string.IsNullOrEmpty(presented) && allowedThumbprints.Any(a => string.Equals(a, presented, StringComparison.OrdinalIgnoreCase));
                if (!ok)
                {
                    logger.LogWarning("Client authentication failed: mTLS required but missing/invalid for client {ClientIdHash}", Bucketization.Bucket(clientId));
                    http.Response.Headers["WWW-Authenticate"] = "Bearer error=invalid_client, error_description=mtls_required";
                    return new ClientAuthenticationResult(false, client, ClientAuthenticationMethod.Mtls, Results.Unauthorized());
                }
            }
        }

        // 4. Authenticate (Secret or Assertion)
        bool authenticated = false;
        bool usedPrivateKeyJwt = false;

        if (string.Equals(clientAssertionType, OAuthConstants.ClientAssertionTypes.JwtBearer, StringComparison.Ordinal) && !string.IsNullOrEmpty(clientAssertion))
        {
            if (!client.AllowPrivateKeyJwt)
            {
                logger.LogWarning("Client authentication failed: private_key_jwt disabled for client {ClientIdHash}", Bucketization.Bucket(clientId));
                return new ClientAuthenticationResult(false, client, ClientAuthenticationMethod.PrivateKeyJwt, Results.BadRequest(new { error = "unauthorized_client", error_description = "private_key_jwt disabled" }));
            }
            usedPrivateKeyJwt = true;
            
            // Determine endpoint URL for audience validation
            var endpoint = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.Path}";
            
            authenticated = await assertionValidator.ValidateAsync(clientId, clientAssertion, endpoint);
            if (!authenticated)
            {
                logger.LogWarning("Client authentication failed: private_key_jwt validation failed for client {ClientIdHash}", Bucketization.Bucket(clientId));
            }
        }
        else
        {
            // Client Secret
            // Check policies if Client Credentials Grant
            if (context.Usage == ClientAuthenticationUsage.TokenEndpoint && 
                string.Equals(context.GrantType, OAuthConstants.GrantTypes.ClientCredentials, StringComparison.Ordinal))
            {
#pragma warning disable CS0618
                if (string.IsNullOrEmpty(client.ClientSecretHash))
                {
                    logger.LogWarning("Client authentication failed: public client not allowed for client_credentials {ClientIdHash}", Bucketization.Bucket(clientId));
                    return new ClientAuthenticationResult(false, client, ClientAuthenticationMethod.None, Results.BadRequest(new { error = "unauthorized_client" }));
                }
#pragma warning restore CS0618

                if (usedBasic && !client.AllowClientSecretBasic)
                {
                    logger.LogWarning("Client authentication failed: client_secret_basic disabled for client {ClientIdHash}", Bucketization.Bucket(clientId));
                    return new ClientAuthenticationResult(false, client, ClientAuthenticationMethod.ClientSecretBasic, Results.BadRequest(new { error = "unauthorized_client", error_description = "client_secret_basic disabled" }));
                }
                if (!usedBasic && !client.AllowClientSecretPost)
                {
                    logger.LogWarning("Client authentication failed: client_secret_post disabled for client {ClientIdHash}", Bucketization.Bucket(clientId));
                    return new ClientAuthenticationResult(false, client, ClientAuthenticationMethod.ClientSecretPost, Results.BadRequest(new { error = "unauthorized_client", error_description = "client_secret_post disabled" }));
                }
            }

            authenticated = await clientStore.ValidateClientSecretAsync(clientId, clientSecret);
            if (!authenticated)
            {
                logger.LogWarning("Client authentication failed: secret validation failed for client {ClientIdHash}", Bucketization.Bucket(clientId));
            }
        }

        if (!authenticated)
        {
            return new ClientAuthenticationResult(false, client, ClientAuthenticationMethod.None, Results.BadRequest(new { error = "unauthorized_client" }));
        }

        var method = usedPrivateKeyJwt ? ClientAuthenticationMethod.PrivateKeyJwt : (usedBasic ? ClientAuthenticationMethod.ClientSecretBasic : ClientAuthenticationMethod.ClientSecretPost);
        return new ClientAuthenticationResult(true, client, method, null);
    }

    private bool ShouldCheckMtls(ClientAuthenticationContext context, Client client)
    {
        if (context.Usage == ClientAuthenticationUsage.TokenEndpoint && 
            string.Equals(context.GrantType, OAuthConstants.GrantTypes.ClientCredentials, StringComparison.Ordinal))
        {
            return true;
        }
        if (context.Usage == ClientAuthenticationUsage.Introspection)
        {
            return true;
        }
        return false;
    }

    private string[]? GetAllowedMtlsThumbprints(ClientAuthenticationContext context, Client client, string clientId)
    {
        if (context.Usage == ClientAuthenticationUsage.TokenEndpoint)
        {
            if (!string.IsNullOrWhiteSpace(client.M2MMtlsThumbprintsJson))
            {
                try { return JsonSerializer.Deserialize<string[]>(client.M2MMtlsThumbprintsJson); }
                catch { return null; }
            }
        }
        else if (context.Usage == ClientAuthenticationUsage.Introspection)
        {
             // Check client-specific
            if (!string.IsNullOrEmpty(client.IntrospectionMtlsThumbprintsJson))
            {
                try { return JsonSerializer.Deserialize<string[]>(client.IntrospectionMtlsThumbprintsJson); }
                catch { }
            }
            // Check global
            if (authOptions.Value.IntrospectionMtlsCertificates is { Count: > 0 })
            {
                if (authOptions.Value.IntrospectionMtlsCertificates.TryGetValue(clientId, out var thumbprints))
                {
                    return thumbprints;
                }
            }
        }
        return null;
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
