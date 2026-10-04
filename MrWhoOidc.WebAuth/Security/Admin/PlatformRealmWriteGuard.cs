using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Platform admin is just a role in the default tenant's platform realm, so any write path that can create,
/// rename or assign roles there grants control of every tenant. Rather than guarding each of those paths
/// (admin pages, admin API, config import, seed manifests), this interceptor refuses every pending change to
/// the platform realm, its roles or its role assignments unless the caller passes the <c>platform-admin</c>
/// policy. Writes outside an HTTP request (startup seeding, background jobs) and requests explicitly marked
/// by <see cref="AllowSystemWrite"/> (token-authenticated /bootstrap) are allowed.
/// </summary>
public sealed class PlatformRealmWriteGuard(
    IHttpContextAccessor httpContextAccessor,
    IOptions<PlatformAdminAuthOptions> options) : SaveChangesInterceptor
{
    private static readonly object SystemWriteKey = new();

    /// <summary>Marks the current request as a trusted system write, e.g. after the bootstrap token was verified.</summary>
    public static void AllowSystemWrite(HttpContext http) => http.Items[SystemWriteKey] = true;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AuthDbContext db && TryGetCheckedRequest(db, out var http)
            && await TouchesPlatformRealmAsync(db, http, cancellationToken).ConfigureAwait(false))
        {
            var authorization = http.RequestServices.GetRequiredService<IAuthorizationService>();
            var decision = await authorization.AuthorizeAsync(http.User, "platform-admin").ConfigureAwait(false);
            if (!decision.Succeeded)
            {
                throw new PlatformRealmWriteDeniedException();
            }
        }

        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        // The check needs async queries and authorization; no sync caller writes realms or roles, so fail closed.
        if (eventData.Context is AuthDbContext db && TryGetCheckedRequest(db, out _))
        {
            throw new PlatformRealmWriteDeniedException();
        }

        return result;
    }

    /// <summary>True when there are pending realm/role/assignment changes inside an unmarked HTTP request.</summary>
    private bool TryGetCheckedRequest(AuthDbContext db, out HttpContext http)
    {
        http = httpContextAccessor.HttpContext!;
        return http is not null
               && !http.Items.ContainsKey(SystemWriteKey)
               && db.ChangeTracker.Entries().Any(e => IsPending(e) && e.Entity is Realm or Role or UserRealmRoleAssignment);
    }

    private async Task<bool> TouchesPlatformRealmAsync(AuthDbContext db, HttpContext http, CancellationToken ct)
    {
        var defaultTenantId = await http.RequestServices.GetRequiredService<IDefaultTenantContext>()
            .GetDefaultTenantIdAsync(ct).ConfigureAwait(false);
        if (defaultTenantId is null)
        {
            return false; // no default tenant yet, so no platform realm to protect
        }

        var realmName = options.Value.RealmName;
        var tenantId = defaultTenantId.Value;
        var pending = db.ChangeTracker.Entries().Where(IsPending).ToList();

        var platformRealmIds = (await db.Realms.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Name == realmName)
                .Select(r => r.Id)
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet();

        var touches = false;
        foreach (var entry in pending.Where(e => e.Entity is Realm))
        {
            var realm = (Realm)entry.Entity;
            // Covers creating it, renaming into it, renaming out of it and deleting it.
            if (realm.TenantId == tenantId
                && (realm.Name == realmName || Original<string>(entry, nameof(Realm.Name)) == realmName || platformRealmIds.Contains(realm.Id)))
            {
                platformRealmIds.Add(realm.Id);
                touches = true;
            }
        }

        var platformRoleIds = (await db.Roles.IgnoreQueryFilters().AsNoTracking()
                .Where(r => platformRealmIds.Contains(r.RealmId))
                .Select(r => r.Id)
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet();

        foreach (var entry in pending.Where(e => e.Entity is Role))
        {
            var role = (Role)entry.Entity;
            if (platformRealmIds.Contains(role.RealmId) || platformRealmIds.Contains(Original<Guid>(entry, nameof(Role.RealmId))))
            {
                platformRoleIds.Add(role.Id);
                touches = true;
            }
        }

        foreach (var entry in pending.Where(e => e.Entity is UserRealmRoleAssignment))
        {
            var assignment = (UserRealmRoleAssignment)entry.Entity;
            if (platformRealmIds.Contains(assignment.RealmId) || platformRoleIds.Contains(assignment.RoleId))
            {
                touches = true;
            }
        }

        return touches;
    }

    private static bool IsPending(EntityEntry entry) => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;

    private static T? Original<T>(EntityEntry entry, string property)
        => entry.State is EntityState.Modified or EntityState.Deleted ? entry.OriginalValues.GetValue<T>(property) : default;
}

public sealed class PlatformRealmWriteDeniedException()
    : InvalidOperationException("Only platform administrators may change the platform realm, its roles or their assignments.");
