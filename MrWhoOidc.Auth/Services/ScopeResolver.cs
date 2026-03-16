using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Default implementation of IScopeResolver supporting global and tenant-scoped scopes.
/// </summary>
internal sealed class ScopeResolver(AuthDbContext db) : IScopeResolver
{
    // Standard OAuth2/OIDC scopes that are always global
    private static readonly HashSet<string> StandardScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openid",
        "profile",
        "email",
        "address",
        "phone",
        "offline_access",
        "roles"
    };
    
    public async Task<IReadOnlyList<Scope>> GetAvailableScopesAsync(Guid? tenantId, CancellationToken ct = default)
    {
        var query = db.Scopes.AsNoTracking();
        
        if (tenantId.HasValue)
        {
            // Return global scopes + tenant-specific scopes
            query = query.Where(s => s.IsGlobal || s.TenantId == tenantId.Value);
        }
        else
        {
            // Return only global scopes
            query = query.Where(s => s.IsGlobal);
        }
        
        return await query.OrderBy(s => s.Name).ToListAsync(ct);
    }
    
    public async Task<ScopeValidationResult> ValidateScopesAsync(
        IEnumerable<string> requestedScopes, 
        Guid? tenantId,
        CancellationToken ct = default)
    {
        var scopeNames = requestedScopes.ToList();
        if (scopeNames.Count == 0)
        {
            return new ScopeValidationResult();
        }
        
        var availableScopes = await GetAvailableScopesAsync(tenantId, ct);
        var availableScopeNames = availableScopes.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        var validScopes = new List<string>(scopeNames.Count);
        var invalidScopes = new List<string>();

        foreach (var scopeName in scopeNames)
        {
            if (availableScopeNames.Contains(scopeName))
            {
                validScopes.Add(scopeName);
            }
            else
            {
                invalidScopes.Add(scopeName);
            }
        }
        
        return new ScopeValidationResult
        {
            ValidScopes = validScopes,
            InvalidScopes = invalidScopes
        };
    }
    
    public async Task<bool> IsScopeNameAvailableAsync(string scopeName, Guid? tenantId, CancellationToken ct = default)
    {
        if (tenantId.HasValue)
        {
            // Check if scope exists globally OR in this tenant
            return !await db.Scopes.AnyAsync(s => 
                s.Name == scopeName && 
                (s.IsGlobal || s.TenantId == tenantId.Value), ct);
        }
        
        // Global scope - check only global namespace
        return !await db.Scopes.AnyAsync(s => s.Name == scopeName && s.IsGlobal, ct);
    }
    
    public bool IsStandardScope(string scopeName)
    {
        return StandardScopes.Contains(scopeName);
    }
}
