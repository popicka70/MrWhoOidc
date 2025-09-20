using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Observability;
using System.Security.Cryptography;
using System.Diagnostics;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IIntrospectionHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class IntrospectionHandler(
    OidcOptions options,
    ITokenValidator tokenValidator,
    IClientStore clients,
    IClientAssertionValidator assertions,
    OidcMetrics metrics,
    ILogger<IntrospectionHandler> logger
) : IIntrospectionHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var sw = Stopwatch.StartNew();

        if (!http.Request.HasFormContentType)
        {
            metrics.IntrospectionActiveFalse.Add(1);
            return Results.BadRequest(new { error = "invalid_request" });
        }

        var (clientIdHeader, clientSecretHeader) = ReadClientCredentials(http);

        var form = await http.Request.ReadFormAsync();
        var token = form["token"].ToString();
        var hint = form["token_type_hint"].ToString(); // currently unused, supports access token only
        var clientId = !string.IsNullOrEmpty(clientIdHeader) ? clientIdHeader : form["client_id"].ToString();
        var clientSecret = !string.IsNullOrEmpty(clientSecretHeader) ? clientSecretHeader : form["client_secret"].ToString();
        var clientBucket = string.IsNullOrEmpty(clientId) ? "unknown" : BucketizeClientId(clientId);
        var tags = new[] { new KeyValuePair<string, object?>("client", clientBucket) };

        metrics.IntrospectionRequests.Add(1, tags);

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId))
        {
            metrics.IntrospectionActiveFalse.Add(1, tags);
            metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
            return Results.BadRequest(new { error = "invalid_request" });
        }

        // Require confidential client for introspection unless using private_key_jwt
        var client = await clients.FindByClientIdAsync(clientId);
        if (client is null)
        {
            metrics.IntrospectionActiveFalse.Add(1, tags);
            metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
            return Results.BadRequest(new { error = "unauthorized_client" });
        }

        // private_key_jwt support
        var clientAssertionType = form["client_assertion_type"].ToString();
        var clientAssertion = form["client_assertion"].ToString();
        var endpoint = (options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}") + "/introspect";

        bool authenticated;
        if (string.Equals(clientAssertionType, "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", StringComparison.Ordinal) && !string.IsNullOrEmpty(clientAssertion))
        {
            authenticated = await assertions.ValidateAsync(clientId, clientAssertion, endpoint);
        }
        else
        {
            // Enforce confidential clients for secret-based auth
            if (string.IsNullOrEmpty(client.ClientSecretHash))
            {
                metrics.IntrospectionActiveFalse.Add(1, tags);
                metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
                return Results.BadRequest(new { error = "unauthorized_client" });
            }
            authenticated = await clients.ValidateClientSecretAsync(clientId, clientSecret);
        }

        if (!authenticated)
        {
            metrics.IntrospectionActiveFalse.Add(1, tags);
            metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
            return Results.BadRequest(new { error = "unauthorized_client" });
        }

        // Validate access token (JWT) using local signing keys
        var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
        var (ok, principal, _) = tokenValidator.Validate(token, issuer);

        if (!ok || principal is null)
        {
            // Per RFC 7662, return 200 with { active:false } on invalid/non-existent token
            metrics.IntrospectionActiveFalse.Add(1, tags);
            LogAudit(logger, clientId, http.Connection.RemoteIpAddress?.ToString(), outcome: "inactive", aud: null);
            metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
            return Results.Json(new { active = false });
        }

        // Build introspection response. Only include common fields we can infer from JWT.
        // Note: we do not persist access tokens, so we can't reflect revocation here.
        var scope = principal.FindFirst("scope")?.Value;
        var sub = principal.FindFirst("sub")?.Value;
        var aud = principal.FindFirst("aud")?.Value;
        var iss = principal.FindFirst("iss")?.Value ?? issuer;
        var iatStr = principal.FindFirst("iat")?.Value;
        var nbfStr = principal.FindFirst("nbf")?.Value;
        var expStr = principal.FindFirst("exp")?.Value;

        long? ToLong(string? s) => long.TryParse(s, out var v) ? v : null;

        var response = new Dictionary<string, object?>
        {
            ["active"] = true,
            ["token_type"] = "Bearer",
            ["scope"] = scope,
            ["sub"] = sub,
            ["username"] = sub,
            ["aud"] = aud,
            ["iss"] = iss,
            ["iat"] = ToLong(iatStr),
            ["nbf"] = ToLong(nbfStr),
            ["exp"] = ToLong(expStr)
        };

        metrics.IntrospectionActiveTrue.Add(1, tags);
        LogAudit(logger, clientId, http.Connection.RemoteIpAddress?.ToString(), outcome: "active", aud: aud);
        metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
        return Results.Json(response);
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

    // Simple privacy-preserving bucketization of client_id for logs/metrics
    static string BucketizeClientId(string clientId)
    {
        // Use SHA-256 and keep first 8 bytes as hex prefix
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clientId));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    static void LogAudit(ILogger logger, string clientId, string? ip, string outcome, string? aud)
    {
        var bucket = BucketizeClientId(clientId);
        logger.LogInformation("introspection audit: client={ClientBucket} ip={IP} outcome={Outcome} aud={Audience}", bucket, ip ?? "unknown", outcome, aud ?? "none");
    }
}
