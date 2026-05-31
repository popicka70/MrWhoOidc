using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Authorization handler for tenant admin access.
/// Checks if the user has the tenant-admin role in the current tenant's default realm.
/// Platform admins must use impersonation to access tenant admin functions.
/// </summary>
public sealed class TenantAdminAuthorizationHandler : AuthorizationHandler<TenantAdminRequirement>
{
    private readonly AuthDbContext _db;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantSwitchingService _tenantSwitchingService;
    private readonly IOptions<TenantAdminAuthOptions> _options;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<TenantAdminAuthorizationHandler> _logger;

    public TenantAdminAuthorizationHandler(
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        ITenantSwitchingService tenantSwitchingService,
        IOptions<TenantAdminAuthOptions> options,
        IHttpContextAccessor httpContextAccessor,
        ILogger<TenantAdminAuthorizationHandler> logger)
    {
        _db = db;
        _tenantAccessor = tenantAccessor;
        _tenantSwitchingService = tenantSwitchingService;
        _options = options;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantAdminRequirement requirement)
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

        // Check if platform admin is impersonating this tenant
        // Access session directly to avoid circular dependency with IImpersonationService
        if (httpContext?.Session != null)
        {
            var impersonatedTenantIdStr = httpContext.Session.GetString("ImpersonatingTenantId");
            if (!string.IsNullOrEmpty(impersonatedTenantIdStr) && Guid.TryParse(impersonatedTenantIdStr, out var impersonatedTenantId))
            {
                var currentTenantId = GetEffectiveTenantId();
                _logger.LogDebug("[TenantAdminAuth] Impersonation check - Impersonating: {Impersonating}, Current: {Current}", impersonatedTenantId, currentTenantId);

                if (impersonatedTenantId == currentTenantId)
                {
                    // User is a platform admin impersonating this tenant - grant access
                    _logger.LogDebug("[TenantAdminAuth] GRANTED via impersonation");
                    context.Succeed(requirement);
                    return;
                }
            }
        }

        // Get current tenant context - try middleware first, then session fallback
        // This ensures authorization works even on pages that skip tenant resolution (e.g., /platform-admin/*)
        var tenantId = GetEffectiveTenantId();

        if (tenantId == null)
        {
            // No tenant context - cannot proceed
            // This can happen if middleware hasn't run yet or tenant resolution failed
            _logger.LogWarning("[TenantAdminAuth] DENIED - No tenant context available for user {UserId}, path {Path}", userId, requestPath);
            return;
        }

        // Check if user has tenant-admin role in current tenant's default realm (realm-scoped)
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
}
