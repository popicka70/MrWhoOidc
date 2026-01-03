using System;
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
