using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Observability;
using System.Security.Cryptography;
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
    OidcOptions options) : IRevocationHandler
{
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

        var (clientIdHeader, clientSecretHeader) = ReadClientCredentials(http);

        var form = await http.Request.ReadFormAsync();
        var token = form[OAuthConstants.Parameters.Token].ToString();
        var hint = form[OAuthConstants.Parameters.TokenTypeHint].ToString();
        var clientId = !string.IsNullOrEmpty(clientIdHeader) ? clientIdHeader : form[OAuthConstants.Parameters.ClientId].ToString();
        var clientSecret = !string.IsNullOrEmpty(clientSecretHeader) ? clientSecretHeader : form[OAuthConstants.Parameters.ClientSecret].ToString();

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

        // private_key_jwt support
        var clientAssertionType = form[OAuthConstants.Parameters.ClientAssertionType].ToString();
        var clientAssertion = form[OAuthConstants.Parameters.ClientAssertion].ToString();
        var revocationEndpoint = http.GetIssuer(options) + "/revoke";

        // Optional mTLS client auth allow-list.
        // If configured for this client_id, mTLS is required and sufficient.
        if (authOptions.Value.RevocationMtlsCertificates is { Count: > 0 } &&
            authOptions.Value.RevocationMtlsCertificates.TryGetValue(clientId, out var allowed) &&
            allowed is { Length: > 0 })
        {
            var cert = http.Connection.ClientCertificate ?? await http.Connection.GetClientCertificateAsync();
            if (cert is null)
            {
                audit.Emit("revocation.client_auth.failed", new
                {
                    client_id = clientId,
                    method = "mtls",
                    reason = "certificate_missing",
                    ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
                });
                return ErrorResults.UnauthorizedClient("Client authentication failed (mtls_required)");
            }

            var presentedX5tS256 = mtlsThumbprintResolver.ResolveThumbprint(cert);
            var presentedHex = cert.GetCertHashString(HashAlgorithmName.SHA256);

            static bool HasValue(string? v) => !string.IsNullOrWhiteSpace(v);

            var match =
                (HasValue(presentedX5tS256) && allowed.Any(t => string.Equals(t, presentedX5tS256, StringComparison.OrdinalIgnoreCase))) ||
                (HasValue(presentedHex) && allowed.Any(t => string.Equals(t, presentedHex, StringComparison.OrdinalIgnoreCase)));

            if (!match)
            {
                audit.Emit("revocation.client_auth.failed", new
                {
                    client_id = clientId,
                    method = "mtls",
                    reason = "thumbprint_mismatch",
                    ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
                });
                return ErrorResults.UnauthorizedClient("Client authentication failed (mtls_required)");
            }

            var ipMtls = http.Connection.RemoteIpAddress?.ToString();
            await revocations.RevokeAsync(token, hint, clientId, ipMtls);
            audit.Emit("revocation.success", new
            {
                client_id = clientId,
                token_type_hint = string.IsNullOrWhiteSpace(hint) ? "none" : hint,
                method = "mtls",
                ip_hash = audit.HashValue(ipMtls)
            });
            return Results.Ok();
        }

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
        {
            audit.Emit("revocation.client_auth.failed", new
            {
                client_id = clientId,
                method = string.Equals(clientAssertionType, OAuthConstants.ClientAssertionTypes.JwtBearer, StringComparison.Ordinal) ? "private_key_jwt" : "client_secret",
                reason = "invalid_credentials",
                ip_hash = audit.HashValue(http.Connection.RemoteIpAddress?.ToString())
            });
            return ErrorResults.UnauthorizedClient("Client authentication failed");
        }

        var ip = http.Connection.RemoteIpAddress?.ToString();
        await revocations.RevokeAsync(token, hint, clientId, ip);
        audit.Emit("revocation.success", new
        {
            client_id = clientId,
            token_type_hint = string.IsNullOrWhiteSpace(hint) ? "none" : hint,
            method = string.Equals(clientAssertionType, OAuthConstants.ClientAssertionTypes.JwtBearer, StringComparison.Ordinal) ? "private_key_jwt" : "client_secret",
            ip_hash = audit.HashValue(ip)
        });
        return Results.Ok();
    }

    static (string? clientId, string? clientSecret) ReadClientCredentials(HttpContext http)
    {
        return MrWhoOidc.WebAuth.Infrastructure.BasicClientCredentialsParser.ReadFromAuthorizationHeader(http.Request.Headers.Authorization.ToString());
    }
}
