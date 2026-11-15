using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Login context extracted from the current principal: captured at /authorize for the authorization-code
/// flow (persisted on the code row) and passed directly to the implicit-flow token issuer.
/// </summary>
public sealed record AuthorizeLoginContext(
    DateTimeOffset AuthTime,
    string? UpstreamIdp,
    string? UpstreamAcr,
    string[] UpstreamAmr,
    string? SessionId,
    IReadOnlyDictionary<string, string> MappedClaims
);

/// <summary>
/// Manages metadata associated with an authorization code, such as session information and client-specific data.
/// </summary>
public interface IAuthorizationMetadataService
{
    /// <summary>
    /// Populates metadata for the given authorization code, including session ID and user agent info.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="code">The authorization code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task PopulateMetadataAsync(HttpContext http, string code, CancellationToken ct = default);

    /// <summary>
    /// Extracts the login context (auth_time, upstream idp/acr/amr, session id, mapped claims) from the
    /// current principal. Shared by the authorization-code flow (which persists it on the code row) and the
    /// implicit flow (which consumes it immediately).
    /// </summary>
    AuthorizeLoginContext GetLoginContext(HttpContext http);

    /// <summary>
    /// F1: records that the given client took part in the current OP session (front-channel/logout
    /// session propagation), when a session is present.
    /// </summary>
    Task RegisterSessionClientAsync(HttpContext http, string clientId, CancellationToken ct = default);
}
