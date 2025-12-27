using Microsoft.AspNetCore.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.Services;

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
}
