using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IRevocationHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class RevocationHandler(IRevocationService revocations, IClientStore clients, OidcMetrics metrics) : IRevocationHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        metrics.RevocationRequests.Add(1);

        if (!http.Request.HasFormContentType)
            return Results.BadRequest(new { error = "invalid_request" });

        var (clientIdHeader, clientSecretHeader) = ReadClientCredentials(http);

        var form = await http.Request.ReadFormAsync();
        var token = form["token"].ToString();
        var hint = form["token_type_hint"].ToString();
        var clientId = !string.IsNullOrEmpty(clientIdHeader) ? clientIdHeader : form["client_id"].ToString();
        var clientSecret = !string.IsNullOrEmpty(clientSecretHeader) ? clientSecretHeader : form["client_secret"].ToString();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId))
            return Results.BadRequest(new { error = "invalid_request" });

        var validClient = await clients.ValidateClientSecretAsync(clientId, clientSecret);
        if (!validClient)
            return Results.BadRequest(new { error = "unauthorized_client" });

        var ip = http.Connection.RemoteIpAddress?.ToString();
        await revocations.RevokeAsync(token, hint, clientId, ip);
        return Results.Ok();
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
