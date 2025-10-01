using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Infrastructure;
using System.Web;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Builds front-channel logout iframe URLs for registered RPs.
/// </summary>
public sealed class FrontChannelLogoutNotifier(AuthDbContext db)
{
    /// <summary>
    /// Retrieves all clients with front-channel logout URIs and builds iframe URLs.
    /// </summary>
    public async Task<List<string>> GetFrontChannelIframeUrlsAsync(string issuer, string? idTokenHint, string? sidFromQuery, CancellationToken cancellationToken = default)
    {
        var clients = await db.Clients
            .AsNoTracking()
            .Where(c => !string.IsNullOrEmpty(c.FrontChannelLogoutUri))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var iframes = new List<string>();

        foreach (var client in clients)
        {
            var uri = client.FrontChannelLogoutUri!;
            var hasQuery = uri.Contains('?', StringComparison.Ordinal);
            var sep = hasQuery ? '&' : '?';
            var url = uri + sep + "iss=" + Uri.EscapeDataString(issuer);

            if (client.FrontChannelLogoutSessionRequired)
            {
                var sidValue = !string.IsNullOrEmpty(sidFromQuery) 
                    ? sidFromQuery 
                    : (idTokenHint != null ? JwtLightParser.TryGetClaim(idTokenHint, "sid") : null);
                    
                if (!string.IsNullOrEmpty(sidValue))
                {
                    url += "&sid=" + Uri.EscapeDataString(sidValue);
                }
            }

            iframes.Add(url);
        }

        return iframes;
    }
}
