using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
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

    /// <summary>
    /// Verifies ownership of the claimed domain: the claim is marked Verified only if a TXT record at
    /// <see cref="TenantDomainClaim.VerificationDnsName"/> equals <see cref="TenantDomainClaim.VerificationDnsValue"/>.
    /// </summary>
    Task<TenantDomainClaimVerificationResult> VerifyClaimAsync(Guid tenantId, Guid claimId, CancellationToken ct = default);

    /// <summary>
    /// Platform-admin override: marks a claim Verified without the DNS check. Audited; callers must restrict it to
    /// platform administrators.
    /// </summary>
    Task<bool> MarkClaimVerifiedManuallyAsync(Guid claimId, Guid? actorUserId, string? actorName, string reason, CancellationToken ct = default);
}

public sealed record TenantDomainClaimCreateResult(TenantDomainClaim Claim);

public enum TenantDomainClaimVerificationOutcome
{
    NotFound,
    Revoked,
    AlreadyVerified,
    Verified,
    RecordNotFound
}

public sealed record TenantDomainClaimVerificationResult(
    TenantDomainClaimVerificationOutcome Outcome,
    string? Domain = null,
    string? DnsName = null,
    string? DnsValue = null);

public sealed record TenantDomainClaimListItem(
    Guid Id,
    string Domain,
    TenantDomainClaimStatus Status,
    TenantDomainEnrollmentMode EnrollmentMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? RevokedAt,
    string? CreatedByUsername,
    string? VerificationDnsName = null,
    string? VerificationDnsValue = null);

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
    IOptions<PublicEmailDomainOptions> publicEmailDomainOptions,
    IDnsTxtResolver? dnsResolver = null,
    MrWhoOidc.Auth.Observability.IAuditSink? audit = null) : ITenantDomainClaimService
{
    /// <summary>Owner-only label: a domain owner publishes the TXT record there, nobody else can.</summary>
    public const string ChallengeLabel = "_mrwho-challenge";
    public const string ChallengeValuePrefix = "mrwho-domain-verification=";

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
        AssignChallenge(claim);

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
                c.CreatedByUsername,
                c.VerificationDnsName,
                c.VerificationDnsValue))
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

    /// <inheritdoc/>
    public async Task<TenantDomainClaimVerificationResult> VerifyClaimAsync(Guid tenantId, Guid claimId, CancellationToken ct = default)
    {
        var claim = await db.TenantDomainClaims
            .FirstOrDefaultAsync(c => c.Id == claimId && c.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (claim is null)
        {
            return new TenantDomainClaimVerificationResult(TenantDomainClaimVerificationOutcome.NotFound);
        }

        if (claim.Status == TenantDomainClaimStatus.Revoked)
        {
            return new TenantDomainClaimVerificationResult(TenantDomainClaimVerificationOutcome.Revoked, claim.Domain);
        }

        if (claim.Status == TenantDomainClaimStatus.Verified)
        {
            return new TenantDomainClaimVerificationResult(TenantDomainClaimVerificationOutcome.AlreadyVerified, claim.Domain);
        }

        // Claims created before DNS verification have no challenge yet: issue one; the owner publishes it first.
        if (string.IsNullOrEmpty(claim.VerificationToken) || string.IsNullOrEmpty(claim.VerificationDnsName) || string.IsNullOrEmpty(claim.VerificationDnsValue))
        {
            AssignChallenge(claim);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Pending(claim);
        }

        var records = dnsResolver is null
            ? []
            : await dnsResolver.ResolveTxtAsync(claim.VerificationDnsName, ct).ConfigureAwait(false);
        if (!records.Any(r => string.Equals(r.Trim(), claim.VerificationDnsValue, StringComparison.Ordinal)))
        {
            logger.LogInformation("Domain claim {DomainClaimId}: verification TXT record not found at {DnsName}", claim.Id, claim.VerificationDnsName);
            return Pending(claim);
        }

        claim.Status = TenantDomainClaimStatus.Verified;
        claim.VerifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        audit?.Emit("tenant_domain_claim.verified", new
        {
            claim_id = claim.Id,
            tenant_id = claim.TenantId,
            domain = claim.Domain,
            method = "dns_txt"
        });
        logger.LogInformation("Domain claim {DomainClaimId} verified by DNS TXT record", claim.Id);
        return new TenantDomainClaimVerificationResult(TenantDomainClaimVerificationOutcome.Verified, claim.Domain);
    }

    /// <inheritdoc/>
    public async Task<bool> MarkClaimVerifiedManuallyAsync(Guid claimId, Guid? actorUserId, string? actorName, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required for a manual domain verification.", nameof(reason));
        }

        var claim = await db.TenantDomainClaims.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == claimId, ct).ConfigureAwait(false);
        if (claim is null || claim.Status == TenantDomainClaimStatus.Revoked)
        {
            return false;
        }

        claim.Status = TenantDomainClaimStatus.Verified;
        claim.VerifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        audit?.Emit("tenant_domain_claim.verified", new
        {
            claim_id = claim.Id,
            tenant_id = claim.TenantId,
            domain = claim.Domain,
            method = "manual_override",
            actor_id = actorUserId?.ToString(),
            actor = actorName,
            reason = reason.Trim()
        });
        logger.LogWarning(
            "Domain claim {DomainClaimId} ({Domain}) manually marked verified by {Actor} ({ActorId}): {Reason}",
            claim.Id, claim.Domain, actorName, actorUserId, reason.Trim());
        return true;
    }

    private static TenantDomainClaimVerificationResult Pending(TenantDomainClaim claim)
        => new(TenantDomainClaimVerificationOutcome.RecordNotFound, claim.Domain, claim.VerificationDnsName, claim.VerificationDnsValue);

    private static void AssignChallenge(TenantDomainClaim claim)
    {
        claim.VerificationToken = Base64UrlToken(32);
        claim.VerificationDnsName = $"{ChallengeLabel}.{claim.NormalizedDomain}";
        claim.VerificationDnsValue = ChallengeValuePrefix + claim.VerificationToken;
    }

    private static string Base64UrlToken(int bytes)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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