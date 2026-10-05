using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services.Authorization;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Shared, secure-by-default rules for creating clients. Every creation path (admin UI, admin API, DCR,
/// configuration import, seed manifests, seeders, CLI) assigns scopes and grant types explicitly through
/// these helpers so that no client is ever created with an implicit "everything allowed" policy.
/// </summary>
public static class ClientProvisioning
{
    /// <summary>
    /// Scopes assigned to a new client when its creator did not choose any (R7). <c>tenants</c> and the
    /// admin API scope are protected and never assigned by default.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultScopes =
    [
        OidcConstants.Scopes.OpenId,
        OidcConstants.Scopes.Profile,
        OidcConstants.Scopes.Email,
        OidcConstants.Scopes.OfflineAccess
    ];

    /// <summary>
    /// Grant types of a client that did not register any. Also the effective policy for legacy rows whose
    /// <c>GrantTypesJson</c> is null.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultGrantTypes =
    [
        OAuthConstants.GrantTypes.AuthorizationCode,
        OAuthConstants.GrantTypes.RefreshToken
    ];

    /// <summary>Every grant type this server implements.</summary>
    public static readonly IReadOnlyList<string> KnownGrantTypes =
    [
        OAuthConstants.GrantTypes.AuthorizationCode,
        OAuthConstants.GrantTypes.RefreshToken,
        OAuthConstants.GrantTypes.ClientCredentials,
        OAuthConstants.GrantTypes.TokenExchange,
        OAuthConstants.GrantTypes.DeviceCode,
        OAuthConstants.GrantTypes.Ciba
    ];

    public static bool IsKnownGrantType(string? grantType) =>
        grantType is not null && KnownGrantTypes.Contains(grantType.Trim(), StringComparer.Ordinal);

    /// <summary>Scopes a client can only obtain when an operator assigned them deliberately.</summary>
    public static bool IsProtectedScope(string scope) =>
        string.Equals(scope, OidcConstants.Scopes.Tenants, StringComparison.Ordinal)
        || string.Equals(scope, AdminApiAccess.Scope, StringComparison.Ordinal);

    /// <summary>
    /// Records <paramref name="grantTypes"/> as the client's registered grant types (deduplicated, defaulting
    /// to <see cref="DefaultGrantTypes"/> when empty) and derives the per-grant <c>Allow*</c> flags from them.
    /// </summary>
    public static void ApplyGrantTypes(Client client, IEnumerable<string>? grantTypes)
    {
        ArgumentNullException.ThrowIfNull(client);
        var normalized = (grantTypes ?? [])
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalized.Count == 0)
        {
            normalized = [.. DefaultGrantTypes];
        }

        client.GrantTypesJson = JsonSerializer.Serialize(normalized);
        ApplyGrantFlags(client, normalized);
    }

    /// <summary>Sets <c>AllowClientCredentials</c>/<c>AllowDeviceAuthorization</c>/<c>AllowCiba</c> from grant types.</summary>
    public static void ApplyGrantFlags(Client client, IReadOnlyCollection<string> grantTypes)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(grantTypes);
        client.AllowClientCredentials = grantTypes.Contains(OAuthConstants.GrantTypes.ClientCredentials, StringComparer.Ordinal);
        client.AllowDeviceAuthorization = grantTypes.Contains(OAuthConstants.GrantTypes.DeviceCode, StringComparer.Ordinal);
        client.AllowCiba = grantTypes.Contains(OAuthConstants.GrantTypes.Ciba, StringComparer.Ordinal);
    }

    /// <summary>
    /// The grant types a client may use: its registered list, or <see cref="DefaultGrantTypes"/> when none was
    /// registered. A corrupt registration yields an empty list (fail closed).
    /// </summary>
    public static IReadOnlyList<string> GetEffectiveGrantTypes(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(client.GrantTypesJson))
        {
            return DefaultGrantTypes;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(client.GrantTypesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Assigns <paramref name="scopeNames"/> (or <see cref="DefaultScopes"/> when null/empty) to the client as
    /// <see cref="ClientScope"/> rows. Only scopes that exist for the client's tenant (global or the tenant's
    /// own) are assigned; the standard OIDC scopes are created as global scopes when the Scopes table lacks
    /// them (ClientScope.ScopeName is a foreign key). Already assigned scopes are skipped. The caller saves.
    /// </summary>
    /// <returns>The scope names now assigned by this call plus those that were already assigned.</returns>
    public static async Task<IReadOnlyList<string>> AssignScopesAsync(
        AuthDbContext db,
        Client client,
        IEnumerable<string>? scopeNames,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(client);

        var requested = (scopeNames ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requested.Count == 0)
        {
            requested = [.. DefaultScopes];
        }

        await EnsureStandardScopesExistAsync(db, requested, ct).ConfigureAwait(false);

        var tenantId = client.TenantId;
        var existingScopeNames = await db.Scopes
            .IgnoreQueryFilters() // scope names are a global key; visibility is checked explicitly below
            .AsNoTracking()
            .Where(s => requested.Contains(s.Name) && (s.TenantId == null || s.TenantId == tenantId))
            .Select(s => s.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var pendingScopeNames = db.ChangeTracker.Entries<Scope>()
            .Where(e => e.State == EntityState.Added && (e.Entity.TenantId == null || e.Entity.TenantId == tenantId))
            .Select(e => e.Entity.Name);
        var available = existingScopeNames.Concat(pendingScopeNames).ToHashSet(StringComparer.Ordinal);

        var alreadyAssigned = await db.ClientScopes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(cs => cs.ClientId == client.Id)
            .Select(cs => cs.ScopeName)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var pendingAssigned = db.ChangeTracker.Entries<ClientScope>()
            .Where(e => e.State == EntityState.Added && e.Entity.ClientId == client.Id)
            .Select(e => e.Entity.ScopeName);
        var assigned = alreadyAssigned.Concat(pendingAssigned).ToHashSet(StringComparer.Ordinal);

        foreach (var scope in requested)
        {
            if (!available.Contains(scope) || assigned.Contains(scope))
            {
                continue;
            }

            db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = scope });
            assigned.Add(scope);
        }

        return requested.Where(assigned.Contains).ToList();
    }

    private static async Task EnsureStandardScopesExistAsync(AuthDbContext db, IReadOnlyCollection<string> requested, CancellationToken ct)
    {
        var standard = requested
            .Where(s => OidcConstants.Scopes.AllStandardScopes.Contains(s, StringComparer.Ordinal))
            .ToList();
        if (standard.Count == 0)
        {
            return;
        }

        var present = await db.Scopes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => standard.Contains(s.Name))
            .Select(s => s.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var pending = db.ChangeTracker.Entries<Scope>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in standard.Except(present, StringComparer.Ordinal))
        {
            if (pending.Contains(name))
            {
                continue;
            }

            db.Scopes.Add(new Scope
            {
                Name = name,
                Description = $"Standard scope {name}",
                IsExposed = true,
                IsGlobal = true,
                TenantId = null
            });
        }
    }
}
