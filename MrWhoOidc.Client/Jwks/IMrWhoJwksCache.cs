using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.Client.Jwks;

public interface IMrWhoJwksCache
{
    ValueTask<JsonWebKeySet> GetAsync(CancellationToken cancellationToken = default);
    void Invalidate();
}
