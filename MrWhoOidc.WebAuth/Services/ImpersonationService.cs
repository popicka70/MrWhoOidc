using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Service for platform admin impersonation of tenant admin role.
/// Allows platform admins to view the system as a tenant admin for troubleshooting.
/// </summary>
public interface IImpersonationService
{
    /// <summary>
    /// Start impersonating a tenant admin for the given tenant.
    /// Only platform admins can impersonate.
    /// </summary>
    Task<bool> StartImpersonationAsync(HttpContext context, ClaimsPrincipal user, Guid tenantId);

    /// <summary>
    /// Stop impersonating and return to platform admin view.
    /// </summary>
    Task StopImpersonationAsync(HttpContext context);

    /// <summary>
    /// Get the currently impersonated tenant ID, if any.
    /// </summary>
    Guid? GetImpersonatedTenantId(HttpContext context);

    /// <summary>
    /// Check if the current user is impersonating a tenant admin.
    /// </summary>
    bool IsImpersonating(HttpContext context);

    /// <summary>
    /// Get impersonation details for display (tenant name, slug, etc.)
    /// </summary>
    Task<ImpersonationInfo?> GetImpersonationInfoAsync(HttpContext context);
}

public class ImpersonationService(
    AuthDbContext db,
    IAuthorizationService authorizationService,
    ILogger<ImpersonationService> logger) : IImpersonationService
{
    private const string ImpersonationTenantIdKey = "ImpersonatingTenantId";
    private const string ImpersonationStartTimeKey = "ImpersonationStartTime";
    private const string ImpersonationStartLogIdKey = "ImpersonationStartLogId";

    public async Task<bool> StartImpersonationAsync(HttpContext context, ClaimsPrincipal user, Guid tenantId)
    {
        // Verify user is platform admin
        var isPlatformAdmin = (await authorizationService.AuthorizeAsync(user, "platform-admin")).Succeeded;
        if (!isPlatformAdmin)
        {
            return false;
        }

        // Get platform admin user ID and username
        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var username = user.Identity?.Name ?? "unknown";

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var platformAdminUserId))
        {
            logger.LogWarning("Cannot start impersonation: invalid user ID claim");
            return false;
        }

        // Verify tenant exists and is active
        var tenant = await db.Tenants
            .Where(t => t.Id == tenantId && t.Status == TenantStatus.Active)
            .FirstOrDefaultAsync();

        if (tenant == null)
        {
            logger.LogWarning("Cannot start impersonation: tenant {TenantId} not found or inactive", tenantId);
            return false;
        }

        // Create audit log entry
        var auditLog = new ImpersonationAuditLog
        {
            Id = Guid.NewGuid(),
            PlatformAdminUserId = platformAdminUserId,
            PlatformAdminUsername = username,
            TenantId = tenantId,
            TenantName = tenant.Name,
            TenantSlug = tenant.Slug,
            Action = ImpersonationAction.Start,
            Timestamp = DateTimeOffset.UtcNow,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString()
        };

        db.ImpersonationAuditLogs.Add(auditLog);
        await db.SaveChangesAsync();

        // Store impersonation in session
        context.Session.SetString(ImpersonationTenantIdKey, tenantId.ToString());
        context.Session.SetString(ImpersonationStartTimeKey, DateTimeOffset.UtcNow.ToString("O"));
        context.Session.SetString(ImpersonationStartLogIdKey, auditLog.Id.ToString());

        logger.LogInformation(
            "Platform admin {Username} (ID: {UserId}) started impersonating tenant {TenantName} (ID: {TenantId})",
            username, platformAdminUserId, tenant.Name, tenantId);

        return true;
    }

    public async Task StopImpersonationAsync(HttpContext context)
    {
        // Get impersonation details before clearing session
        var tenantId = GetImpersonatedTenantId(context);
        var startLogIdStr = context.Session.GetString(ImpersonationStartLogIdKey);
        var startTimeStr = context.Session.GetString(ImpersonationStartTimeKey);

        if (tenantId.HasValue && Guid.TryParse(startLogIdStr, out var startLogId))
        {
            // Find the start log entry
            var startLog = await db.ImpersonationAuditLogs
                .Where(l => l.Id == startLogId)
                .FirstOrDefaultAsync();

            if (startLog != null)
            {
                // Calculate duration
                TimeSpan? duration = null;
                if (DateTimeOffset.TryParse(startTimeStr, out var startTime))
                {
                    duration = DateTimeOffset.UtcNow - startTime;
                }

                // Create stop audit log entry
                var stopLog = new ImpersonationAuditLog
                {
                    Id = Guid.NewGuid(),
                    PlatformAdminUserId = startLog.PlatformAdminUserId,
                    PlatformAdminUsername = startLog.PlatformAdminUsername,
                    TenantId = startLog.TenantId,
                    TenantName = startLog.TenantName,
                    TenantSlug = startLog.TenantSlug,
                    Action = ImpersonationAction.Stop,
                    Timestamp = DateTimeOffset.UtcNow,
                    IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = context.Request.Headers.UserAgent.ToString(),
                    StartLogId = startLogId,
                    Duration = duration
                };

                db.ImpersonationAuditLogs.Add(stopLog);
                await db.SaveChangesAsync();

                logger.LogInformation(
                    "Platform admin {Username} (ID: {UserId}) stopped impersonating tenant {TenantName} (ID: {TenantId}) after {Duration}",
                    startLog.PlatformAdminUsername, startLog.PlatformAdminUserId, startLog.TenantName, startLog.TenantId,
                    duration?.ToString(@"hh\:mm\:ss") ?? "unknown");
            }
        }

        // Clear session
        context.Session.Remove(ImpersonationTenantIdKey);
        context.Session.Remove(ImpersonationStartTimeKey);
        context.Session.Remove(ImpersonationStartLogIdKey);
    }

    public Guid? GetImpersonatedTenantId(HttpContext context)
    {
        var tenantIdStr = context.Session.GetString(ImpersonationTenantIdKey);
        if (string.IsNullOrEmpty(tenantIdStr))
        {
            return null;
        }

        return Guid.TryParse(tenantIdStr, out var tenantId) ? tenantId : null;
    }

    public bool IsImpersonating(HttpContext context)
    {
        return GetImpersonatedTenantId(context) != null;
    }

    public async Task<ImpersonationInfo?> GetImpersonationInfoAsync(HttpContext context)
    {
        var tenantId = GetImpersonatedTenantId(context);
        if (tenantId == null)
        {
            return null;
        }

        var tenant = await db.Tenants
            .Where(t => t.Id == tenantId.Value)
            .Select(t => new ImpersonationInfo
            {
                TenantId = t.Id,
                TenantName = t.Name,
                TenantSlug = t.Slug,
                IssuerUri = t.IssuerUri,
                StartTime = DateTimeOffset.Parse(context.Session.GetString(ImpersonationStartTimeKey) ?? DateTimeOffset.UtcNow.ToString("O"))
            })
            .FirstOrDefaultAsync();

        return tenant;
    }
}

/// <summary>
/// Information about the current impersonation session.
/// </summary>
public class ImpersonationInfo
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string TenantSlug { get; set; } = string.Empty;
    public string IssuerUri { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }

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
}
