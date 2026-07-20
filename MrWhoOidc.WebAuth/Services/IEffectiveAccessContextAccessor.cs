using MrWhoOidc.Auth.Models.Delegation;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Resolves an immutable EffectiveAccessContext for the current request.
/// Implements AD-1: Keep actor and subject distinct.
/// Evaluates Tenant Support Access, Delegated Access Grant, and normal fallback in priority order.
/// Exactly one elevated context may be active at a time (AD-1 constraint).
/// </summary>
public interface IEffectiveAccessContextAccessor
{
    /// <summary>
    /// Resolve the current effective access context.
    /// </summary>
    /// <param name="ct">Cancellation token forwarded to all async dependencies.</param>
    /// <returns>The resolved EffectiveAccessContext for the current request.</returns>
    Task<EffectiveAccessContext> GetContextAsync(CancellationToken ct = default);
}
