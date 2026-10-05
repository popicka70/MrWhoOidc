using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services.SubjectIdentifiers;

public sealed class PairwiseSubjectService(
    AuthDbContext db,
    ISectorIdentifierResolver sectorIdentifierResolver,
    ILogger<PairwiseSubjectService> logger) : IPairwiseSubjectService
{
    public async Task<string> GetSubjectAsync(Client client, Guid userId, CancellationToken ct = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (userId == Guid.Empty) throw new ArgumentException("UserId must be a non-empty GUID", nameof(userId));

        if (!string.Equals(client.SubjectType, OidcConstants.SubjectTypes.Pairwise, StringComparison.Ordinal))
        {
            return userId.ToString();
        }

        var tenantId = client.TenantId;
        var sectorIdentifier = await sectorIdentifierResolver.ResolveSectorIdentifierAsync(client, ct).ConfigureAwait(false);

        var existing = await db.PairwiseSubjectIdentifiers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId && x.SectorIdentifier == sectorIdentifier, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing.Subject;
        }

        var subject = GenerateOpaqueSubject();

        db.PairwiseSubjectIdentifiers.Add(new PairwiseSubjectIdentifier
        {
            Id = GuidHelper.NewId(),
            TenantId = tenantId,
            UserId = userId,
            SectorIdentifier = sectorIdentifier,
            Subject = subject,
            CreatedAtUtc = DateTime.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Audit-style log: avoid logging raw identifiers or the subject itself.
            logger.LogInformation(
                "pairwise_subject.created tenant={TenantId} client_bucket={ClientBucket} user_bucket={UserBucket} sector_bucket={SectorBucket}",
                tenantId,
                Bucketization.BucketizeClientId(client.ClientId),
                Bucketization.Bucket(userId.ToString()),
                Bucketization.Bucket(sectorIdentifier));
            return subject;
        }
        catch (DbUpdateException)
        {
            var winner = await db.PairwiseSubjectIdentifiers
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId && x.SectorIdentifier == sectorIdentifier, ct)
                .ConfigureAwait(false);

            if (winner is not null)
            {
                return winner.Subject;
            }

            throw;
        }
    }

    public Task<Guid?> ResolveUserIdAsync(string? subject, CancellationToken ct = default)
        => ResolveUserIdAsync(db, subject, ct);

    /// <summary>
    /// Reverse lookup for subjects issued by <see cref="GetSubjectAsync"/>. Pairwise subjects are random
    /// (not derivable), so the persisted mapping is the source of truth; it is unique per tenant
    /// (UX_PairwiseSubjectIdentifiers_Tenant_Subject) and tenant-filtered by the context.
    /// </summary>
    public static async Task<Guid?> ResolveUserIdAsync(AuthDbContext db, string? subject, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        if (Guid.TryParse(subject, out var userId))
        {
            return userId == Guid.Empty ? null : userId;
        }

        return await db.PairwiseSubjectIdentifiers
            .AsNoTracking()
            .Where(x => x.Subject == subject)
            .Select(x => (Guid?)x.UserId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private static string GenerateOpaqueSubject()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
