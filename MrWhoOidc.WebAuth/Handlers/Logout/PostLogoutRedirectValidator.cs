using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Observability;
using System.Security.Cryptography;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Validates and creates opaque references for post-logout redirect URIs.
/// </summary>
public sealed class PostLogoutRedirectValidator(
    AuthDbContext db,
    IAuditSink audit,
    OidcMetrics metrics,
    ILogger<PostLogoutRedirectValidator> logger)
{
    /// <summary>
    /// Validates the post_logout_redirect_uri against the client's allow-list and creates an opaque reference.
    /// Returns the reference ID if validation succeeds, null otherwise.
    /// </summary>
    public async Task<string?> ValidateAndCreateReferenceAsync(
        string postLogoutUri, 
        string clientId, 
        string? state,
        CancellationToken cancellationToken = default)
    {
        var client = await db.Clients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == clientId, cancellationToken)
            .ConfigureAwait(false);

        if (client is null)
        {
            var host = TryGetHost(postLogoutUri);
            audit.Emit("logout.redirect.rejected_client_not_found", new 
            { 
                client_id = clientId, 
                post_logout_host = host, 
                post_logout_hash = audit.HashValue(postLogoutUri) 
            });
            metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", "client_not_found"));
            logger.LogWarning("Rejecting post_logout_redirect_uri because client {ClientId} was not found. host={Host}", clientId, host ?? "unknown");
            return null;
        }

        if (string.IsNullOrEmpty(client.AllowedLogoutRedirectUrisJson))
        {
            var host = TryGetHost(postLogoutUri);
            audit.Emit("logout.redirect.rejected_missing_allowlist", new 
            { 
                client_id = clientId, 
                post_logout_host = host, 
                post_logout_hash = audit.HashValue(postLogoutUri) 
            });
            metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", "no_allow_list"));
            logger.LogInformation("Rejecting post_logout_redirect_uri for client {ClientId}: no allowed logout URIs configured. host={Host}", clientId, host ?? "unknown");
            return null;
        }

        try
        {
            var allowed = JsonSerializer.Deserialize<string[]>(client.AllowedLogoutRedirectUrisJson!) ?? Array.Empty<string>();
            
            if (!UrlComparison.IsAllowed(postLogoutUri, allowed))
            {
                var host = TryGetHost(postLogoutUri);
                audit.Emit("logout.redirect.rejected_not_allowed", new 
                { 
                    client_id = clientId, 
                    post_logout_host = host, 
                    post_logout_hash = audit.HashValue(postLogoutUri) 
                });
                metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", "post_logout_not_allowed"));
                logger.LogInformation("Rejecting post_logout_redirect_uri for client {ClientId}: value not on allow list. host={Host}", clientId, host ?? "unknown");
                return null;
            }

            // Create opaque reference
            var idBytes = RandomNumberGenerator.GetBytes(16); // 128-bit
            var id = Base64UrlEncoder.Encode(idBytes); // url-safe, no padding
            
            var entity = new LogoutRedirectReference
            {
                Id = id,
                ClientId = clientId,
                RedirectUri = postLogoutUri,
                State = string.IsNullOrEmpty(state) ? null : state,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                Used = false
            };

            db.LogoutRedirectReferences.Add(entity);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            audit.Emit("logout.redirect.ref.created", new { client_id = clientId, has_state = !string.IsNullOrEmpty(state) });
            return id;
        }
        catch (JsonException ex)
        {
            var host = TryGetHost(postLogoutUri);
            audit.Emit("logout.redirect.rejected_invalid_allowlist", new 
            { 
                client_id = clientId, 
                post_logout_host = host, 
                post_logout_hash = audit.HashValue(postLogoutUri) 
            });
            metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", "invalid_allow_list"));
            logger.LogWarning(ex, "Rejecting post_logout_redirect_uri for client {ClientId}: allow list JSON malformed. host={Host}", clientId, host ?? "unknown");
            return null;
        }
    }

    private static string? TryGetHost(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : null;
    }
}
