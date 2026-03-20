using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Services;

public interface ICliClientService
{
    string BuildClientId(string tenantSlug);
    Task<Client> EnableCliAccessAsync(Guid tenantId, string tenantSlug, CancellationToken ct = default);
    Task DisableCliAccessAsync(Guid tenantId, string tenantSlug, CancellationToken ct = default);
    Task<string?> GetCliClientIdAsync(Guid tenantId, CancellationToken ct = default);
}

internal sealed class CliClientService(AuthDbContext db, IClientStore clientStore) : ICliClientService
{
    private static readonly string[] CliScopes =
    {
        OidcConstants.Scopes.OpenId,
        OidcConstants.Scopes.Profile,
        OidcConstants.Scopes.Email,
        OidcConstants.Scopes.Roles,
        OidcConstants.Scopes.Tenants,
        OidcConstants.Scopes.OfflineAccess
    };

    private static readonly string[] CliGrantTypes =
    {
        OAuthConstants.GrantTypes.DeviceCode,
        OAuthConstants.GrantTypes.RefreshToken
    };

    public string BuildClientId(string tenantSlug)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug))
        {
            throw new ArgumentException("Tenant slug is required.", nameof(tenantSlug));
        }

        return $"mrwho-cli-{tenantSlug.Trim().ToLowerInvariant()}";
    }

    public async Task<Client> EnableCliAccessAsync(Guid tenantId, string tenantSlug, CancellationToken ct = default)
    {
        var clientId = BuildClientId(tenantSlug);

        var defaultRealmId = await db.Realms
            .Where(r => r.TenantId == tenantId && r.Name == "default")
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (!defaultRealmId.HasValue)
        {
            throw new InvalidOperationException("Default realm was not found for the tenant.");
        }

        var client = await db.Clients
            .Include(c => c.ClientSecrets)
            .FirstOrDefaultAsync(c => c.ClientId == clientId && c.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (client is null)
        {
            client = new Client
            {
                ClientId = clientId,
                ClientName = "MrWho CLI",
                IsSystemClient = true,
                TenantId = tenantId,
                RealmId = defaultRealmId.Value,
                RequirePkce = false,
                RequireConsent = false,
                AllowDeviceAuthorization = true,
                AllowClientCredentials = false,
                AllowCiba = false,
                GrantTypesJson = JsonSerializer.Serialize(CliGrantTypes),
                ResponseTypesJson = JsonSerializer.Serialize(new[] { OAuthConstants.ResponseTypes.Code }),
                Scope = string.Join(' ', CliScopes),
                ApplicationType = "native",
                TokenEndpointAuthMethod = "none",
                AllowClientSecretBasic = false,
                AllowClientSecretPost = false,
                AllowPrivateKeyJwt = false,
                AllowLocalLogin = true,
                AllowExternalIdp = true,
                AllowQrLogin = false
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            client.ClientName = "MrWho CLI";
            client.IsSystemClient = true;
            client.RealmId = defaultRealmId.Value;
            client.RequirePkce = false;
            client.RequireConsent = false;
            client.AllowDeviceAuthorization = true;
            client.AllowClientCredentials = false;
            client.AllowCiba = false;
            client.GrantTypesJson = JsonSerializer.Serialize(CliGrantTypes);
            client.ResponseTypesJson = JsonSerializer.Serialize(new[] { OAuthConstants.ResponseTypes.Code });
            client.Scope = string.Join(' ', CliScopes);
            client.ApplicationType = "native";
            client.TokenEndpointAuthMethod = "none";
            client.AllowClientSecretBasic = false;
            client.AllowClientSecretPost = false;
            client.AllowPrivateKeyJwt = false;
            client.AllowLocalLogin = true;
            client.AllowExternalIdp = true;
            client.AllowQrLogin = false;
        }

#pragma warning disable CS0618
        client.ClientSecretHash = null;
#pragma warning restore CS0618

        var existingScopes = await db.ClientScopes
            .Where(cs => cs.ClientId == client.Id)
            .Select(cs => cs.ScopeName)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var scope in CliScopes.Except(existingScopes, StringComparer.Ordinal))
        {
            db.ClientScopes.Add(new ClientScope
            {
                ClientId = client.Id,
                ScopeName = scope
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await clientStore.InvalidateClientCacheAsync(client.ClientId, client.TenantId, ct).ConfigureAwait(false);
        return client;
    }

    public async Task DisableCliAccessAsync(Guid tenantId, string tenantSlug, CancellationToken ct = default)
    {
        var clientId = BuildClientId(tenantSlug);
        var client = await db.Clients
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ClientId == clientId, ct)
            .ConfigureAwait(false);

        if (client is null)
        {
            return;
        }

        client.AllowDeviceAuthorization = false;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await clientStore.InvalidateClientCacheAsync(client.ClientId, client.TenantId, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetCliClientIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await db.Clients
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsSystemClient && c.AllowDeviceAuthorization)
            .OrderBy(c => c.ClientId)
            .Select(c => c.ClientId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}