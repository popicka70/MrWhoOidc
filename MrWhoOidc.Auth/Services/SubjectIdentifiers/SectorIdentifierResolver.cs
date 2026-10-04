using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.SubjectIdentifiers;

public sealed class SectorIdentifierResolver(IHttpClientFactory httpClientFactory) : ISectorIdentifierResolver
{
    /// <summary>Named HttpClient configured with SSRF protection.</summary>
    public const string SafeHttpClientName = "sector-identifier-safe";

    public Task<string> ResolveSectorIdentifierAsync(Client client, CancellationToken ct = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));

        if (!string.IsNullOrWhiteSpace(client.SectorIdentifierUri))
        {
            // The sector document is validated when the client is registered or saved
            // (ValidateSectorIdentifierUriAsync). At token time only its host matters, so no fetch:
            // an unreachable or slow sector host must not break or stall token issuance.
            return Task.FromResult(HostOf(client.SectorIdentifierUri));
        }

        var sector = ResolveFromAllowedLoginRedirectUris(client.AllowedLoginRedirectUrisJson);
        return Task.FromResult(sector);
    }

    public async Task ValidateSectorIdentifierUriAsync(string sectorIdentifierUri, IReadOnlyCollection<string> redirectUris, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(sectorIdentifierUri?.Trim(), UriKind.Absolute, out var sectorUri) || string.IsNullOrWhiteSpace(sectorUri.Host))
        {
            throw new InvalidOperationException("sector_identifier_uri must be an absolute URI with a host");
        }

        // Use a safe HttpClient to prevent SSRF via DNS rebinding or redirects to internal IPs.
        var http = httpClientFactory.CreateClient(SafeHttpClientName);
        await SectorIdentifierUriValidator.ValidateAsync(sectorUri, redirectUris, http, ct).ConfigureAwait(false);
    }

    internal static string HostOf(string sectorIdentifierUri)
    {
        if (!Uri.TryCreate(sectorIdentifierUri.Trim(), UriKind.Absolute, out var sectorUri) || string.IsNullOrWhiteSpace(sectorUri.Host))
        {
            throw new InvalidOperationException("sector_identifier_uri must be an absolute URI with a host");
        }

        // Normalize sector identifier consistently (host lowercased)
        return sectorUri.Host.ToLowerInvariant();
    }

    internal static string ResolveFromAllowedLoginRedirectUris(string? allowedLoginRedirectUrisJson)
    {
        if (string.IsNullOrWhiteSpace(allowedLoginRedirectUrisJson))
        {
            throw new InvalidOperationException("Cannot derive sector identifier: client has no allowed login redirect URIs configured");
        }

        string[] redirectUris;
        try
        {
            redirectUris = JsonSerializer.Deserialize<string[]>(allowedLoginRedirectUrisJson) ?? Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Cannot derive sector identifier: allowed login redirect URIs are not valid JSON", ex);
        }

        if (redirectUris.Length == 0)
        {
            throw new InvalidOperationException("Cannot derive sector identifier: allowed login redirect URIs list is empty");
        }

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in redirectUris)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new InvalidOperationException($"Cannot derive sector identifier: invalid redirect URI '{raw}'");
            }

            hosts.Add(uri.Host);
        }

        if (hosts.Count != 1)
        {
            throw new InvalidOperationException($"Cannot derive sector identifier: expected exactly one redirect host but found {hosts.Count}");
        }

        return hosts.Single().ToLowerInvariant();
    }
}
