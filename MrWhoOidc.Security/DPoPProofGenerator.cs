using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace MrWhoOidc.Security;

public sealed record DPoPProofRequest(string HttpMethod, Uri Uri, string? AccessToken = null, string? Nonce = null);

public interface IDPoPProofGenerator
{
    ValueTask<string> CreateProofAsync(DPoPProofRequest request, CancellationToken cancellationToken = default);
}

public interface IDPoPKeyStore
{
    ValueTask<DPoPKeyMaterial> GetCurrentKeyAsync(CancellationToken cancellationToken = default);
}

public sealed record DPoPKeyMaterial(SecurityKey Key, JsonWebKey JsonWebKey, string JwkJson, DateTimeOffset ExpiresAt);

public sealed class EphemeralDpopKeyStore : IDPoPKeyStore, IDisposable
{
    private readonly TimeSpan _rotationInterval;
    private DPoPKeyMaterial? _current;
    private readonly object _lock = new();

    public EphemeralDpopKeyStore() : this(TimeSpan.FromHours(1)) { }

    public EphemeralDpopKeyStore(TimeSpan rotationInterval)
    {
        if (rotationInterval < TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationInterval), "Rotation interval must be at least five minutes.");
        }
        _rotationInterval = rotationInterval;
    }

    public ValueTask<DPoPKeyMaterial> GetCurrentKeyAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_current is null || _current.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _current = CreateKey();
            }
            return ValueTask.FromResult(_current!);
        }
    }

    private DPoPKeyMaterial CreateKey()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var securityKey = new ECDsaSecurityKey(ecdsa)
        {
            KeyId = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16))
        };

        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(securityKey);
        jwk.KeyId = securityKey.KeyId;
        jwk.Alg = SecurityAlgorithms.EcdsaSha256;
        jwk.Use = JsonWebKeyUseNames.Sig;

        var jwkJson = jwk.ToString() ?? throw new InvalidOperationException("Unable to serialize JsonWebKey.");
        return new DPoPKeyMaterial(securityKey, jwk, jwkJson, DateTimeOffset.UtcNow.Add(_rotationInterval));
    }

    public void Dispose()
    {
        if (_current?.Key is ECDsaSecurityKey ecdsaKey)
        {
            ecdsaKey.ECDsa?.Dispose();
        }
    }
}

public sealed class JwtDpopProofGenerator : IDPoPProofGenerator
{
    private readonly IDPoPKeyStore _keyStore;

    public JwtDpopProofGenerator(IDPoPKeyStore keyStore)
    {
        _keyStore = keyStore;
    }

    public async ValueTask<string> CreateProofAsync(DPoPProofRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Uri);

        var keyMaterial = await _keyStore.GetCurrentKeyAsync(cancellationToken).ConfigureAwait(false);
        var credentials = new SigningCredentials(keyMaterial.Key, keyMaterial.Key is ECDsaSecurityKey ? SecurityAlgorithms.EcdsaSha256 : SecurityAlgorithms.RsaSha256);

        var now = DateTimeOffset.UtcNow;
        var payload = new JwtPayload
        {
            {"htm", request.HttpMethod.ToUpperInvariant()},
            {"htu", request.Uri.ToString()},
            {"iat", now.ToUnixTimeSeconds()},
            {"jti", Guid.NewGuid().ToString("N")}
        };

        if (!string.IsNullOrEmpty(request.AccessToken))
        {
            var hash = SHA256.HashData(Encoding.ASCII.GetBytes(request.AccessToken));
            payload["ath"] = Base64UrlEncoder.Encode(hash);
        }

        if (!string.IsNullOrEmpty(request.Nonce))
        {
            payload["nonce"] = request.Nonce;
        }

        using var jwkDocument = JsonDocument.Parse(keyMaterial.JwkJson);
        var header = new JwtHeader(credentials)
        {
            {"typ", "dpop+jwt"},
            {"jwk", jwkDocument.RootElement.Clone() }
        };

        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(token);
    }
}
