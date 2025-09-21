using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using System.Net.Http.Headers;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IParHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class ParHandler(OidcOptions options, IClientStore clients, IClientAssertionValidator assertions, IAuthorizeService authorize, IPushedAuthorizationRequestStore parStore, IRequestObjectValidator requestObjects, IOptions<AuthOptions> authOptions) : IParHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        if (!http.Request.HasFormContentType)
        {
            return ErrorResults.InvalidRequest("Form content expected");
        }

        var form = await http.Request.ReadFormAsync();

        // Client authentication: private_key_jwt, basic, or post
        var (clientId, clientSecret) = ReadClientCredentials(http);
        if (string.IsNullOrEmpty(clientId)) clientId = form["client_id"].ToString();
        if (string.IsNullOrWhiteSpace(clientId)) return ErrorResults.InvalidRequest("Missing client_id");

        var clientAssertionType = form["client_assertion_type"].ToString();
        var clientAssertion = form["client_assertion"].ToString();
        var parEndpoint = (options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}") + "/par";

        bool authenticated = false;
        if (string.Equals(clientAssertionType, "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", StringComparison.Ordinal) && !string.IsNullOrEmpty(clientAssertion))
        {
            authenticated = await assertions.ValidateAsync(clientId, clientAssertion, parEndpoint);
        }
        else
        {
            if (string.IsNullOrEmpty(clientSecret)) clientSecret = form["client_secret"].ToString();
            authenticated = await clients.ValidateClientSecretAsync(clientId, clientSecret);
        }

        if (!authenticated) return ErrorResults.UnauthorizedClient();

        // Optional: object size limit
        var roJwt = form["request"].ToString();
        var maxBytes = authOptions.Value.RequestObjectMaxBytes;
        if (maxBytes > 0 && !string.IsNullOrEmpty(roJwt) && Encoding.UTF8.GetByteCount(roJwt) > maxBytes)
        {
            return Results.Json(new { error = "invalid_request_object", error_description = "request object too large" }, statusCode: 400);
        }

        // If a request object is provided, validate and extract fields; otherwise, build from form parameters
        AuthorizeRequest req;
        if (!string.IsNullOrEmpty(roJwt))
        {
            var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
            var aud = issuer.TrimEnd('/') + "/authorize";
            var validation = await requestObjects.ValidateAsync(roJwt, aud);
            if (!validation.IsValid)
            {
                return Results.Json(new { error = validation.Error, error_description = validation.ErrorDescription }, statusCode: 400);
            }

            // client_id in JWT must match the authenticated client_id
            if (!string.Equals(validation.ClientId, clientId, StringComparison.Ordinal))
            {
                return Results.Json(new { error = "invalid_request", error_description = "client_id mismatch between auth and request object" }, statusCode: 400);
            }

            req = validation.Request!;
            // Allow state from form to override state in request object
            var stateOverride = form["state"].ToString();
            if (!string.IsNullOrEmpty(stateOverride)) req.state = stateOverride;
        }
        else
        {
            // Build an authorization request from form parameters
            req = new AuthorizeRequest
            {
                response_type = form["response_type"],
                client_id = clientId,
                redirect_uri = form["redirect_uri"],
                scope = form["scope"],
                state = form["state"], // not used by PAR but harmless
                nonce = form["nonce"],
                code_challenge = form["code_challenge"],
                code_challenge_method = form["code_challenge_method"],
                resource = form["resource"],
            };
        }

        var result = await authorize.ValidateAsync(req);
        if (!result.IsValid)
        {
            return Results.Json(new { error = result.Error, error_description = result.ErrorDescription }, statusCode: 400);
        }

        // Generate opaque id (128-bit base64url without padding)
        string id;
        using (var rng = RandomNumberGenerator.Create())
        {
            Span<byte> bytes = stackalloc byte[16];
            rng.GetBytes(bytes);
            id = Convert.ToBase64String(bytes.ToArray()).TrimEnd('=')
                    .Replace('+', '-').Replace('/', '_');
        }

        var issuer2 = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
        var requestUri = issuer2.TrimEnd('/') + "/par/" + id;

        var expiresAt = parStore.Create(id, req, clientId!, TimeSpan.FromMinutes(5), requestUri);
        var expiresIn = (int)Math.Max(0, (expiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        return Results.Json(new { request_uri = requestUri, expires_in = expiresIn });
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
