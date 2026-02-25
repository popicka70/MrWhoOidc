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
    internal const string SafeHttpClientName = "sector-identifier-safe";

    public Task<string> ResolveSectorIdentifierAsync(Client client, CancellationToken ct = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));

        if (!string.IsNullOrWhiteSpace(client.SectorIdentifierUri))
        {
            return ResolveFromSectorIdentifierUriAsync(client, ct);
        }

        var sector = ResolveFromAllowedLoginRedirectUris(client.AllowedLoginRedirectUrisJson);
        return Task.FromResult(sector);
    }

    private async Task<string> ResolveFromSectorIdentifierUriAsync(Client client, CancellationToken ct)
    {
        if (!Uri.TryCreate(client.SectorIdentifierUri?.Trim(), UriKind.Absolute, out var sectorUri))
        {
            throw new InvalidOperationException("sector_identifier_uri must be a valid absolute URI");
        }

        if (string.IsNullOrWhiteSpace(sectorUri.Host))
        {
            throw new InvalidOperationException("sector_identifier_uri must be an absolute URI with a host");
        }

        var redirectUris = ParseAllowedLoginRedirectUris(client.AllowedLoginRedirectUrisJson);

        // Use a safe HttpClient to prevent SSRF via DNS rebinding or redirects to internal IPs.
        var http = httpClientFactory.CreateClient(SafeHttpClientName);

        await SectorIdentifierUriValidator.ValidateAsync(sectorUri, redirectUris, http, ct).ConfigureAwait(false);

        // Normalize sector identifier consistently (host lowercased)
        return sectorUri.Host.ToLowerInvariant();
    }

    internal static IReadOnlyCollection<string> ParseAllowedLoginRedirectUris(string? allowedLoginRedirectUrisJson)
    {
        if (string.IsNullOrWhiteSpace(allowedLoginRedirectUrisJson))
        {
            throw new InvalidOperationException("Client has no allowed login redirect URIs configured");
        }

        string[] redirectUris;
        try
        {
            redirectUris = JsonSerializer.Deserialize<string[]>(allowedLoginRedirectUrisJson) ?? Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Allowed login redirect URIs are not valid JSON", ex);
        }

        var cleaned = redirectUris
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .ToArray();

        if (cleaned.Length == 0)
        {
            throw new InvalidOperationException("Allowed login redirect URIs list is empty");
        }

        return cleaned;
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
