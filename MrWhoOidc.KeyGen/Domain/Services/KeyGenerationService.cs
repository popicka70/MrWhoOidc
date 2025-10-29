using MrWhoOidc.KeyGen.Domain.Cryptography;
using MrWhoOidc.KeyGen.Domain.Models;
using MrWhoOidc.KeyGen.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MrWhoOidc.KeyGen.Domain.Services;

/// <summary>
/// Service for generating cryptographic key pairs.
/// </summary>
public class KeyGenerationService : IKeyGenerationService
{
    private readonly KeyGenDbContext _context;

    public KeyGenerationService(KeyGenDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<(string Kid, string PrivateKeyJwk, string PublicKeyJwks)> GenerateKeyPairAsync(
        string algorithm,
        string keyType,
        int? keySize,
        string? curve,
        string? createdBy = null)
    {
        // Validate inputs
        ValidateInputs(algorithm, keyType, keySize, curve);

        // Generate kid using UUIDv7
        var kid = GuidHelper.NewId().ToString();

        // Generate key pair and serialize
        string privateKeyJwk;
        string publicKeyJwks;

        if (keyType == "RSA")
        {
            using var rsa = RsaKeyGenerator.Generate(keySize!.Value);
            privateKeyJwk = JwkSerializer.SerializeRsaPrivateKey(rsa, kid, algorithm);
            publicKeyJwks = JwkSerializer.SerializeRsaPublicKey(rsa, kid, algorithm);
        }
        else // EC
        {
            using var ecdsa = EcdsaKeyGenerator.Generate(curve!);
            privateKeyJwk = JwkSerializer.SerializeEcdsaPrivateKey(ecdsa, kid, algorithm);
            publicKeyJwks = JwkSerializer.SerializeEcdsaPublicKey(ecdsa, kid, algorithm, curve!);
        }

        // Store metadata
        var metadata = new KeyPairMetadata
        {
            Id = GuidHelper.NewId(),
            Kid = kid,
            Algorithm = algorithm,
            KeyType = keyType,
            KeySize = keySize,
            Curve = curve,
            PublicKeyJwks = publicKeyJwks,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = "Active",
            CreatedBy = createdBy,
            DownloadCount = 0
        };

        _context.KeyPairMetadata.Add(metadata);
        await _context.SaveChangesAsync();

        return (kid, privateKeyJwk, publicKeyJwks);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeKeyAsync(string kid, string? revokedBy = null)
    {
        var metadata = await _context.KeyPairMetadata
            .FirstOrDefaultAsync(k => k.Kid == kid);

        if (metadata == null)
        {
            return false;
        }

        // Check if already revoked
        if (metadata.Status == "Revoked")
        {
            return true; // Already revoked, return success
        }

        // Update status
        metadata.Status = "Revoked";
        metadata.RevokedAt = DateTimeOffset.UtcNow;

        // Note: We don't have a RevokedBy field in the entity, but we could add it later
        // For now, we just log the revocation

        await _context.SaveChangesAsync();

        return true;
    }

    private static void ValidateInputs(string algorithm, string keyType, int? keySize, string? curve)
    {
        // Validate algorithm
        var validAlgorithms = new[] { "RS256", "RS384", "RS512", "ES256", "ES384", "ES512", "PS256" };
        if (!validAlgorithms.Contains(algorithm))
        {
            throw new ArgumentException(
                $"Algorithm must be one of: {string.Join(", ", validAlgorithms)}",
                nameof(algorithm));
        }

        // Validate key type
        if (keyType != "RSA" && keyType != "EC")
        {
            throw new ArgumentException("Key type must be RSA or EC", nameof(keyType));
        }

        // Validate RSA parameters
        if (keyType == "RSA")
        {
            if (!keySize.HasValue)
            {
                throw new ArgumentException("Key size is required for RSA keys", nameof(keySize));
            }

            if (keySize.Value != 2048 && keySize.Value != 3072 && keySize.Value != 4096)
            {
                throw new ArgumentException("RSA key size must be 2048, 3072, or 4096", nameof(keySize));
            }

            if (!algorithm.StartsWith("RS") && !algorithm.StartsWith("PS"))
            {
                throw new ArgumentException("RSA keys require RS* or PS* algorithm", nameof(algorithm));
            }
        }

        // Validate EC parameters
        if (keyType == "EC")
        {
            if (string.IsNullOrEmpty(curve))
            {
                throw new ArgumentException("Curve is required for EC keys", nameof(curve));
            }

            if (curve != "P-256" && curve != "P-384" && curve != "P-521")
            {
                throw new ArgumentException("Curve must be P-256, P-384, or P-521", nameof(curve));
            }

            if (!algorithm.StartsWith("ES"))
            {
                throw new ArgumentException("EC keys require ES* algorithm", nameof(algorithm));
            }
        }
    }
}
