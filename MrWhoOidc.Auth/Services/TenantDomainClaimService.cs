using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface ITenantDomainClaimService
{
    Task<TenantDomainClaimCreateResult> CreateClaimAsync(
        Guid tenantId,
        string domain,
        TenantDomainEnrollmentMode enrollmentMode,
        Guid? createdByUserId,
        string? createdByUsername,
        CancellationToken ct = default);

    Task<IReadOnlyList<TenantDomainClaimListItem>> ListClaimsAsync(Guid tenantId, CancellationToken ct = default);

    Task<TenantDomainEnrollmentMatch?> ResolveAutoJoinClaimAsync(string email, CancellationToken ct = default);

    Task<bool> RevokeClaimAsync(Guid tenantId, Guid claimId, Guid? revokedByUserId, string? reason, CancellationToken ct = default);

    Task<bool> MarkClaimVerifiedAsync(Guid claimId, CancellationToken ct = default);
}

public sealed record TenantDomainClaimCreateResult(TenantDomainClaim Claim);

public sealed record TenantDomainClaimListItem(
    Guid Id,
    string Domain,
    TenantDomainClaimStatus Status,
    TenantDomainEnrollmentMode EnrollmentMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? RevokedAt,
    string? CreatedByUsername);

public sealed record TenantDomainEnrollmentMatch(
    Guid ClaimId,
    Guid TenantId,
    string TenantSlug,
    string TenantName,
    string Domain,
    TenantDomainEnrollmentMode EnrollmentMode);

