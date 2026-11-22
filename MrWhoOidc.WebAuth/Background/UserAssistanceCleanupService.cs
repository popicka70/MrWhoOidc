using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Assistance;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Background;

/// <summary>
/// Expires overdue assistance requests and sessions, revoking target OP sessions and their tokens.
/// <para>
/// The request path also detects expiry and fails closed. This worker makes the durable state converge when
/// the requester, helper or manager is not using the browser anymore. Pending requests lose their one-time
/// invitation token; active sessions revoke the target OP session and every token issued for it.
/// </para>
/// </summary>
public sealed class UserAssistanceCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<UserAssistanceCleanupService> _logger;

    public UserAssistanceCleanupService(
        IServiceProvider serviceProvider,
        ILogger<UserAssistanceCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "User assistance cleanup failed; it will retry");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IUserAssistanceStore>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditSink>();
        var metrics = scope.ServiceProvider.GetRequiredService<IUserAssistanceMetrics>();

        var now = DateTimeOffset.UtcNow;
        var expiredIds = await db.UserAssistanceSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => (s.Status == UserAssistanceStatus.Pending && s.ExpiresAt <= now)
                        || (s.Status == UserAssistanceStatus.Active
                            && s.SessionExpiresAt != null
                            && s.SessionExpiresAt <= now))
            .Select(s => s.Id)
            .Take(500)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var sessionId in expiredIds)
        {
            var session = await store.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
            if (session is null
                || session.Status is not (UserAssistanceStatus.Pending or UserAssistanceStatus.Active))
            {
                continue;
            }

            var ended = await store.EndAsync(
                session.Id,
                UserAssistanceStatus.Expired,
                now,
                null,
                session.Status == UserAssistanceStatus.Pending
                    ? "Request expired"
                    : "Session expired",
                ct).ConfigureAwait(false);
            if (!ended)
            {
                continue;
            }

            metrics.Expirations.Add(1, new KeyValuePair<string, object?>("tenant_id", session.TenantId.ToString()));
            if (session.Status == UserAssistanceStatus.Active && session.AcceptedAt is { } acceptedAt)
            {
                metrics.SessionDuration.Record(
                    (now - acceptedAt).TotalMilliseconds,
                    new KeyValuePair<string, object?>("end_reason", "expired"));
            }

            audit.Emit("user_assistance.expired", new
            {
                request_id = session.Id.ToString(),
                session_id = session.Id.ToString(),
                requester_id = session.RequesterUserId.ToString(),
                helper_id = session.HelperUserId.ToString(),
                tenant_id = session.TenantId.ToString(),
                mode = session.Mode.ToString(),
                was_active = session.Status == UserAssistanceStatus.Active,
                expired_at = now.ToUniversalTime().ToString("O")
            });

            _logger.LogInformation(
                "Expired assistance {Kind} {SessionId} for requester {Requester} and helper {Helper}",
                session.Status == UserAssistanceStatus.Active ? "session" : "request",
                session.Id,
                session.RequesterUsername,
                session.HelperUsername);
        }
    }
}
