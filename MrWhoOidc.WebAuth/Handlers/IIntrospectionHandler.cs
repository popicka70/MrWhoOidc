namespace MrWhoOidc.WebAuth.Handlers;

/// <summary>
/// Handles OAuth 2.0 token introspection requests (RFC 7662).
/// </summary>
public interface IIntrospectionHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}
