using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;

namespace MrWhoOidc.Auth.Services;

public interface IJwksCache
{
    Task<JsonWebKeySet?> GetAsync(string jwksUri, TimeSpan ttl, IHttpClientFactory httpFactory, CancellationToken ct = default, string? httpClientName = null);
}

public sealed class JwksCache : IJwksCache
{
    private sealed record Entry(JsonWebKeySet Set, DateTimeOffset ExpiresAt);
    // Instance-level cache so each host/test scope gets an isolated view. A previous static implementation caused
    // test flakiness when ephemeral upstream signing keys changed while the cached JWKS (same URI) persisted.
    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    public async Task<JsonWebKeySet?> GetAsync(string jwksUri, TimeSpan ttl, IHttpClientFactory httpFactory, CancellationToken ct = default, string? httpClientName = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(jwksUri, out var e) && e.ExpiresAt > now)
            return e.Set;

        try
        {
            HttpClient http;
            var disposeHttp = false;
            try
            {
                http = httpFactory.CreateClient(httpClientName ?? SectorIdentifierResolver.SafeHttpClientName);
            }
            catch (InvalidOperationException)
            {
                http = MrWhoOidc.Auth.Utils.NetworkSecurity.CreateSafeHttpClient(TimeSpan.FromSeconds(10));
                disposeHttp = true;
            }

            try
            {
                using var resp = await http.GetAsync(jwksUri, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await JwksHttp.ReadBoundedStringAsync(resp.Content, ct).ConfigureAwait(false);
                var set = new JsonWebKeySet(json);
                _cache[jwksUri] = new Entry(set, now.Add(ttl));
                return set;
            }
            finally
            {
                if (disposeHttp)
                {
                    http.Dispose();
                }
            }
        }
        catch
        {
            // On failure, keep stale if present
            if (e is not null) return e.Set;
            return null;
        }
    }
}

/// <summary>
/// Reads a remote JWKS with a size cap so a hostile jwks_uri cannot make the server buffer an unbounded body.
/// </summary>
internal static class JwksHttp
{
    public const int MaxJwksResponseBytes = 256 * 1024;

    public static async Task<string> ReadBoundedStringAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxJwksResponseBytes)
        {
            throw new InvalidOperationException("JWKS response exceeds the size limit");
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxJwksResponseBytes)
            {
                throw new InvalidOperationException("JWKS response exceeds the size limit");
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
