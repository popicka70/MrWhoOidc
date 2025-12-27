using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Authorization;

/// <summary>
/// Validates an OIDC authorization request against protocol rules and client configuration.
/// </summary>
public interface IAuthorizeRequestValidator
{
    /// <summary>
    /// Validates the provided authorization request.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A validation result indicating success or failure with protocol-compliant error details.</returns>
    Task<AuthorizeValidationResult> ValidateAsync(AuthorizeRequest request, CancellationToken ct = default);
}
