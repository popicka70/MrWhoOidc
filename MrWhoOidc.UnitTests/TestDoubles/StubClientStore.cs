using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.TestDoubles;

public sealed class StubClientStore : IClientStore
{
    private readonly MrWhoOidc.Auth.Persistence.Client? _client;

    public StubClientStore(MrWhoOidc.Auth.Persistence.Client? client = null)
    {
        _client = client;
    }

    public Task<MrWhoOidc.Auth.Persistence.Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
    {
        if (_client is not null && string.Equals(_client.ClientId, clientId, StringComparison.Ordinal))
        {
            return Task.FromResult<MrWhoOidc.Auth.Persistence.Client?>(_client);
        }

        return Task.FromResult<MrWhoOidc.Auth.Persistence.Client?>(null);
    }

    public Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default)
        => Task.FromResult(false);

    public IQueryable<MrWhoOidc.Auth.Persistence.Client> QueryClients(CancellationToken ct = default)
        => Array.Empty<MrWhoOidc.Auth.Persistence.Client>().AsQueryable();

    public Task InvalidateClientCacheAsync(string clientId, Guid tenantId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<MrWhoOidc.Auth.Persistence.ClientSecret?> GetPrimarySecretAsync(Guid clientRecordId, CancellationToken ct = default)
        => Task.FromResult<MrWhoOidc.Auth.Persistence.ClientSecret?>(null);

    public Task<List<MrWhoOidc.Auth.Persistence.ClientSecret>> GetActiveSecretsAsync(Guid clientRecordId, CancellationToken ct = default)
        => Task.FromResult(new List<MrWhoOidc.Auth.Persistence.ClientSecret>());

    public Task<MrWhoOidc.Auth.Persistence.ClientSecret> CreateSecretAsync(
        Guid clientRecordId,
        string secretValue,
        string? description,
        string? createdBy,
        DateTime? expiresAtUtc = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return Task.FromResult(new MrWhoOidc.Auth.Persistence.ClientSecret
        {
            ClientId = clientRecordId,
            SecretHash = "stub",
            Description = description,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAtUtc,
            CreatedBy = createdBy,
            ActivatedAtUtc = null,
            RevokedAtUtc = null,
            IsPrimary = false,
        });
    }

    public Task<bool> ActivateSecretAsync(Guid secretId, string activatedBy, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> SetPrimarySecretAsync(Guid secretId, string updatedBy, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> RevokeSecretAsync(Guid secretId, string revokedBy, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> RecordSecretUsageAsync(Guid secretId, CancellationToken ct = default)
        => Task.FromResult(false);
}
