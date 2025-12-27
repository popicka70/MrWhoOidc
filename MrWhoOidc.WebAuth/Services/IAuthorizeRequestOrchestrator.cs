using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.Services.Authorization;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Context for the authorization request processing.
/// </summary>
/// <param name="Request">The resolved and validated OIDC authorize request.</param>
/// <param name="CorrelationId">The correlation ID for the request.</param>
/// <param name="ClientBucket">The client bucket for metrics.</param>
/// <param name="Mode">The request mode (e.g., "PAR", "JAR", "Standard").</param>
/// <param name="RequestUriRaw">The raw request URI if applicable.</param>
public record AuthorizationContext(
    AuthorizeRequest Request,
    string CorrelationId,
    string ClientBucket,
    string Mode,
    string? RequestUriRaw
);

/// <summary>
/// Orchestrates the initial resolution and validation of an OIDC authorization request.
/// </summary>
public interface IAuthorizeRequestOrchestrator
{
    /// <summary>
    /// Resolves the request from the HTTP context (handling PAR, JAR, or standard query params) and validates it.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple containing either an error result or the successfully resolved authorization context.</returns>
    Task<(IResult? error, AuthorizationContext? context)> ResolveAndValidateAsync(HttpContext http, CancellationToken ct = default);
}
