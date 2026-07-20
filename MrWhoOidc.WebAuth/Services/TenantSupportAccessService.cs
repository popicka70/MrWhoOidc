using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.SupportAccess;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Service for platform admin tenant support access.
/// Uses durable TenantSupportAccessSession model persisted via ITenantSupportAccessStore.
/// </summary>
public interface ITenantSupportAccessService
{
    /// <summary>
    /// Start support access for the given tenant.
    /// Only platform admins can start support access.
    /// </summary>
    Task<bool> StartSupportAccessAsync(HttpContext context, ClaimsPrincipal user, Guid tenantId, string reason, int? expiryMinutes = null, string? ticketReference = null);

    /// <summary>
    /// Stop support access and return to platform admin view.
    /// </summary>
    Task StopSupportAccessAsync(HttpContext context);

    /// <summary>
    /// Get the currently support-accessed tenant ID, if any.
    /// </summary>
    Task<Guid?> GetSupportAccessTenantIdAsync(HttpContext context);

    /// <summary>
    /// Check if the current user has active support access.
    /// </summary>
    Task<bool> IsSupportAccessActiveAsync(HttpContext context);

    /// <summary>
    /// Get support access details for display (tenant name, slug, reason, expiry, etc.)
    /// </summary>
    Task<TenantSupportAccessInfo?> GetSupportAccessInfoAsync(HttpContext context);
}

