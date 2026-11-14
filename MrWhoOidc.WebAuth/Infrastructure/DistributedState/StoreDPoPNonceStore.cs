using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Security;

namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>
/// DPoP nonce store over <see cref="IDistributedStateStore"/>. Nonces are shared by every replica so
/// a client's nonce remains valid no matter which instance handles the next request.
/// </summary>
internal sealed class StoreDPoPNonceStore(
    IDistributedStateStore store,
    ILogger<StoreDPoPNonceStore> logger) : IDPoPNonceStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    public async Task<(bool ok, string nonce)> ValidateOrIssueAsync(
        string endpoint,
        string clientIp,
        string? jkt,
        string? provided,
        CancellationToken ct = default)
    {
        var key = Key(endpoint, clientIp, jkt);

        try
        {
            var existing = await store.GetAsync(key, ct).ConfigureAwait(false);
            if (existing is not null
                && !string.IsNullOrEmpty(provided)
                && string.Equals(existing, provided, StringComparison.Ordinal))
            {
                return (true, existing);
            }

            var nonce = CreateNonce();
            await store.SetAsync(key, nonce, Ttl, ct).ConfigureAwait(false);
            return (false, nonce);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cannot validate or persist a nonce right now. Issue a challenge anyway: the caller
            // rejects the request, and a later request can succeed once the store recovers.
            logger.LogWarning(ex, "Distributed state unavailable while validating DPoP nonce; issuing a challenge.");
            return (false, CreateNonce());
        }
    }

    internal static string Key(string endpoint, string clientIp, string? jkt)
        => $"dpop:nonce:{endpoint}:{HashIp(clientIp)}:{(jkt ?? "no")}";

    // Never put the raw client IP into a stored key.
    private static string HashIp(string clientIp)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(clientIp ?? string.Empty));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private static string CreateNonce()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
