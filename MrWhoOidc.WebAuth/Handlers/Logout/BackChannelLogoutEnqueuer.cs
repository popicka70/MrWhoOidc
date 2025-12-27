using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Background;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Enqueues back-channel logout notifications to the outbox for background delivery.
/// </summary>
public sealed class BackChannelLogoutEnqueuer(
    AuthDbContext db,
    ILogoutTokenService tokenService,
    ILogger<BackChannelLogoutEnqueuer> logger,
    IAuditSink audit,
    OidcEndpointMetrics metrics,
    IOptionsMonitor<BackchannelFeatureOptions> featureOpts,
    IConfiguration config)
{
    /// <summary>
    /// Enqueues back-channel logout notifications for all registered clients with BackChannelLogoutUri.
    /// </summary>
    public async Task EnqueueNotificationsAsync(
        HttpContext http,
        string issuer,
        string? idTokenHint,
        string? sidFromQuery,
        CancellationToken cancellationToken = default)
    {
        if (!featureOpts.CurrentValue.Enabled)
        {
            logger.LogInformation("BCL emission disabled by feature flag - skipping enqueue");
            return;
        }

        var clients = await db.Clients
            .AsNoTracking()
            .Where(c => !string.IsNullOrEmpty(c.BackChannelLogoutUri))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (clients.Count == 0)
        {
            return;
        }

        logger.LogInformation("Enqueuing BCL notifications for {Count} clients", clients.Count);

        var allowList = config.GetSection("Backchannel:AllowHosts").Get<string[]>() ?? Array.Empty<string>();
        var blockList = config.GetSection("Backchannel:BlockHosts").Get<string[]>() ?? Array.Empty<string>();

        var sub = idTokenHint != null ? JwtLightParser.TryGetClaim(idTokenHint, "sub") : null;
        var sid = !string.IsNullOrEmpty(sidFromQuery) ? sidFromQuery : (idTokenHint != null ? JwtLightParser.TryGetClaim(idTokenHint, "sid") : null);

        foreach (var client in clients)
        {
            var token = await tokenService.CreateLogoutTokenAsync(issuer, client.ClientId, sub, sid, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                logger.LogWarning("Skipping BCL for client {ClientId}: unable to create logout token (no sub or sid)", client.ClientId);
                continue;
            }

            // Apply allow/block list filtering
            if (Uri.TryCreate(client.BackChannelLogoutUri, UriKind.Absolute, out var target))
            {
                var host = target.Host;

                if (blockList.Contains(host, StringComparer.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Skipping BCL for client {ClientId}: host {Host} is blocked", client.ClientId, host);
                    continue;
                }

                if (allowList.Length > 0 && !allowList.Contains(host, StringComparer.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Skipping BCL for client {ClientId}: host {Host} not in allow-list", client.ClientId, host);
                    continue;
                }
            }

            var entity = new BackchannelLogoutNotification
            {
                ClientDbId = client.Id,
                ClientId = client.ClientId,
                TargetUri = client.BackChannelLogoutUri!,
                LogoutToken = token,
                Sid = sid,
                Sub = sub,
                Status = "pending",
                AttemptCount = 0,
                MaxAttempts = 5,
                CreatedAt = DateTimeOffset.UtcNow
            };

            db.BackchannelLogoutNotifications.Add(entity);

            logger.LogInformation(
                "BCL enqueue: client={ClientId} target={TargetHost} sid={HasSid} sub={HasSub}",
                entity.ClientId,
                new Uri(entity.TargetUri).Host,
                !string.IsNullOrEmpty(entity.Sid),
                !string.IsNullOrEmpty(entity.Sub));

            metrics.BclEmitted.Add(1, new KeyValuePair<string, object?>("client_id", entity.ClientId));

            var httpIp = http.Connection.RemoteIpAddress?.ToString();
            audit.Emit("bcl.enqueue", new
            {
                client_id = entity.ClientId,
                target = new Uri(entity.TargetUri).Host,
                sid_hash = audit.HashValue(entity.Sid),
                sub_hash = audit.HashValue(entity.Sub),
                created_at = entity.CreatedAt,
                ip = httpIp
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
