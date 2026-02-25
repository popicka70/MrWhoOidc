using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services.SubjectIdentifiers;

public static class SectorIdentifierUriValidator
{
    public static async Task ValidateAsync(
        Uri sectorIdentifierUri,
        IReadOnlyCollection<string> clientRedirectUris,
        HttpClient httpClient,
        CancellationToken ct = default)
    {
        if (sectorIdentifierUri is null) throw new ArgumentNullException(nameof(sectorIdentifierUri));
        if (httpClient is null) throw new ArgumentNullException(nameof(httpClient));

        if (!string.Equals(sectorIdentifierUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("sector_identifier_uri must use HTTPS");
        }

        if (string.IsNullOrWhiteSpace(sectorIdentifierUri.Host))
        {
            throw new InvalidOperationException("sector_identifier_uri must be an absolute URI with a host");
        }

        if (clientRedirectUris is null || clientRedirectUris.Count == 0)
        {
            throw new InvalidOperationException("Cannot validate sector_identifier_uri: client has no redirect URIs configured");
        }

        var normalizedClientRedirectUris = new List<string>(clientRedirectUris.Count);
        foreach (var u in clientRedirectUris)
        {
            if (!UrlComparison.IsValidAbsolute(u))
            {
                throw new InvalidOperationException($"Cannot validate sector_identifier_uri: invalid redirect URI '{u}'");
            }
            normalizedClientRedirectUris.Add(UrlComparison.NormalizeForAllowList(u));
        }

        var sectorRedirectUris = await FetchRedirectUrisAsync(httpClient, sectorIdentifierUri, ct).ConfigureAwait(false);
        var normalizedSectorRedirectUris = new HashSet<string>(
            sectorRedirectUris.Select(UrlComparison.NormalizeForAllowList),
            StringComparer.Ordinal);

        var missing = normalizedClientRedirectUris
            .Where(u => !normalizedSectorRedirectUris.Contains(u))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "sector_identifier_uri redirect_uris did not include all client redirect URIs. Missing: " + string.Join(", ", missing));
        }
    }

    private static async Task<IReadOnlyCollection<string>> FetchRedirectUrisAsync(HttpClient httpClient, Uri sectorIdentifierUri, CancellationToken ct)
    {
        // Use a safe HttpClient to prevent SSRF via DNS rebinding or redirects to internal IPs.
        // We use a separate instance to ensure the security-hardened ConnectCallback is used.
        using var safeHttp = MrWhoOidc.Auth.Utils.NetworkSecurity.CreateSafeHttpClient(TimeSpan.FromSeconds(10));

        HttpResponseMessage response;
        try
        {
            response = await safeHttp.GetAsync(sectorIdentifierUri, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to fetch sector_identifier_uri", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"sector_identifier_uri returned {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        string json;
        try
        {
            json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to read sector_identifier_uri response", ex);
        }

        string[] redirectUris;
        try
        {
            redirectUris = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("sector_identifier_uri must return a JSON array of redirect URIs", ex);
        }

        var cleaned = redirectUris
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .ToArray();

        if (cleaned.Length == 0)
        {
            throw new InvalidOperationException("sector_identifier_uri returned an empty redirect URI list");
        }

        foreach (var u in cleaned)
        {
            if (!UrlComparison.IsValidAbsolute(u))
            {
                throw new InvalidOperationException($"sector_identifier_uri returned an invalid redirect URI '{u}'");
            }
        }

        return cleaned;
    }
}
