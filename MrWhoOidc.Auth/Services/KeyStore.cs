using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IKeyStore
{
    Task<RsaJwk> GetActiveSigningKeyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RsaJwk>> GetPublicJwksAsync(CancellationToken ct = default);
}

internal sealed class KeyStore(AuthDbContext db) : IKeyStore
{
    public async Task<RsaJwk> GetActiveSigningKeyAsync(CancellationToken ct = default)
    {
        var current = await db.SigningKeys.OrderByDescending(k => k.CreatedAt).FirstOrDefaultAsync(ct);
        if (current is null)
        {
            // Generate a new RSA keypair and persist it
            using var rsa = RSA.Create(2048);
            var kid = Guid.NewGuid().ToString("N");
            var jwk = RsaJwk.FromRSA(rsa, kid, alg: "RS256", includePrivate: true);

            db.SigningKeys.Add(new Persistence.SigningKey
            {
                Kid = jwk.Kid,
                Alg = jwk.Alg,
                JwkJson = jwk.ToJson(includePrivate: true)
            });
            await db.SaveChangesAsync(ct);
            return jwk;
        }

        // Load from DB
        var stored = System.Text.Json.JsonSerializer.Deserialize<RsaJwk>(current.JwkJson)!;
        return stored;
    }

    public async Task<IReadOnlyList<RsaJwk>> GetPublicJwksAsync(CancellationToken ct = default)
    {
        var keys = await db.SigningKeys.OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        return keys
            .Select(k => System.Text.Json.JsonSerializer.Deserialize<RsaJwk>(k.JwkJson)!)
            .Select(k => new RsaJwk
            {
                Kty = k.Kty,
                Kid = k.Kid,
                Alg = k.Alg,
                Use = k.Use,
                N = k.N,
                E = k.E,
                D = null, P = null, Q = null, DP = null, DQ = null, QI = null
            })
            .ToList();
    }
}
