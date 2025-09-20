using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Observability;
using System.Security.Cryptography;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using System.Text.Json;

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
    ILogger<IntrospectionHandler> logger,
    AuthDbContext db,
    IOptions<AuthOptions> authOptions
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
        var hint = form["token_type_hint"].ToString();
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

        // Introspection policy: check allowed audiences for this client, if configured
        string? requestedAud = null;

        // Try JWT first
        var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
        var (ok, principal, _) = tokenValidator.Validate(token, issuer);
        if (ok && principal is not null)
        {
            requestedAud = principal.FindFirst("aud")?.Value;

            if (!IsClientAllowedForAudience(client, requestedAud))
            {
                metrics.IntrospectionActiveFalse.Add(1, tags);
                metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
                LogAudit(logger, clientId, http.Connection.RemoteIpAddress?.ToString(), outcome: "forbidden", aud: requestedAud);
                return Results.Json(new { active = false });
            }

            var scope = principal.FindFirst("scope")?.Value;
            var sub = principal.FindFirst("sub")?.Value;
            var iss = principal.FindFirst("iss")?.Value ?? issuer;
            var iatStr = principal.FindFirst("iat")?.Value;
            var nbfStr = principal.FindFirst("nbf")?.Value;
            var expStr = principal.FindFirst("exp")?.Value;
            var jti = principal.FindFirst("jti")?.Value;

            // cnf (DPoP/bound tokens) when present
            object? cnf = null;
            var cnfRaw = principal.FindFirst("cnf")?.Value;
            if (!string.IsNullOrEmpty(cnfRaw))
            {
                try
                {
                    cnf = JsonDocument.Parse(cnfRaw).RootElement;
                }
                catch
                {
                    // ignore if invalid JSON
                }
            }

            long? ToLong(string? s) => long.TryParse(s, out var v) ? v : null;

            var response = new Dictionary<string, object?>
            {
                ["active"] = true,
                ["token_type"] = "Bearer",
                ["scope"] = scope,
                ["sub"] = sub,
                ["username"] = sub,
                ["aud"] = requestedAud,
                ["iss"] = iss,
                ["iat"] = ToLong(iatStr),
                ["nbf"] = ToLong(nbfStr),
                ["exp"] = ToLong(expStr),
                ["jti"] = jti
            };

            if (cnf is not null)
            {
                response["cnf"] = cnf;
            }

            metrics.IntrospectionActiveTrue.Add(1, tags);
            LogAudit(logger, clientId, http.Connection.RemoteIpAddress?.ToString(), outcome: "active", aud: requestedAud);
            metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
            return Results.Json(response);
        }

        // Opaque token path: look up by hash in DB
        var hash = Hash(token);
        var entity = await db.Tokens.AsNoTracking().FirstOrDefaultAsync(t => t.Type == "access" && t.TokenHash == hash, http.RequestAborted);
        if (entity is null)
        {
            metrics.IntrospectionActiveFalse.Add(1, tags);
            LogAudit(logger, clientId, http.Connection.RemoteIpAddress?.ToString(), outcome: "inactive", aud: null);
            metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
            return Results.Json(new { active = false });
        }

        requestedAud = entity.Audience;
        if (!IsClientAllowedForAudience(client, requestedAud))
        {
            metrics.IntrospectionActiveFalse.Add(1, tags);
            metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
            LogAudit(logger, clientId, http.Connection.RemoteIpAddress?.ToString(), outcome: "forbidden", aud: requestedAud);
            return Results.Json(new { active = false });
        }

        var active = entity.RevokedAt is null && entity.ExpiresAt > DateTimeOffset.UtcNow;
        if (!active)
        {
            metrics.IntrospectionActiveFalse.Add(1, tags);
            LogAudit(logger, clientId, http.Connection.RemoteIpAddress?.ToString(), outcome: "inactive", aud: requestedAud);
            metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
            return Results.Json(new { active = false });
        }

        var scopes = System.Text.Json.JsonSerializer.Deserialize<string[]>(entity.ScopesJson) ?? Array.Empty<string>();
        var responseOpaque = new Dictionary<string, object?>
        {
            ["active"] = true,
            ["token_type"] = "Bearer",
            ["scope"] = string.Join(' ', scopes),
            ["sub"] = entity.UserId.ToString(),
            ["username"] = entity.UserId.ToString(),
            ["aud"] = entity.Audience,
            ["iss"] = issuer,
            ["exp"] = entity.ExpiresAt.ToUnixTimeSeconds(),
            ["jti"] = entity.Jti
        };

        metrics.IntrospectionActiveTrue.Add(1, tags);
        LogAudit(logger, clientId, http.Connection.RemoteIpAddress?.ToString(), outcome: "active", aud: entity.Audience);
        metrics.IntrospectionDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
        return Results.Json(responseOpaque);
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

    bool IsClientAllowedForAudience(MrWhoOidc.Auth.Persistence.Client client, string? audience)
    {
        if (string.IsNullOrEmpty(audience)) return true; // if not present, skip policy

        // 1) Per-client allow-list if set
        if (!string.IsNullOrEmpty(client.IntrospectionAudiencesJson))
        {
            try
            {
                var list = JsonSerializer.Deserialize<string[]>(client.IntrospectionAudiencesJson) ?? Array.Empty<string>();
                return list.Contains(audience, StringComparer.Ordinal);
            }
            catch { /* fall through to global */ }
        }

        // 2) Global config allow-list map
        var map = authOptions.Value.IntrospectionPermissions;
        if (map is null || map.Count == 0) return true; // no policy configured
        if (!map.TryGetValue(client.ClientId, out var audiences)) return false;
        return audiences.Contains(audience, StringComparer.Ordinal);
    }

    static string Hash(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    static string BucketizeClientId(string clientId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clientId));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    static void LogAudit(ILogger logger, string clientId, string? ip, string outcome, string? aud)
    {
        var bucket = BucketizeClientId(clientId);
        logger.LogInformation("introspection audit: client={ClientBucket} ip={IP} outcome={Outcome} aud={Audience}", bucket, ip ?? "unknown", outcome, aud ?? "none");
    }
}