internal sealed partial class TenantDomainClaimService(
    AuthDbContext db,
    ILogger<TenantDomainClaimService> logger,
    IOptions<PublicEmailDomainOptions> publicEmailDomainOptions) : ITenantDomainClaimService
{
    private static readonly IdnMapping Idn = new();
    private readonly HashSet<string> _publicEmailDomains =
        publicEmailDomainOptions.Value.Domains;

    public async Task<TenantDomainClaimCreateResult> CreateClaimAsync(
        Guid tenantId,
        string domain,
        TenantDomainEnrollmentMode enrollmentMode,
        Guid? createdByUserId,
        string? createdByUsername,
        CancellationToken ct = default)
    {
        var normalizedDomain = NormalizeDomainForClaim(domain, _publicEmailDomains);

        if (enrollmentMode == TenantDomainEnrollmentMode.Disabled)
        {
            throw new ArgumentException("Choose an active enrollment mode for a new domain claim.", nameof(enrollmentMode));
        }

        var tenantExists = await db.Tenants.AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, ct)
            .ConfigureAwait(false);
        if (!tenantExists)
        {
            throw new InvalidOperationException("Tenant is not available for domain claims.");
        }

        var existing = await db.TenantDomainClaims.AsNoTracking()
            .Include(c => c.Tenant)
            .FirstOrDefaultAsync(c => c.NormalizedDomain == normalizedDomain && c.Status != TenantDomainClaimStatus.Revoked, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Domain '{normalizedDomain}' is already claimed by tenant '{existing.Tenant.Name}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var claim = new TenantDomainClaim
        {
            TenantId = tenantId,
            Domain = normalizedDomain,
            NormalizedDomain = normalizedDomain,
            Status = TenantDomainClaimStatus.PendingVerification,
            EnrollmentMode = enrollmentMode,
            CreatedByUserId = createdByUserId,
            CreatedByUsername = string.IsNullOrWhiteSpace(createdByUsername) ? null : createdByUsername.Trim(),
            CreatedAt = now
        };

        db.TenantDomainClaims.Add(claim);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Created domain claim {DomainClaimId} for tenant {TenantId} (pending verification)", claim.Id, tenantId);
        return new TenantDomainClaimCreateResult(claim);
    }

    public async Task<IReadOnlyList<TenantDomainClaimListItem>> ListClaimsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await db.TenantDomainClaims.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Status == TenantDomainClaimStatus.Revoked)
            .ThenBy(c => c.Domain)
            .Select(c => new TenantDomainClaimListItem(
                c.Id,
                c.Domain,
                c.Status,
                c.EnrollmentMode,
                c.CreatedAt,
                c.VerifiedAt,
                c.RevokedAt,
                c.CreatedByUsername))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<TenantDomainEnrollmentMatch?> ResolveAutoJoinClaimAsync(string email, CancellationToken ct = default)
    {
        var domain = TryGetNormalizedEmailDomain(email);
        if (domain is null)
        {
            return null;
        }

        return await db.TenantDomainClaims.AsNoTracking()
            .Where(c => c.NormalizedDomain == domain
                && c.Status == TenantDomainClaimStatus.Verified
                && c.EnrollmentMode == TenantDomainEnrollmentMode.AutoJoin
                && c.Tenant.Status == TenantStatus.Active)
            .Select(c => new TenantDomainEnrollmentMatch(
                c.Id,
                c.TenantId,
                c.Tenant.Slug,
                c.Tenant.Name,
                c.Domain,
                c.EnrollmentMode))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> RevokeClaimAsync(Guid tenantId, Guid claimId, Guid? revokedByUserId, string? reason, CancellationToken ct = default)
    {
        var claim = await db.TenantDomainClaims
            .FirstOrDefaultAsync(c => c.Id == claimId && c.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (claim is null || claim.Status == TenantDomainClaimStatus.Revoked)
        {
            return false;
        }

        claim.Status = TenantDomainClaimStatus.Revoked;
        claim.RevokedAt = DateTimeOffset.UtcNow;
        claim.RevokedByUserId = revokedByUserId;
        claim.RevocationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Revoked domain claim {DomainClaimId} for tenant {TenantId}", claim.Id, tenantId);
        return true;
    }

    // TODO: implement DNS verification — real DNS-based verification (token + DNS TXT check +
    // background job) is the proper long-term fix. This method only unblocks the enrollment flow
    // via an explicit admin action.
    /// <inheritdoc/>
    public async Task<bool> MarkClaimVerifiedAsync(Guid claimId, CancellationToken ct = default)
    {
        var claim = await db.TenantDomainClaims.FirstOrDefaultAsync(c => c.Id == claimId, ct);
        if (claim is null || claim.Status == TenantDomainClaimStatus.Revoked)
        {
            return false;
        }

        claim.Status = TenantDomainClaimStatus.Verified;
        claim.VerifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Domain claim {DomainClaimId} marked verified", claim.Id);
        return true;
    }

    private string? TryGetNormalizedEmailDomain(string email)
    {
        var normalizedEmail = EmailNormalizer.NormalizeForLookup(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return null;
        }

        var atIndex = normalizedEmail.LastIndexOf('@');
        if (atIndex < 0 || atIndex == normalizedEmail.Length - 1)
        {
            return null;
        }

        try
        {
            return NormalizeDomainForClaim(normalizedEmail[(atIndex + 1)..], _publicEmailDomains);
        }
        catch (ValidationException)
        {
            return null;
        }
    }

    private static string NormalizeDomainForClaim(string domain, HashSet<string> publicEmailDomains)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new ValidationException("Domain is required.");
        }

        var candidate = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (candidate.Contains('@', StringComparison.Ordinal))
        {
            throw new ValidationException("Enter a domain name, not an email address.");
        }

        if (candidate.Contains("://", StringComparison.Ordinal)
            || candidate.Contains('/', StringComparison.Ordinal)
            || candidate.Contains('\\', StringComparison.Ordinal)
            || candidate.Contains(':', StringComparison.Ordinal)
            || candidate.Contains('*', StringComparison.Ordinal))
        {
            throw new ValidationException("Enter only the domain name, such as example.com.");
        }

        string ascii;
        try
        {
            ascii = Idn.GetAscii(candidate);
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException("Domain name is invalid.", ex);
        }

        if (ascii.Length is < 4 or > 253)
        {
            throw new ValidationException("Domain name is invalid.");
        }

        var labels = ascii.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length < 2 || labels[^1].Length < 2)
        {
            throw new ValidationException("Domain must include a registrable suffix, such as example.com.");
        }

        if (labels.Any(label => label.Length > 63 || !DomainLabelRegex().IsMatch(label)))
        {
            throw new ValidationException("Domain name contains an invalid label.");
        }

        if (publicEmailDomains.Contains(ascii))
        {
            throw new ValidationException("Public email provider domains cannot be claimed.");
        }

        return ascii;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainLabelRegex();
}