using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IClientAssertionValidator
{
    Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default);
}

public sealed class ClientAssertionValidator(AuthDbContext db) : IClientAssertionValidator
{
    public async Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default)
    {
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client == null) return false;

        // For now, not persisted: accept only secret-based clients as private_key_jwt disabled without JWK configured
        if (string.IsNullOrEmpty(client.ClientSecretHash))
            return false;

        // TODO: Add configuration-based JWKs per client when needed
        return false;
    }
}
