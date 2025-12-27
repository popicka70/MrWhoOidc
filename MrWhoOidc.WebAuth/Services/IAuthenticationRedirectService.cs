using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.Services.Authorization;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Handles redirects to the login page or external identity providers.
/// </summary>
public interface IAuthenticationRedirectService
{
    /// <summary>
    /// Redirects the user to the appropriate login page or external IDP based on the provider selection result.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="selection">The selected provider details.</param>
    /// <param name="validation">The validated authorization request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An IResult representing the redirect.</returns>
    Task<IResult> RedirectToLoginAsync(HttpContext http, ProviderSelectionResult selection, AuthorizeValidationResult validation, CancellationToken ct = default);
}
