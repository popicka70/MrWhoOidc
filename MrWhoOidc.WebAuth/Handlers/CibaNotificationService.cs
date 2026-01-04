using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Handlers;

/// <summary>
/// Service for notifying users of CIBA authentication requests.
/// Implementations can use push notifications, SMS, email, or other mechanisms.
/// </summary>
public interface ICibaNotificationService
{
    /// <summary>
    /// Notify the user that authentication has been requested.
    /// The user should be directed to the CIBA consent page to approve/deny the request.
    /// </summary>
    /// <param name="request">The CIBA authentication request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyUserAsync(CibaAuthenticationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a ping notification to the client after user authorization (for ping mode).
    /// </summary>
    /// <param name="request">The authorized CIBA authentication request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendPingNotificationAsync(CibaAuthenticationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default (no-op) CIBA notification service.
/// In production, replace with an implementation that sends push notifications, SMS, etc.
/// </summary>
public sealed class DefaultCibaNotificationService(ILogger<DefaultCibaNotificationService> logger) : ICibaNotificationService
{
    public Task NotifyUserAsync(CibaAuthenticationRequest request, CancellationToken cancellationToken = default)
    {
        // Default implementation just logs - in production, implement actual notification
        logger.LogInformation("[CIBA] User notification requested for auth_req_id={AuthReqId}, user_hint={UserHint}. " +
            "Configure a real notification service for production use.",
            request.AuthReqId, request.UserIdentifierHint);

        // A real implementation would:
        // 1. Look up the user by login_hint (email, phone, etc.)
        // 2. Send push notification / SMS / email with a link to the consent page
        // 3. The link would be something like: https://idp.example.com/ciba/consent?auth_req_id={authReqId}

        return Task.CompletedTask;
    }

    public async Task SendPingNotificationAsync(CibaAuthenticationRequest request, CancellationToken cancellationToken = default)
    {
        // For ping mode, we need to POST to the client's backchannel_client_notification_endpoint
        // with the client_notification_token they provided

        if (string.IsNullOrEmpty(request.ClientNotificationToken))
        {
            logger.LogWarning("[CIBA] Cannot send ping notification - no client_notification_token for auth_req_id={AuthReqId}",
                request.AuthReqId);
            return;
        }

        logger.LogInformation("[CIBA] Ping notification would be sent for auth_req_id={AuthReqId}. " +
            "Configure client's backchannel_client_notification_endpoint for production use.",
            request.AuthReqId);

        // A real implementation would:
        // 1. Look up the client's backchannel_client_notification_endpoint
        // 2. POST to that endpoint with:
        //    - Authorization: Bearer {client_notification_token}
        //    - Body: { "auth_req_id": "..." }

        await Task.CompletedTask;
    }
}
