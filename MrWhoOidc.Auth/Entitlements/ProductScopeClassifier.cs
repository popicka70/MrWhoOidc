namespace MrWhoOidc.Auth.Entitlements;

public static class ProductScopeClassifier
{
    private static readonly HashSet<string> StandardScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openid",
        "profile",
        "email",
        "roles",
        "offline_access",
        "tenants",
    };

    private static readonly HashSet<string> ExplicitProductScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "mrwhopdf",
    };

    public static bool IsProductScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        if (StandardScopes.Contains(scope))
        {
            return false;
        }

        if (ExplicitProductScopes.Contains(scope))
        {
            return true;
        }

        return scope.StartsWith("licensing.", StringComparison.OrdinalIgnoreCase);
    }
}
