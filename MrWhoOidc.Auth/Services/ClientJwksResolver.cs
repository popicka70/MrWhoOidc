using System.Net.Http;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

public sealed class ClientJwksResolver : IClientJwksProvider
{
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(
        Client client,
        IHttpClientFactory? httpClientFactory = null,
        IJwksCache? jwksCache = null,
        int cacheSeconds = 300,
        CancellationToken ct = default)
        => (await GetJsonWebKeysAsync(client, httpClientFactory, jwksCache, cacheSeconds, ct).ConfigureAwait(false))
            .Cast<SecurityKey>()
            .ToArray();

    public async Task<JsonWebKey?> GetEncryptionKeyAsync(
        Client client,
        IHttpClientFactory? httpClientFactory = null,
        IJwksCache? jwksCache = null,
        int cacheSeconds = 300,
        CancellationToken ct = default)
    {
        var keys = await GetJsonWebKeysAsync(client, httpClientFactory, jwksCache, cacheSeconds, ct).ConfigureAwait(false);

        return keys.FirstOrDefault(k => string.Equals(k.Kty, "RSA", StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(k.Use, "enc", StringComparison.OrdinalIgnoreCase))
            ?? keys.FirstOrDefault(k => string.Equals(k.Kty, "RSA", StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyCollection<SecurityKey> ParseSecurityKeys(string? jwkOrJwksJson)
        => ParseJsonWebKeys(jwkOrJwksJson).Cast<SecurityKey>().ToArray();

    private async Task<IReadOnlyCollection<JsonWebKey>> GetJsonWebKeysAsync(
        Client client,
        IHttpClientFactory? httpClientFactory,
        IJwksCache? jwksCache,
        int cacheSeconds,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(client.PublicJwksJson))
        {
            return ParseJsonWebKeys(client.PublicJwksJson);
        }

        if (string.IsNullOrWhiteSpace(client.PublicJwksUri)
            || !Uri.TryCreate(client.PublicJwksUri, UriKind.Absolute, out var jwksUri)
            || (!string.Equals(jwksUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(jwksUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return Array.Empty<JsonWebKey>();
        }

        var ttl = TimeSpan.FromSeconds(cacheSeconds > 0 ? cacheSeconds : 300);

        if (jwksCache is not null && httpClientFactory is not null)
        {
            var set = await jwksCache.GetAsync(client.PublicJwksUri!, ttl, httpClientFactory, ct).ConfigureAwait(false);
            if (set?.Keys is { Count: > 0 })
            {
                return set.Keys.ToArray();
            }

            return Array.Empty<JsonWebKey>();
        }

        using var http = CreateHttpClient(httpClientFactory);
        using var response = await http.GetAsync(jwksUri, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseJsonWebKeys(json);
    }

    private HttpClient CreateHttpClient(IHttpClientFactory? httpClientFactory)
    {
        if (httpClientFactory is null)
        {
            return NetworkSecurity.CreateSafeHttpClient(_defaultTimeout);
        }

        try
        {
            return httpClientFactory.CreateClient(SectorIdentifierResolver.SafeHttpClientName);
        }
        catch (InvalidOperationException)
        {
            return httpClientFactory.CreateClient();
        }
    }

    private IReadOnlyCollection<JsonWebKey> ParseJsonWebKeys(string? jwkOrJwksJson)
    {
        if (string.IsNullOrWhiteSpace(jwkOrJwksJson))
        {
            return Array.Empty<JsonWebKey>();
        }

        try
        {
            using var doc = JsonDocument.Parse(jwkOrJwksJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("keys", out _))
            {
                return new JsonWebKeySet(jwkOrJwksJson).Keys.ToArray();
            }

            // If the root is an array, treat it as a JWKS (keys array)
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var keys = new List<JsonWebKey>();
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        keys.Add(new JsonWebKey(element.GetRawText()));
                    }
                }
                return keys;
            }

            return new[] { new JsonWebKey(jwkOrJwksJson) };
        }
        catch
        {
            return Array.Empty<JsonWebKey>();
        }
    }
}
