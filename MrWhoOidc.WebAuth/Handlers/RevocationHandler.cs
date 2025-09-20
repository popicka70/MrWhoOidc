using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IRevocationHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class RevocationHandler(IRevocationService revocations) : IRevocationHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        if (!http.Request.HasFormContentType)
            return Results.BadRequest(new { error = "invalid_request" });

        var form = await http.Request.ReadFormAsync();
        var token = form["token"].ToString();
        var hint = form["token_type_hint"].ToString();
        var clientId = form["client_id"].ToString();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId))
            return Results.BadRequest(new { error = "invalid_request" });

        await revocations.RevokeAsync(token, hint, clientId);
        return Results.Ok();
    }
}
