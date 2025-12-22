using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface ITenantsClaimService
{
    Task<string> BuildTenantsClaimJsonAsync(Guid userId, CancellationToken ct = default);
}

internal sealed class TenantsClaimService(AuthDbContext db) : ITenantsClaimService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> BuildTenantsClaimJsonAsync(Guid userId, CancellationToken ct = default)
    {
        var userEmail = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.NormalizedEmail ?? u.Email)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var normalizedEmail = EmailNormalizer.NormalizeForLookup(userEmail);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return "[]";
        }

        var userTenantRows = await (
            from u in db.Users.AsNoTracking()
            where u.NormalizedEmail == normalizedEmail
            join t in db.Tenants.AsNoTracking() on u.TenantId equals t.Id
            where t.Status == TenantStatus.Active
            select new
            {
                UserId = u.Id,
                TenantId = t.Id,
                TenantName = t.Name,
                TenantSlug = t.Slug,
                IssuerUri = t.IssuerUri
            }
        ).ToListAsync(ct).ConfigureAwait(false);

        if (userTenantRows.Count == 0)
        {
            return "[]";
        }

        var userIds = userTenantRows.Select(x => x.UserId).Distinct().ToArray();

        var roleAssignments = await (
            from assignment in db.UserRoleAssignments.AsNoTracking()
            where assignment.IsActive && userIds.Contains(assignment.UserId)
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            where role.IsActive
            select new { assignment.UserId, role.Name }
        ).ToListAsync(ct).ConfigureAwait(false);

        var rolesByUser = roleAssignments.ToLookup(x => x.UserId, x => x.Name);

        var tenants = userTenantRows
            .GroupBy(x => new { x.TenantId, x.TenantName, x.TenantSlug, x.IssuerUri })
            .Select(g =>
            {
                var roleNames = g.SelectMany(entry => rolesByUser[entry.UserId])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var hasAdminAccess = roleNames.Any(role =>
                    string.Equals(role, "platform-admin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, "tenant-admin", StringComparison.OrdinalIgnoreCase));

                return new
                {
                    tenant_id = g.Key.TenantId,
                    tenant_name = g.Key.TenantName,
                    tenant_slug = g.Key.TenantSlug,
                    issuer = g.Key.IssuerUri,
                    is_admin = hasAdminAccess
                };
            })
            .OrderBy(t => t.tenant_name)
            .ToArray();

        return JsonSerializer.Serialize(tenants, JsonOptions);
    }
}
