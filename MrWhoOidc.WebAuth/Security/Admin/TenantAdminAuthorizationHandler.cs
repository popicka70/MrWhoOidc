using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.SupportAccess;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Authorization handler for tenant admin access.
/// Checks if the user has the tenant-admin role in the current tenant's default realm.
/// Platform admins must use support access to access tenant admin functions.
/// Supports per-endpoint operation kind enforcement for read-only support access.
/// Handles both TenantAdminRequirement (policy-level) and
/// TenantAdminOperationRequirement (per-endpoint operation kind).
/// </summary>
public sealed class TenantAdminAuthorizationHandler : AuthorizationHandler<IAuthorizationRequirement>
{
    private readonly AuthDbContext _db;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantSwitchingService _tenantSwitchingService;
    private readonly IOptions<TenantAdminAuthOptions> _options;
    private readonly IOptions<PlatformAdminAuthOptions> _platformAdminOptions;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<TenantAdminAuthorizationHandler> _logger;
    private readonly IDefaultTenantContext _defaultTenantContext;
    private readonly ITenantSupportAccessStore _supportAccessStore;
    private readonly IAuditSink _audit;
    private readonly ITenantSupportAccessMetrics _metrics;

    public TenantAdminAuthorizationHandler(
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        ITenantSwitchingService tenantSwitchingService,
        IOptions<TenantAdminAuthOptions> options,
        IOptions<PlatformAdminAuthOptions> platformAdminOptions,
        IHttpContextAccessor httpContextAccessor,
        ILogger<TenantAdminAuthorizationHandler> logger,
        IDefaultTenantContext defaultTenantContext,
        ITenantSupportAccessStore supportAccessStore,
        IAuditSink audit,
        ITenantSupportAccessMetrics metrics)
    {
        _db = db;
        _tenantAccessor = tenantAccessor;
        _tenantSwitchingService = tenantSwitchingService;
        _options = options;
        _platformAdminOptions = platformAdminOptions;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _defaultTenantContext = defaultTenantContext;
        _supportAccessStore = supportAccessStore;
        _audit = audit;
        _metrics = metrics;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IAuthorizationRequirement requirement)
    {
        // Get user ID from claims
        var sub = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId))
        {
            _logger.LogDebug("[TenantAdminAuth] No valid user ID claim found");
            return;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        var requestPath = httpContext?.Request.Path.Value;
        _logger.LogInformation("[TenantAdminAuth] Evaluating user {UserId} for path {Path}", userId, requestPath);

        var operationKind = ResolveOperationKind(requirement, httpContext);
        _logger.LogDebug("[TenantAdminAuth] Operation requirement: {Kind}", operationKind);

        // Helper: Get effective tenant ID (middleware-resolved or session fallback)
        Guid? GetEffectiveTenantId()
        {
            var tid = _tenantAccessor.CurrentTenant?.TenantId;
            _logger.LogInformation("[TenantAdminAuth] Middleware tenant: {MiddlewareTenant}", tid?.ToString() ?? "(null)");

            if (tid == null && httpContext != null)
            {
                var sessionAvailable = httpContext.Session != null;
                var hasSessionCookie = httpContext.Request.Cookies.ContainsKey("__Host-mrwhooidc-session");
                _logger.LogInformation("[TenantAdminAuth] Session available: {SessionAvailable}, HasSessionCookie: {HasCookie}", sessionAvailable, hasSessionCookie);

                tid = _tenantSwitchingService.GetPreferredTenantId(httpContext);
                _logger.LogInformation("[TenantAdminAuth] Session tenant: {SessionTenant}", tid?.ToString() ?? "(null)");
            }
            return tid;
        }

        // Check for an active support session
        var supportAccessSessionIdStr = httpContext?.Session != null
            ? httpContext.Session.GetString("SupportAccessSessionId")
            : null;

        if (!string.IsNullOrEmpty(supportAccessSessionIdStr) && Guid.TryParse(supportAccessSessionIdStr, out var sessionId))
        {
            // Load the durable session and verify tenant association
            var currentTenantId = GetEffectiveTenantId();
            _logger.LogDebug("[TenantAdminAuth] Support access check - SessionId: {SessionId}, CurrentTenant: {Current}", sessionId, currentTenantId);

            var session = await _supportAccessStore.GetByIdAsync(sessionId, currentTenantId ?? Guid.Empty)
                .ConfigureAwait(false);

            if (session is null)
            {
                var validationPayload = new
                {
                    session_id = sessionId.ToString(),
                    actor_id = userId.ToString(),
                    tenant_id = currentTenantId?.ToString() ?? "(unknown)",
                    reason = "session_not_found",
                    path = requestPath ?? "(unknown)"
                };
                _audit.Emit("tenant_support_access.validation_failed", validationPayload);
                _metrics.TenantSupportAccessValidationFailures.Add(1, new KeyValuePair<string, object?>("reason", "session_not_found"));
                _logger.LogWarning("[TenantAdminAuth] DENIED - Support access session {SessionId} not found for tenant {CurrentTenantId}", sessionId, currentTenantId);
                httpContext?.Session?.Remove("SupportAccessSessionId");
                return;
            }

            if (session.PlatformAdminUserAccountId != userId)
            {
                _audit.Emit("tenant_support_access.validation_failed", new
                {
                    session_id = sessionId.ToString(),
                    actor_id = userId.ToString(),
                    tenant_id = currentTenantId?.ToString() ?? "(unknown)",
                    reason = "actor_mismatch",
                    path = requestPath ?? "(unknown)"
                });
                _metrics.TenantSupportAccessValidationFailures.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "actor_mismatch"));
                _logger.LogWarning(
                    "[TenantAdminAuth] DENIED - Support session {SessionId} belongs to a different actor",
                    sessionId);
                httpContext?.Session?.Remove("SupportAccessSessionId");
                return;
            }

            // Re-verify that the actor still possesses the platform-admin role
            // Perform a DB query against UserRealmRoleAssignments for the platform realm
            var platformRealmName = _platformAdminOptions.Value.RealmName;
            var platformAdminRoleName = _platformAdminOptions.Value.PlatformAdminRoleName;
            var platformTenantId = await _defaultTenantContext.GetDefaultTenantIdAsync()
                .ConfigureAwait(false);

            if (platformTenantId is null)
            {
                _logger.LogWarning("[TenantAdminAuth] DENIED - Cannot determine platform tenant ID");
                httpContext?.Session?.Remove("SupportAccessSessionId");
                return;
            }

            var hasPlatformAdminRole = await _db.UserRealmRoleAssignments.AsNoTracking()
                .Join(_db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
                .Join(_db.Realms, ar => ar.r.RealmId, rl => rl.Id, (ar, rl) => new { ar.a, ar.r, rl })
                .AnyAsync(x => x.a.UserId == userId
                            && x.a.IsActive
                            && x.a.RealmId == x.rl.Id
                            && x.r.IsActive
                            && x.r.Name == platformAdminRoleName
                            && x.r.TenantId == platformTenantId.Value
                            && x.rl.TenantId == platformTenantId.Value
                            && x.rl.Name == platformRealmName);

            if (!hasPlatformAdminRole)
            {
                var validationPayload = new
                {
                    session_id = sessionId.ToString(),
                    actor_id = userId.ToString(),
                    tenant_id = currentTenantId?.ToString() ?? "(unknown)",
                    reason = "platform_admin_role_missing",
                    path = requestPath ?? "(unknown)"
                };
                _audit.Emit("tenant_support_access.validation_failed", validationPayload);
                _metrics.TenantSupportAccessValidationFailures.Add(1, new KeyValuePair<string, object?>("reason", "platform_admin_role_missing"));
                _logger.LogWarning("[TenantAdminAuth] DENIED - Actor no longer has platform-admin role");
                httpContext?.Session?.Remove("SupportAccessSessionId");
                return;
            }

            // Verify target tenant is active
            var tenant = await _db.Tenants
                .FirstOrDefaultAsync(t => t.Id == currentTenantId)
                .ConfigureAwait(false);

            if (tenant is null || tenant.Status != TenantStatus.Active)
            {
                var validationPayload = new
                {
                    session_id = sessionId.ToString(),
                    actor_id = userId.ToString(),
                    tenant_id = currentTenantId?.ToString() ?? "(unknown)",
                    reason = "tenant_inactive",
                    path = requestPath ?? "(unknown)"
                };
                _audit.Emit("tenant_support_access.validation_failed", validationPayload);
                _metrics.TenantSupportAccessValidationFailures.Add(1, new KeyValuePair<string, object?>("reason", "tenant_inactive"));
                _logger.LogWarning("[TenantAdminAuth] DENIED - Target tenant {TenantId} not found or inactive", currentTenantId);
                httpContext?.Session?.Remove("SupportAccessSessionId");
                return;
            }

            // Check session status and expiration
            if (session.Status != SupportAccessStatus.Active)
            {
                var validationPayload = new
                {
                    session_id = sessionId.ToString(),
                    actor_id = userId.ToString(),
                    tenant_id = currentTenantId?.ToString() ?? "(unknown)",
                    reason = "session_not_active",
                    status = session.Status.ToString(),
                    path = requestPath ?? "(unknown)"
                };
                _audit.Emit("tenant_support_access.validation_failed", validationPayload);
                _metrics.TenantSupportAccessValidationFailures.Add(1, new KeyValuePair<string, object?>("reason", "session_not_active"));
                _logger.LogWarning("[TenantAdminAuth] DENIED - Support session {SessionId} is not active (status: {Status})",
                    sessionId, session.Status);
                httpContext?.Session?.Remove("SupportAccessSessionId");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (session.ExpiresAt <= now)
            {
                var validationPayload = new
                {
                    session_id = sessionId.ToString(),
                    actor_id = userId.ToString(),
                    tenant_id = currentTenantId?.ToString() ?? "(unknown)",
                    reason = "session_expired",
                    expires_at = session.ExpiresAt.ToUniversalTime().ToString("O"),
                    path = requestPath ?? "(unknown)"
                };
                _audit.Emit("tenant_support_access.validation_failed", validationPayload);
                _metrics.TenantSupportAccessValidationFailures.Add(1, new KeyValuePair<string, object?>("reason", "session_expired"));
                _logger.LogWarning("[TenantAdminAuth] DENIED - Support session {SessionId} has expired", sessionId);
                httpContext?.Session?.Remove("SupportAccessSessionId");
                return;
            }

            // Check if the operation kind is allowed by the session mode
            // If ReadOnly, deny any Write or SecuritySensitiveWrite
            if (session.Mode == SupportAccessMode.ReadOnly)
            {
                // ReadOnly mode - only Read operations are allowed
                if (operationKind == TenantAdminOperationKind.Write
                    || operationKind == TenantAdminOperationKind.SecuritySensitiveWrite)
                {
                    _logger.LogWarning("[TenantAdminAuth] DENIED - ReadOnly support session cannot perform {Kind} operation",
                        operationKind);
                    var deniedPayload = new
                    {
                        session_id = sessionId.ToString(),
                        actor_id = userId.ToString(),
                        tenant_id = currentTenantId?.ToString() ?? "(unknown)",
                        operation_kind = operationKind.ToString(),
                        reason = "write_denied_readonly",
                        path = requestPath ?? "(unknown)"
                    };
                    _audit.Emit("tenant_support_access.write_denied", deniedPayload);
                    _metrics.TenantSupportAccessWriteDenials.Add(1, new KeyValuePair<string, object?>("operation_kind", operationKind.ToString()));
                    return;
                }
            }

            // All checks passed - grant access via support session
            var usedPayload = new
            {
                session_id = sessionId.ToString(),
                actor_id = userId.ToString(),
                tenant_id = currentTenantId?.ToString() ?? "(unknown)",
                operation_kind = operationKind?.ToString() ?? "none",
                path = requestPath ?? "(unknown)"
            };
            _audit.Emit("tenant_support_access.used", usedPayload);
            _metrics.TenantSupportAccessStops.Add(1, new KeyValuePair<string, object?>("tenant_id", currentTenantId?.ToString() ?? "(unknown)"));
            _logger.LogDebug("[TenantAdminAuth] GRANTED via support access for session {SessionId}", sessionId);
            context.Succeed(requirement);
            return;
        }

        // No support session active - fall back to normal tenant-admin role check
        // All operation kinds are granted for regular tenant admins
        var tenantId = GetEffectiveTenantId();

        if (tenantId == null)
        {
            _logger.LogWarning("[TenantAdminAuth] DENIED - No tenant context available for user {UserId}, path {Path}", userId, requestPath);
            return;
        }

        var realmName = _options.Value.RealmName;
        var roleName = _options.Value.TenantAdminRoleName;

        _logger.LogDebug("[TenantAdminAuth] Checking role {Role} in realm {Realm} for tenant {TenantId}", roleName, realmName, tenantId);

        var hasRole = await _db.UserRealmRoleAssignments.AsNoTracking()
            .Join(_db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(_db.Realms, ar => ar.r.RealmId, rl => rl.Id, (ar, rl) => new { ar.a, ar.r, rl })
            .AnyAsync(x => x.a.UserId == userId
                            && x.a.IsActive
                            && x.r.IsActive
                            && x.r.Name == roleName
                            && x.rl.TenantId == tenantId
                            && x.rl.Name == realmName);

        if (hasRole)
        {
            _logger.LogDebug("[TenantAdminAuth] GRANTED via role assignment for user {UserId}", userId);
            context.Succeed(requirement);
        }
        else
        {
            _logger.LogDebug("[TenantAdminAuth] DENIED - user {UserId} lacks role {Role} in tenant {TenantId}", userId, roleName, tenantId);
        }
    }

    internal static TenantAdminOperationKind? ResolveOperationKind(
        IAuthorizationRequirement requirement,
        HttpContext? httpContext)
    {
        if (requirement is TenantAdminOperationRequirement operationRequirement)
        {
            return operationRequirement.Kind;
        }

        if (requirement is not TenantAdminRequirement)
        {
            return null;
        }

        if (httpContext is null)
        {
            return TenantAdminOperationKind.Write;
        }

        var method = httpContext.Request.Method;
        return HttpMethods.IsGet(method)
            || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method)
                ? TenantAdminOperationKind.Read
                : TenantAdminOperationKind.Write;
    }
}
