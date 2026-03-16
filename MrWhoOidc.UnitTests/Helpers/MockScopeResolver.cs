using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.UnitTests.Helpers;

/// <summary>
/// Mock implementation of IScopeResolver for testing.
/// </summary>
internal sealed class MockScopeResolver : IScopeResolver
{
    public Task<IReadOnlyList<Scope>> GetAvailableScopesAsync(Guid? tenantId, CancellationToken ct = default)
    {
        // Return standard scopes + any tenant-specific test scopes
        var scopes = new List<Scope>
        {
            new Scope { Name = "openid", Description = "OpenID scope", IsGlobal = true },
            new Scope { Name = "profile", Description = "Profile scope", IsGlobal = true },
            new Scope { Name = "email", Description = "Email scope", IsGlobal = true },
            new Scope { Name = "offline_access", Description = "Offline access", IsGlobal = true },
            new Scope { Name = "roles", Description = "Roles scope", IsGlobal = true }
        };
        
        if (tenantId.HasValue)
        {
            scopes.Add(new Scope { Name = "custom.read", Description = "Custom read", TenantId = tenantId, IsGlobal = false });
            scopes.Add(new Scope { Name = "custom.write", Description = "Custom write", TenantId = tenantId, IsGlobal = false });
        }
        
        return Task.FromResult<IReadOnlyList<Scope>>(scopes);
    }

    public Task<ScopeValidationResult> ValidateScopesAsync(IEnumerable<string> scopeNames, Guid? tenantId, CancellationToken ct = default)
    {
        var available = GetAvailableScopesAsync(tenantId, ct).Result;
        var availableNames = available.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var validScopes = new List<string>();
        var invalidScopes = new List<string>();

        foreach (var scope in scopeNames)
        {
            if (availableNames.Contains(scope))
            {
                validScopes.Add(scope);
            }
            else
            {
                invalidScopes.Add(scope);
            }
        }
        
        return Task.FromResult(new ScopeValidationResult
        {
            ValidScopes = validScopes,
            InvalidScopes = invalidScopes
        });
    }

    public Task<bool> IsScopeNameAvailableAsync(string scopeName, Guid? tenantId, CancellationToken ct = default)
    {
        // For testing, just check if it's not a standard scope
        var isStandard = IsStandardScope(scopeName);
        return Task.FromResult(!isStandard);
    }

    public bool IsStandardScope(string scopeName)
    {
        return scopeName switch
        {
            "openid" => true,
            "profile" => true,
            "email" => true,
            "address" => true,
            "phone" => true,
            "offline_access" => true,
            "roles" => true,
            _ => false
        };
    }
}
