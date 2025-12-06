using System.Security.Cryptography;
using LicensingService.Core.Entities;
using LicensingService.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LicensingService.Core.Crypto;

/// <summary>
/// Default implementation of ISigningKeyService.
/// Loads key from file/config and stores public keys in database for JWKS.
/// </summary>
public class SigningKeyService : ISigningKeyService
{
    private readonly LicensingDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SigningKeyService> _logger;
    private ECDsa? _activeKey;
    private string? _activeKid;

    public SigningKeyService(
        LicensingDbContext dbContext,
        IConfiguration configuration,
        ILogger<SigningKeyService> logger)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<(ECDsa Key, string Kid)> GetActiveSigningKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_activeKey == null || _activeKid == null)
        {
            await InitializeAsync(cancellationToken);
        }

        if (_activeKey == null || _activeKid == null)
        {
            throw new InvalidOperationException("No active signing key available. Call InitializeAsync first.");
        }

        return (_activeKey, _activeKid);
    }

    public async Task<IReadOnlyList<(ECDsa Key, string Kid, string Algorithm)>> GetPublicKeysAsync(CancellationToken cancellationToken = default)
    {
        // Return all non-retired keys for JWKS endpoint
        var signingKeys = await _dbContext.SigningKeys
            .Where(k => k.Status != SigningKeyStatus.Retired)
            .ToListAsync(cancellationToken);

        var result = new List<(ECDsa, string, string)>();

        foreach (var key in signingKeys)
        {
            try
            {
                // Parse public key from JWK and create ECDsa for verification
                var ecdsa = LoadPublicKeyFromJwk(key.PublicKeyJwks);
                result.Add((ecdsa, key.Kid, key.Algorithm));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load public key {Kid}", key.Kid);
            }
        }

        return result;
    }

    public async Task<string> RotateKeyAsync(CancellationToken cancellationToken = default)
    {
        // Mark current active key as rotated
        var currentActive = await _dbContext.SigningKeys
            .FirstOrDefaultAsync(k => k.Status == SigningKeyStatus.Active, cancellationToken);

        if (currentActive != null)
        {
            currentActive.Status = SigningKeyStatus.Rotated;
            currentActive.RotatedAt = DateTimeOffset.UtcNow;
        }

        // Generate new key
        var newKey = EcdsaKeyHelper.GenerateP256Key();
        var newKid = $"licensing-{GuidHelper.NewId():N}";
        var publicKeyJwk = JwkSerializer.SerializeEcdsaPublicKeyToJwk(newKey, newKid);

        var signingKey = new SigningKey
        {
            Id = GuidHelper.NewId(),
            Kid = newKid,
            Algorithm = "ES256",
            PublicKeyJwks = publicKeyJwk,
            Status = SigningKeyStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.SigningKeys.Add(signingKey);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Update cached key
        _activeKey = newKey;
        _activeKid = newKid;

        _logger.LogInformation("Rotated signing key to {Kid}", newKid);

        return newKid;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Try to load from configuration first
        var keyPath = _configuration["Licensing:SigningKeyPath"];
        var kid = _configuration["Licensing:SigningKeyId"] ?? "licensing-key";

        if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
        {
            var pemKey = await File.ReadAllTextAsync(keyPath, cancellationToken);
            _activeKey = EcdsaKeyHelper.LoadFromPem(pemKey);
            _activeKid = kid;

            // Ensure public key is stored in database
            await EnsurePublicKeyStoredAsync(kid, cancellationToken);
            
            _logger.LogInformation("Loaded signing key from file: {Kid}", kid);
            return;
        }

        // Try to load active key from database
        var activeDbKey = await _dbContext.SigningKeys
            .FirstOrDefaultAsync(k => k.Status == SigningKeyStatus.Active, cancellationToken);

        if (activeDbKey != null)
        {
            _activeKid = activeDbKey.Kid;
            // Note: Private key must be loaded from external storage
            // For now, we'll generate a new key if no file exists
            _logger.LogWarning("Active key {Kid} found in database but private key not available", activeDbKey.Kid);
        }

        // Generate new key if none exists
        if (_activeKey == null)
        {
            _logger.LogInformation("No signing key found, generating new key");
            await RotateKeyAsync(cancellationToken);
        }
    }

    private async Task EnsurePublicKeyStoredAsync(string kid, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.SigningKeys
            .FirstOrDefaultAsync(k => k.Kid == kid, cancellationToken);

        if (existing == null && _activeKey != null)
        {
            var publicKeyJwk = JwkSerializer.SerializeEcdsaPublicKeyToJwk(_activeKey, kid);

            var signingKey = new SigningKey
            {
                Id = GuidHelper.NewId(),
                Kid = kid,
                Algorithm = "ES256",
                PublicKeyJwks = publicKeyJwk,
                Status = SigningKeyStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _dbContext.SigningKeys.Add(signingKey);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static ECDsa LoadPublicKeyFromJwk(string jwkJson)
    {
        var jwk = System.Text.Json.JsonDocument.Parse(jwkJson);
        var root = jwk.RootElement;

        var x = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(root.GetProperty("x").GetString()!);
        var y = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(root.GetProperty("y").GetString()!);
        var crv = root.GetProperty("crv").GetString();

        var curve = crv switch
        {
            "P-256" => ECCurve.NamedCurves.nistP256,
            "P-384" => ECCurve.NamedCurves.nistP384,
            "P-521" => ECCurve.NamedCurves.nistP521,
            _ => ECCurve.NamedCurves.nistP256
        };

        var parameters = new ECParameters
        {
            Curve = curve,
            Q = new ECPoint { X = x, Y = y }
        };

        var ecdsa = ECDsa.Create(parameters);
        return ecdsa;
    }
}
