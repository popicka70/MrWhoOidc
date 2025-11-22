using System;

namespace MrWhoOidc.KeyGen.Domain.Models;

/// <summary>
/// Helper constants for license scope options emitted by the key generator.
/// </summary>
public static class LicenseScopeOptions
{
    public const string Platform = "platform";
    public const string Tenant = "tenant";

    public static bool IsValid(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        return scope.Equals(Platform, StringComparison.OrdinalIgnoreCase)
            || scope.Equals(Tenant, StringComparison.OrdinalIgnoreCase);
    }
}
