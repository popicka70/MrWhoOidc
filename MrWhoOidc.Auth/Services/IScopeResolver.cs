using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for resolving scopes in a multi-tenant context, supporting both global and tenant-scoped scopes.
/// </summary>
public interface IScopeResolver
{
    /// <summary>
    /// Get all scopes visible to the current context (global + tenant-specific).
    /// </summary>
    /// <param name="tenantId">Tenant ID. If null, returns only global scopes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of available scopes.</returns>
    Task<IReadOnlyList<Scope>> GetAvailableScopesAsync(Guid? tenantId, CancellationToken ct = default);
    
    /// <summary>
    /// Validate that requested scopes exist and are accessible to the given tenant.
    /// </summary>
    /// <param name="requestedScopes">The scopes being requested.</param>
    /// <param name="tenantId">Tenant ID. If null, only global scopes are valid.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result with valid and invalid scopes.</returns>
    Task<ScopeValidationResult> ValidateScopesAsync(
        IEnumerable<string> requestedScopes, 
        Guid? tenantId,
        CancellationToken ct = default);
    
    /// <summary>
    /// Check if a scope name is available for creation.
    /// </summary>
    /// <param name="scopeName">The scope name to check.</param>
    /// <param name="tenantId">Tenant ID. If null, checks global scope namespace.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the name is available, false if already taken.</returns>
    Task<bool> IsScopeNameAvailableAsync(string scopeName, Guid? tenantId, CancellationToken ct = default);
    
    /// <summary>
    /// Check if a scope is a standard OAuth2/OIDC scope.
    /// </summary>
    /// <param name="scopeName">The scope name to check.</param>
    /// <returns>True if the scope is a standard scope.</returns>
    bool IsStandardScope(string scopeName);
}

/// <summary>
/// Result of scope validation.
/// </summary>
public sealed class ScopeValidationResult
{
    public IReadOnlyList<string> ValidScopes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InvalidScopes { get; init; } = Array.Empty<string>();
    public bool IsValid => InvalidScopes.Count == 0;
}
