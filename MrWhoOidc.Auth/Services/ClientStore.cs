using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IClientStore
{
    Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default);
}

internal sealed class ClientStore(AuthDbContext db) : IClientStore
{
    public Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
        => db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
}
