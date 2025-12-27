using System;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Authorization;

/// <summary>
/// Evaluates whether a user has already consented to the requested scopes for a specific client.
/// </summary>
public interface IConsentProcessor
{
    /// <summary>
    /// Evaluates the consent state for the given user, client, and scopes.
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="requestedScopes">The list of scopes being requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A decision indicating if consent is granted, required, or if an error occurred.</returns>
    Task<ConsentDecision> EvaluateAsync(Guid userId, string clientId, string[] requestedScopes, CancellationToken ct = default);
}
