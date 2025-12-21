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
    };

    public static bool IsProductScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        // Treat any non-standard scope as a product scope.
        // This aligns with the convention that platform/product permissions are expressed as custom scopes.
        return !StandardScopes.Contains(scope);
    }
}
