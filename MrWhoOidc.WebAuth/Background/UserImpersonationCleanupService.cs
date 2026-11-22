using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.Impersonation;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Background;

/// <summary>
/// Marks expired impersonation sessions and revokes their target OP sessions and refresh tokens.
/// The live request path also detects expiry; this worker guarantees the durable state converges even when
/// the administrator closes the browser.
/// </summary>
public sealed class UserImpersonationCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<UserImpersonationCleanupService> _logger;

    public UserImpersonationCleanupService(
        IServiceProvider serviceProvider,
        ILogger<UserImpersonationCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
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
                _logger.LogWarning(ex, "User impersonation cleanup failed; it will retry");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IUserImpersonationStore>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditSink>();
        var metrics = scope.ServiceProvider.GetRequiredService<IUserImpersonationMetrics>();

        var now = DateTimeOffset.UtcNow;
        var expired = await db.UserImpersonationSessions
            .IgnoreQueryFilters()
            .Where(s => s.Status == UserImpersonationStatus.Active && s.ExpiresAt <= now)
            .Select(s => s.Id)
            .ToListAsync(ct);

        foreach (var sessionId in expired)
        {
            var session = await store.GetByIdAsync(sessionId, ct);
            if (session is null || session.Status != UserImpersonationStatus.Active)
            {
                continue;
            }

            var ended = await store.EndAsync(sessionId, UserImpersonationStatus.Expired, now, null, "expired", ct);
            if (!ended)
            {
                continue;
            }

            metrics.Expirations.Add(1, new KeyValuePair<string, object?>("tenant_id", session.TenantSlug));
            metrics.SessionDuration.Record(
                (now - session.CreatedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("end_reason", "expired"));

            audit.Emit("user_impersonation.expired", new
            {
                session_id = session.Id.ToString(),
                actor_account_id = session.ActorUserAccountId.ToString(),
                target_account_id = session.TargetUserAccountId.ToString(),
                tenant_id = session.TenantId.ToString(),
                expired_at = session.ExpiresAt.ToUniversalTime().ToString("O")
            });

            _logger.LogInformation(
                "Expired impersonation session {SessionId} for target {Target} in tenant {Tenant}",
                session.Id, session.TargetUsername, session.TenantSlug);
        }
    }
}
