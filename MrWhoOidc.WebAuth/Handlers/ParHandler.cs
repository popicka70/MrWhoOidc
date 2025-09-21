using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using System.Net.Http.Headers;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MrWhoOidc.WebAuth.Observability;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IParHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class ParHandler(OidcOptions options, IClientStore clients, IClientAssertionValidator assertions, IAuthorizeService authorize, IPushedAuthorizationRequestStore parStore, IRequestObjectValidator requestObjects, IOptions<AuthOptions> authOptions, OidcMetrics metrics, ILogger<ParHandler> logger) : IParHandler
{
    // In-memory per-client sliding window limiter (small step). For distributed deployments, replace with Redis limiter.
    private static readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> _clientWindows = new();
    private const int ClientRateLimitPerMinute = 60; // TODO: make configurable if needed

    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var corr = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        metrics.ParRequests.Add(1);
        if (!http.Request.HasFormContentType)
        {
            metrics.ParFailures.Add(1);
            return Results.Json(new { error = "invalid_request", error_description = "Form content expected", correlation_id = corr }, statusCode: 400);
        }

        var form = await http.Request.ReadFormAsync();

        // Client id for partitioning (from header or form)
        var (clientIdHeader, _) = ReadClientCredentials(http);
        var clientIdForRate = !string.IsNullOrEmpty(clientIdHeader) ? clientIdHeader : form["client_id"].ToString();
        var clientBucket = !string.IsNullOrEmpty(clientIdForRate) ? BucketizeClientId(clientIdForRate) : "unknown";

        // Per-client sliding window limiter
        if (!string.IsNullOrEmpty(clientIdForRate))
        {
            var now = DateTimeOffset.UtcNow;
            _clientWindows.AddOrUpdate(clientBucket, _ => (1, now), (_, cur) =>
            {
                if (now - cur.WindowStart >= TimeSpan.FromMinutes(1))
                {
                    return (1, now);
                }
                if (cur.Count + 1 > ClientRateLimitPerMinute)
                {
                    return (cur.Count + 1, cur.WindowStart);
                }
                return (cur.Count + 1, cur.WindowStart);
            });
            var snapshot = _clientWindows[clientBucket];
            if (snapshot.Count > ClientRateLimitPerMinute && now - snapshot.WindowStart < TimeSpan.FromMinutes(1))
            {
                metrics.ParFailures.Add(1);
                logger.LogWarning("/par 429: per-client window exceeded corr={Corr} client={Client}", corr, clientBucket);
                return Results.Json(new { error = "rate_limit_exceeded", error_description = "Too many requests", correlation_id = corr }, statusCode: 429);
            }
        }

        // Size metric for request object if present
        var roJwtRaw = form["request"].ToString();
        if (!string.IsNullOrEmpty(roJwtRaw))
        {
            metrics.ParRequestSizeBytes.Record(System.Text.Encoding.UTF8.GetByteCount(roJwtRaw));
        }

        // Client authentication: private_key_jwt, basic, or post
        var (clientId, clientSecret) = ReadClientCredentials(http);
        if (string.IsNullOrEmpty(clientId)) clientId = form["client_id"].ToString();
        if (string.IsNullOrWhiteSpace(clientId)) { metrics.ParFailures.Add(1); return Results.Json(new { error = "invalid_request", error_description = "Missing client_id", correlation_id = corr }, statusCode: 400); }

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

        if (!authenticated) { metrics.ParFailures.Add(1); return Results.Json(new { error = "unauthorized_client", correlation_id = corr }, statusCode: 400); }

        // Optional: object size limit
        var maxBytes = authOptions.Value.RequestObjectMaxBytes;
        if (maxBytes > 0 && !string.IsNullOrEmpty(roJwtRaw) && Encoding.UTF8.GetByteCount(roJwtRaw) > maxBytes)
        {
            metrics.ParFailures.Add(1);
            return Results.Json(new { error = "invalid_request_object", error_description = "request object too large", correlation_id = corr }, statusCode: 400);
        }

        // Build/validate request
        AuthorizeRequest req;
        if (!string.IsNullOrEmpty(roJwtRaw))
        {
            var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
            var aud = issuer.TrimEnd('/') + "/authorize";
            var validation = await requestObjects.ValidateAsync(roJwtRaw, aud);
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
                return Results.Json(new { error = "invalid_request", error_description = "client_id mismatch between auth and request object", correlation_id = corr }, statusCode: 400);
            }
            req = validation.Request!;
            var stateOverride = form["state"].ToString();
            if (!string.IsNullOrEmpty(stateOverride)) req.state = stateOverride;
        }
        else
        {
            req = new AuthorizeRequest
            {
                response_type = form["response_type"],
                client_id = clientId,
                redirect_uri = form["redirect_uri"],
                scope = form["scope"],
                state = form["state"],
                nonce = form["nonce"],
                code_challenge = form["code_challenge"],
                code_challenge_method = form["code_challenge_method"],
                resource = form["resource"],
            };
        }

        var result = await authorize.ValidateAsync(req);
        if (!result.IsValid)
        {
            metrics.ParFailures.Add(1);
            logger.LogWarning("/par 400: validation failed corr={Corr} client={Client}", corr, BucketizeClientId(clientId));
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

        var issuer2 = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
        var requestUri = issuer2.TrimEnd('/') + "/par/" + id;

        try
        {
            var expiresAt = parStore.Create(id, req, clientId!, TimeSpan.FromMinutes(5), requestUri);
            var expiresIn = (int)Math.Max(0, (expiresAt - DateTimeOffset.UtcNow).TotalSeconds);
            metrics.ParSuccess.Add(1);
            return Results.Json(new { request_uri = requestUri, expires_in = expiresIn, correlation_id = corr });
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