public class TenantSupportAccessService(
    AuthDbContext db,
    IAuthorizationService authorizationService,
    ITenantSupportAccessStore store,
    ILogger<TenantSupportAccessService> logger,
    IAuditSink audit,
    ITenantSupportAccessMetrics metrics,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions) : ITenantSupportAccessService
{
    private const string SupportAccessSessionIdKey = "SupportAccessSessionId";

    private const int DefaultExpiryMinutes = 15;

    public async Task<bool> StartSupportAccessAsync(HttpContext context, ClaimsPrincipal user, Guid tenantId, string reason, int? expiryMinutes = null, string? ticketReference = null)
    {
        // Check feature flag: Tenant Support Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableTenantSupportAccess)
        {
            logger.LogWarning("TenantSupportAccess is disabled via EnableTenantSupportAccess flag.");
            return false;
        }

        // Verify user is platform admin
        var isPlatformAdmin = (await authorizationService.AuthorizeAsync(user, "platform-admin")).Succeeded;
        if (!isPlatformAdmin)
        {
            return false;
        }

        // Get platform admin user ID
        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var platformAdminUserId))
        {
            logger.LogWarning("Cannot start support access: invalid user ID claim");
            return false;
        }

        // Verify tenant exists and is active
        var tenant = await db.Tenants
            .Where(t => t.Id == tenantId && t.Status == TenantStatus.Active)
            .FirstOrDefaultAsync();

        if (tenant == null)
        {
            logger.LogWarning("Cannot start support access: tenant {TenantId} not found or inactive", tenantId);
            return false;
        }

        // Validate reason
        if (string.IsNullOrWhiteSpace(reason))
        {
            logger.LogWarning("Cannot start support access: reason is required");
            return false;
        }

        // Calculate expiry time
        var resolvedExpiryMinutes = expiryMinutes ?? DefaultExpiryMinutes;
        resolvedExpiryMinutes = Math.Clamp(resolvedExpiryMinutes, 1, 60);
        var expiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(resolvedExpiryMinutes);

        // Create durable session entity
        var session = new TenantSupportAccessSession
        {
            Id = GuidHelper.NewId(),
            PlatformAdminUserAccountId = platformAdminUserId,
            TenantId = tenantId,
            Mode = SupportAccessMode.ReadOnly,
            Status = SupportAccessStatus.Active,
            Reason = reason,
            TicketReference = ticketReference,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt
        };

        // Persist via store
        await store.CreateAsync(session);

        // Emit audit event
        var auditPayload = new
        {
            session_id = session.Id.ToString(),
            actor_id = platformAdminUserId.ToString(),
            tenant_id = tenantId.ToString(),
            reason = reason,
            ticket_reference = ticketReference ?? null,
            mode = "ReadOnly",
            expires_at = expiresAt.ToUniversalTime().ToString("O")
        };
        audit.Emit("tenant_support_access.started", auditPayload);

        // Record metrics
        metrics.TenantSupportAccessStarts.Add(1, new KeyValuePair<string, object?>("tenant_id", tenantId.ToString()));

        // Store only the session ID in ASP.NET session
        context.Session.SetString(SupportAccessSessionIdKey, session.Id.ToString());

        logger.LogInformation(
            "Platform admin {UserId} started support access for tenant {TenantId} (reason: {Reason}, session: {SessionId})",
            platformAdminUserId, tenantId, reason, session.Id);

        return true;
    }

    public async Task StopSupportAccessAsync(HttpContext context)
    {
        // Retrieve session ID from session
        var sessionIdStr = context.Session.GetString(SupportAccessSessionIdKey);
        if (string.IsNullOrEmpty(sessionIdStr))
        {
            return;
        }

        if (!Guid.TryParse(sessionIdStr, out var sessionId))
        {
            return;
        }

        // Load the durable session directly from DB (we don't know tenant yet for store lookup)
        var existingSession = await db.TenantSupportAccessSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (existingSession == null)
        {
            // Session not found in store; just clear the session key
            context.Session.Remove(SupportAccessSessionIdKey);
            return;
    }

        // Update session status to Ended
        existingSession.Status = SupportAccessStatus.Ended;
        existingSession.EndedAt = DateTimeOffset.UtcNow;
        existingSession.ConcurrencyToken = GuidHelper.NewId();

        await store.UpdateAsync(existingSession);

        // Emit audit event
        var auditPayload = new
        {
            session_id = sessionId.ToString(),
            actor_id = existingSession.PlatformAdminUserAccountId.ToString(),
            tenant_id = existingSession.TenantId.ToString(),
            reason = existingSession.Reason,
            duration_minutes = (int)(DateTimeOffset.UtcNow - existingSession.CreatedAt).TotalMinutes
        };
        audit.Emit("tenant_support_access.ended", auditPayload);

        // Record metrics: stop count and session duration histogram
        metrics.TenantSupportAccessStops.Add(1, new KeyValuePair<string, object?>("tenant_id", existingSession.TenantId.ToString()));
        metrics.TenantSupportAccessSessionDuration.Record(
            (double)(DateTimeOffset.UtcNow - existingSession.CreatedAt).TotalMilliseconds,
            new KeyValuePair<string, object?>("reason", existingSession.Reason));

        logger.LogInformation(
            "Platform admin ended support access session {SessionId} for tenant {TenantId}",
            sessionId, existingSession.TenantId);

        // Clear session
        context.Session.Remove(SupportAccessSessionIdKey);
    }

    public async Task<Guid?> GetSupportAccessTenantIdAsync(HttpContext context)
    {
        var sessionIdStr = context.Session.GetString(SupportAccessSessionIdKey);
        if (string.IsNullOrEmpty(sessionIdStr))
        {
            return null;
        }

        var sessionId = Guid.TryParse(sessionIdStr, out var parsedId) ? parsedId : (Guid?)null;
        if (sessionId == null)
        {
            return null;
        }

        // Load the session directly from DB to get tenant ID
        var session = await db.TenantSupportAccessSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session == null)
        {
            return null;
        }

        return session.TenantId;
    }

    public async Task<bool> IsSupportAccessActiveAsync(HttpContext context)
    {
        var sessionIdStr = context.Session.GetString(SupportAccessSessionIdKey);
        if (string.IsNullOrEmpty(sessionIdStr))
        {
            return false;
        }

        var sessionId = Guid.TryParse(sessionIdStr, out var parsedId) ? parsedId : (Guid?)null;
        if (sessionId == null)
        {
            return false;
        }

        var session = await db.TenantSupportAccessSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        return session != null && session.Status == SupportAccessStatus.Active && session.ExpiresAt > DateTimeOffset.UtcNow;
    }

    public async Task<TenantSupportAccessInfo?> GetSupportAccessInfoAsync(HttpContext context)
    {
        var sessionIdStr = context.Session.GetString(SupportAccessSessionIdKey);
        if (string.IsNullOrEmpty(sessionIdStr))
        {
            return null;
        }

        var sessionId = Guid.TryParse(sessionIdStr, out var parsedId) ? parsedId : (Guid?)null;
        if (sessionId == null)
        {
            return null;
        }

        // Load the durable session
        var session = await db.TenantSupportAccessSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session == null)
        {
            return null;
        }

        // Load tenant info for display
        var tenant = await db.Tenants
            .Where(t => t.Id == session.TenantId)
            .Select(t => new
            {
                t.Name,
                t.Slug,
                t.IssuerUri
            })
            .FirstOrDefaultAsync();

        if (tenant == null)
        {
            return null;
        }

        // Map to TenantSupportAccessInfo
        return new TenantSupportAccessInfo
        {
            SessionId = session.Id,
            TenantId = session.TenantId,
            TenantName = tenant.Name,
            TenantSlug = tenant.Slug,
            IssuerUri = tenant.IssuerUri,
            StartTime = session.CreatedAt,
            ExpiresAt = session.ExpiresAt,
            Reason = session.Reason,
            TicketReference = session.TicketReference,
            Status = session.Status
        };
    }
}

/// <summary>
/// Information about the current support access session.
/// </summary>
public class TenantSupportAccessInfo
{
    public Guid SessionId { get; set; }
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string TenantSlug { get; set; } = string.Empty;
    public string IssuerUri { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? TicketReference { get; set; }
    public SupportAccessStatus Status { get; set; }

    public string Duration
    {
        get
        {
            var duration = DateTimeOffset.UtcNow - StartTime;
            if (duration.TotalMinutes < 1)
            {
                return "< 1 min";
            }
            else if (duration.TotalHours < 1)
            {
                return $"{(int)duration.TotalMinutes} min";
            }
            else
            {
                return $"{(int)duration.TotalHours}h {duration.Minutes}m";
            }
        }
    }

    public string RemainingTime
    {
        get
        {
            if (Status != SupportAccessStatus.Active)
            {
                return "Ended";
            }

            var remaining = ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining.TotalSeconds <= 0)
            {
                return "Expired";
            }

            var totalMinutes = (int)Math.Ceiling(remaining.TotalMinutes);
            var minutes = totalMinutes % 60;
            var hours = totalMinutes / 60;

            if (hours > 0)
            {
                return $"{hours}h {minutes}m";
            }
            else
            {
                return $"{minutes}m";
            }
        }
    }
}
