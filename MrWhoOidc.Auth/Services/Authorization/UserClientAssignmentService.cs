using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Authorization;

public sealed class UserClientAssignmentService(
    AuthDbContext db,
    IClientStore clients,
    ILogger<UserClientAssignmentService> logger) : IUserClientAssignmentService
{
    public async Task<(bool assigned, string? error)> EnsureAssignedAsync(Guid userId, string clientId, string? idp, CancellationToken ct = default)
    {
        logger.LogInformation("🔍 Ensuring user {UserId} is assigned to client {ClientId}", userId, clientId);

        var client = await clients.FindByClientIdAsync(clientId, ct).ConfigureAwait(false);
        if (client == null)
        {
            logger.LogWarning("❌ Client not found: {ClientId}", clientId);
            return (false, "Unknown client");
        }

        var assigned = await db.UserClientAssignments.AsNoTracking()
            .AnyAsync(a => a.UserId == userId && a.ClientId == client.Id && a.RealmId == client.RealmId && a.IsActive, ct)
            .ConfigureAwait(false);

        if (assigned)
        {
            logger.LogInformation("✅ User {UserId} is already assigned to client {ClientId}", userId, clientId);
            return (true, null);
        }

        // Auto-approval logic
        var isExternalSession = !string.IsNullOrWhiteSpace(idp);
        var canAutoAssign = client.AutoApprovalMode == AutoApprovalMode.All ||
            (client.AutoApprovalMode == AutoApprovalMode.OnlyExternalIdp && isExternalSession);

        if (canAutoAssign)
        {
            var userTenantId = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.TenantId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (userTenantId != Guid.Empty && userTenantId == client.TenantId)
            {
                logger.LogInformation("📝 Auto-assigning user {UserId} to client {ClientId} (Mode={Mode}, External={IsExternal})",
                    userId, clientId, client.AutoApprovalMode, isExternalSession);

                var assignment = new UserClientAssignment
                {
                    UserId = userId,
                    ClientId = client.Id,
                    RealmId = client.RealmId,
                    IsActive = true
                };

                db.UserClientAssignments.Add(assignment);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                return (true, null);
            }
            else
            {
                logger.LogInformation("Authorize auto-assign skipped due to tenant mismatch or unknown user tenant. ClientId={ClientId}, UserId={UserId}, UserTenantId={UserTenantId}, ClientTenantId={ClientTenantId}",
                    clientId, userId, userTenantId, client.TenantId);
            }
        }

        logger.LogWarning("❌ User {UserId} is NOT assigned to client {ClientId} and auto-approval is not permitted", userId, clientId);
        return (false, "User is not assigned to this application");
    }
}
