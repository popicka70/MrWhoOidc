using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IClientStore
{
    Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default);
    Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default);
    IQueryable<Client> QueryClients(CancellationToken ct = default);
}

internal sealed class ClientStore(AuthDbContext db, IPasswordHasher hasher) : IClientStore
{
    public Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
        => db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct);

    public async Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default)
    {
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct).ConfigureAwait(false);
        if (client is null) return false;
        if (string.IsNullOrEmpty(client.ClientSecretHash))
        {
            // Public client: secret not required; allow if no secret provided
            return string.IsNullOrEmpty(clientSecret);
        }
        if (string.IsNullOrEmpty(clientSecret)) return false;
        return hasher.Verify(clientSecret, client.ClientSecretHash);
    }

    public IQueryable<Client> QueryClients(CancellationToken ct = default)
        => db.Clients.AsQueryable();
}
