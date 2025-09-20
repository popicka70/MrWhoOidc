using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IIntrospectionHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class IntrospectionHandler(
    OidcOptions options,
    ITokenValidator tokenValidator,
    IClientStore clients,
    IClientAssertionValidator assertions
) : IIntrospectionHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        if (!http.Request.HasFormContentType)
            return Results.BadRequest(new { error = "invalid_request" });

        var (clientIdHeader, clientSecretHeader) = ReadClientCredentials(http);

        var form = await http.Request.ReadFormAsync();
        var token = form["token"].ToString();
        var hint = form["token_type_hint"].ToString(); // currently unused, supports access token only
        var clientId = !string.IsNullOrEmpty(clientIdHeader) ? clientIdHeader : form["client_id"].ToString();
        var clientSecret = !string.IsNullOrEmpty(clientSecretHeader) ? clientSecretHeader : form["client_secret"].ToString();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId))
            return Results.BadRequest(new { error = "invalid_request" });

        // Require confidential client for introspection unless using private_key_jwt
        var client = await clients.FindByClientIdAsync(clientId);
        if (client is null)
            return Results.BadRequest(new { error = "unauthorized_client" });

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
                return Results.BadRequest(new { error = "unauthorized_client" });
            }
            authenticated = await clients.ValidateClientSecretAsync(clientId, clientSecret);
        }

        if (!authenticated)
            return Results.BadRequest(new { error = "unauthorized_client" });

        // Validate access token (JWT) using local signing keys
        var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";
        var (ok, principal, _) = tokenValidator.Validate(token, issuer);

        if (!ok || principal is null)
        {
            // Per RFC 7662, return 200 with { active:false } on invalid/non-existent token
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
}
