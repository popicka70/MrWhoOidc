using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Handlers;
using System.Net.Http.Headers;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MrWhoOidc.WebAuth.Observability;
using System.Diagnostics;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IParHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class ParHandler(OidcOptions options, IClientStore clients, IClientAssertionValidator assertions, IAuthorizeService authorize, IPushedAuthorizationRequestStore parStore, IRequestObjectValidator requestObjects, IOptions<AuthOptions> authOptions, OidcMetrics metrics, ILogger<ParHandler> logger) : IParHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var corr = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        metrics.ParRequests.Add(1);
        if (!http.Request.HasFormContentType)
        {
            metrics.ParFailures.Add(1);
            return ErrorResults.InvalidRequest("Form content expected", correlationId: corr);
        }

        var form = await http.Request.ReadFormAsync();

        // Size metric for request object if present
        var roJwtRaw = form["request"].ToString();
        if (!string.IsNullOrEmpty(roJwtRaw))
        {
            metrics.ParRequestSizeBytes.Record(System.Text.Encoding.UTF8.GetByteCount(roJwtRaw));
        }

        // Client authentication: private_key_jwt, basic, or post
        var (clientId, clientSecretFromHeader) = ReadClientCredentials(http);
        if (string.IsNullOrEmpty(clientId)) clientId = form[OAuthConstants.Parameters.ClientId].ToString();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            metrics.ParFailures.Add(1);
            return ErrorResults.InvalidRequest("Missing client_id", correlationId: corr);
        }

        var clientAssertionType = form[OAuthConstants.Parameters.ClientAssertionType].ToString();
        var clientAssertion = form[OAuthConstants.Parameters.ClientAssertion].ToString();
        var parEndpoint = http.GetIssuer(options) + "/par";

        bool authenticated = false;
        string authAttemptMode;
        string? authFailureDetail = null; // will be set if authentication ultimately fails

        if (string.Equals(clientAssertionType, OAuthConstants.ClientAssertionTypes.JwtBearer, StringComparison.Ordinal) && !string.IsNullOrEmpty(clientAssertion))
        {
            authAttemptMode = "private_key_jwt";
            authenticated = await assertions.ValidateAsync(clientId, clientAssertion, parEndpoint).ConfigureAwait(false);
            if (!authenticated)
            {
                authFailureDetail = "invalid_private_key_jwt"; // signature / claims / audience / key mismatch
            }
        }
        else
        {
            // Distinguish between basic and post usage
            string? clientSecretFinal = clientSecretFromHeader;
            bool usedBasic = !string.IsNullOrEmpty(clientSecretFromHeader);
            if (string.IsNullOrEmpty(clientSecretFinal))
            {
                clientSecretFinal = form[OAuthConstants.Parameters.ClientSecret].ToString();
            }
            bool usedPost = !usedBasic && !string.IsNullOrEmpty(clientSecretFinal);
            authAttemptMode = usedBasic ? "client_secret_basic" : usedPost ? "client_secret_post" : "no_credentials";
            authenticated = await clients.ValidateClientSecretAsync(clientId, clientSecretFinal).ConfigureAwait(false);
            if (!authenticated)
            {
                authFailureDetail = authAttemptMode switch
                {
                    "client_secret_basic" => string.IsNullOrEmpty(clientSecretFinal) ? "missing_basic_secret" : "invalid_basic_secret",
                    "client_secret_post" => string.IsNullOrEmpty(clientSecretFinal) ? "missing_post_secret" : "invalid_post_secret",
                    "no_credentials" => "missing_credentials",
                    _ => "secret_validation_failed"
                };
            }
        }

        if (!authenticated)
        {
            metrics.ParFailures.Add(1);
            logger.LogWarning("/par 400 unauthorized_client corr={Corr} client_hash={ClientHash} mode={Mode} reason={Reason}", corr, BucketizeClientId(clientId), authAttemptMode, authFailureDetail ?? "auth_failed");
            return ErrorResults.UnauthorizedClient(correlationId: corr);
        }

        // Optional: object size limit
        var maxBytes = authOptions.Value.RequestObjectMaxBytes;
        if (maxBytes > 0 && !string.IsNullOrEmpty(roJwtRaw) && Encoding.UTF8.GetByteCount(roJwtRaw) > maxBytes)
        {
            metrics.ParFailures.Add(1);
            return ErrorResults.InvalidRequestObject("request object too large", correlationId: corr);
        }

        // Build/validate request
        AuthorizeRequest req;
        if (!string.IsNullOrEmpty(roJwtRaw))
        {
            var issuer = http.GetIssuer(options);
            var aud = issuer.TrimEnd('/') + "/authorize";
            var validation = await requestObjects.ValidateAsync(roJwtRaw, aud).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                metrics.ParFailures.Add(1);
                logger.LogWarning("/par 400: invalid request object corr={Corr} client={Client}", corr, BucketizeClientId(clientId));
                return Results.Json(new { error = validation.Error, error_description = validation.ErrorDescription, correlation_id = corr }, statusCode: 400);
            }
            if (!string.Equals(validation.ClientId, clientId, StringComparison.Ordinal))
            {
                metrics.ParFailures.Add(1);
                logger.LogWarning("/par 400: client_id mismatch corr={Corr} client={Client}", corr, BucketizeClientId(clientId));
                return ErrorResults.InvalidRequest("client_id mismatch between auth and request object", correlationId: corr);
            }
            req = validation.Request!;
            var stateOverride = form["state"].ToString();
            if (!string.IsNullOrEmpty(stateOverride)) req.state = stateOverride;
        }
        else
        {
            req = new AuthorizeRequest
            {
                response_type = form[OAuthConstants.Parameters.ResponseType],
                client_id = clientId,
                redirect_uri = form[OAuthConstants.Parameters.RedirectUri],
                scope = form[OAuthConstants.Parameters.Scope],
                state = form[OAuthConstants.Parameters.State],
                nonce = form[OAuthConstants.Parameters.Nonce],
                code_challenge = form[OAuthConstants.Parameters.CodeChallenge],
                code_challenge_method = form[OAuthConstants.Parameters.CodeChallengeMethod],
                resource = form[OAuthConstants.Parameters.Resource],
            };
        }

        var result = await authorize.ValidateAsync(req).ConfigureAwait(false);
        if (!result.IsValid)
        {
            metrics.ParFailures.Add(1);
            logger.LogWarning("/par 400: validation failed corr={Corr} client={Client} err={Err} desc={Desc}", corr, BucketizeClientId(clientId), result.Error, result.ErrorDescription);
            return Results.Json(new { error = result.Error, error_description = result.ErrorDescription, correlation_id = corr }, statusCode: 400);
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

        var issuer2 = http.GetIssuer(options);
        var requestUri = issuer2.TrimEnd('/') + "/par/" + id;

        try
        {
            var expiresAt = parStore.Create(id, req, clientId!, TimeSpan.FromMinutes(5), requestUri);
            var expiresIn = (int)Math.Max(0, (expiresAt - DateTimeOffset.UtcNow).TotalSeconds);
            metrics.ParSuccess.Add(1);
            logger.LogInformation("/par 201: success corr={Corr} client={Client} uri={Uri}", corr, BucketizeClientId(clientId!), requestUri);
            // RFC 9126 Section 2.2: PAR success response MUST be HTTP 201 Created with Cache-Control: no-store
            http.Response.StatusCode = 201;
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.ContentType = "application/json";
            return Results.Json(new { request_uri = requestUri, expires_in = expiresIn });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("pending limit", StringComparison.OrdinalIgnoreCase))
        {
            metrics.ParFailures.Add(1);
            logger.LogWarning(ex, "/par 429: pending limit corr={Corr} client={Client}", corr, BucketizeClientId(clientId!));
            return Results.Json(new { error = "rate_limit_exceeded", error_description = "Too many pending requests", correlation_id = corr }, statusCode: 429);
        }
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

    static string BucketizeClientId(string clientId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clientId));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}
