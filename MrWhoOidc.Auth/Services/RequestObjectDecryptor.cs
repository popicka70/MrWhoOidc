using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.Auth.Services;

public interface IRequestObjectDecryptor
{
    /// <summary>
    /// Attempts to decrypt a JWE request object and returns the inner JWT (nested JWT) when present.
    /// Returns null when the input does not look like a JWE.
    /// Throws when the input looks like a JWE but cannot be decrypted.
    /// </summary>
    Task<string?> TryDecryptToInnerJwtAsync(string requestObject, CancellationToken ct = default);
}

internal sealed class RequestObjectDecryptor(IKeyStore keyStore, ILogger<RequestObjectDecryptor> logger) : IRequestObjectDecryptor
{
    public async Task<string?> TryDecryptToInnerJwtAsync(string requestObject, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestObject))
        {
            return null;
        }

        // JWE compact serialization has 5 parts.
        var parts = requestObject.Split('.');
        if (parts.Length != 5)
        {
            return null;
        }

        ValidateSupportedHeader(parts[0]);

        var encKey = await keyStore.GetActiveEncryptionKeyAsync(ct).ConfigureAwait(false);

        // Decrypt the JWE and capture the inner token string (nested JWT) via SignatureValidator.
        // If the decrypted content is not a nested JWT, we treat it as unsupported.
        string? inner = null;
        var tvp = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            RequireSignedTokens = false,
            ValidateIssuerSigningKey = false,
            TokenDecryptionKey = encKey,
            SignatureValidator = (token, _) =>
            {
                inner = token;
                return new JwtSecurityToken(token);
            }
        };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            _ = handler.ValidateToken(requestObject, tvp, out _);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JAR: failed to decrypt request object");
            throw;
        }

        if (string.IsNullOrWhiteSpace(inner))
        {
            throw new InvalidOperationException("Encrypted request object did not contain a nested JWT");
        }

        return inner;
    }

    private static void ValidateSupportedHeader(string headerPart)
    {
        static string Base64UrlToBase64(string base64Url)
            => base64Url.Replace('-', '+').Replace('_', '/') + new string('=', (4 - base64Url.Length % 4) % 4);

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(Base64UrlToBase64(headerPart)));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var alg = root.TryGetProperty("alg", out var a) ? a.GetString() : null;
            var enc = root.TryGetProperty("enc", out var e) ? e.GetString() : null;

            // Minimal initial support: RSA-OAEP + A256CBC-HS512.
            if (!string.Equals(alg, SecurityAlgorithms.RsaOAEP, StringComparison.Ordinal) ||
                !string.Equals(enc, SecurityAlgorithms.Aes256CbcHmacSha512, StringComparison.Ordinal))
            {
                throw new NotSupportedException($"Unsupported request object encryption (alg={alg ?? "(null)"}, enc={enc ?? "(null)"})");
            }
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Invalid JWE header", ex);
        }
    }
}
