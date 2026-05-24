using System.Threading;
using System.Threading.Tasks;
using System;

namespace MrWhoOidc.Auth.Services.Authorization;

/// <summary>
/// Determines which identity provider (IDP) should be used for the current authorization request.
/// </summary>
public interface IProviderSelectionService
{
    /// <summary>
    /// Evaluates the best IDP to use based on request parameters and client configuration.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="idpParam">The 'idp' parameter from the request, if any.</param>
    /// <param name="idpHint">The 'idp_hint' parameter from the request, if any.</param>
    /// <param name="lastUsedIdp">The IDP last used by the user, if known.</param>
    /// <param name="forceAccountSelection">Whether to force the user to select an account.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="tenantId">The tenant scope for provider selection, if already resolved.</param>
    /// <returns>A result indicating the selected IDP or if a selection UI is required.</returns>
    Task<ProviderSelectionResult> EvaluateAsync(
        string clientId,
        string? idpParam = null,
        string? idpHint = null,
        string? lastUsedIdp = null,
        bool forceAccountSelection = false,
        CancellationToken ct = default,
        Guid? tenantId = null);
}
