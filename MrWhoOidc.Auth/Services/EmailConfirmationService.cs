using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for managing email confirmation tokens and verification.
/// </summary>
public interface IEmailConfirmationService
{
    /// <summary>
    /// Creates a confirmation token for a user's primary email.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The creation result.</returns>
    Task<EmailConfirmationCreateResult> CreatePrimaryConfirmationAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a confirmation token for a user's alternative email.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="alternative">The alternative email entity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The creation result.</returns>
    Task<EmailConfirmationCreateResult> CreateAlternativeConfirmationAsync(User user, UserAlternativeEmail alternative, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies an email confirmation token.
    /// </summary>
    /// <param name="token">The token to verify.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verification result.</returns>
    Task<EmailConfirmationVerifyResult> ConfirmAsync(string? token, CancellationToken cancellationToken = default);
}

public enum EmailConfirmationCreateStatus
{
    Created,
    AlreadyVerified,
    EmailMissing,
    AlternativeMissing
}

public sealed record EmailConfirmationCreateResult(EmailConfirmationCreateStatus Status, string? Token = null, DateTimeOffset? ExpiresAt = null)
{
    public bool IsSuccess => Status == EmailConfirmationCreateStatus.Created && Token is not null && ExpiresAt is not null;
}

public enum EmailConfirmationVerifyStatus
{
    Success,
    AlreadyVerified,
    NotFound,
    Expired,
    Cancelled,
    EmailMismatch,
    InvalidToken
}

public sealed record EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus Status, string Purpose, string? Email)
{
    public bool IsSuccess => Status == EmailConfirmationVerifyStatus.Success;
}

internal sealed class EmailConfirmationService(
    AuthDbContext db,
    IOptions<EmailConfirmationOptions> options,
    ILogger<EmailConfirmationService> logger) : IEmailConfirmationService
{
    private readonly EmailConfirmationOptions _options = options.Value;

    public async Task<EmailConfirmationCreateResult> CreatePrimaryConfirmationAsync(User user, CancellationToken cancellationToken = default)
    {
        if (user is null) throw new ArgumentNullException(nameof(user));

        if (user.EmailVerified)
        {
            return new EmailConfirmationCreateResult(EmailConfirmationCreateStatus.AlreadyVerified);
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return new EmailConfirmationCreateResult(EmailConfirmationCreateStatus.EmailMissing);
        }

        return await CreateInternalAsync(
            tenantId: user.TenantId,
            userId: user.Id,
            alternativeEmailId: null,
            email: user.Email,
            purpose: EmailConfirmationPurposes.Primary,
            cancellationToken);
    }

    public async Task<EmailConfirmationCreateResult> CreateAlternativeConfirmationAsync(User user, UserAlternativeEmail alternative, CancellationToken cancellationToken = default)
    {
        if (user is null) throw new ArgumentNullException(nameof(user));
        if (alternative is null) throw new ArgumentNullException(nameof(alternative));

        if (alternative.IsVerified)
        {
            return new EmailConfirmationCreateResult(EmailConfirmationCreateStatus.AlreadyVerified);
        }

        if (string.IsNullOrWhiteSpace(alternative.Email))
        {
            return new EmailConfirmationCreateResult(EmailConfirmationCreateStatus.AlternativeMissing);
        }

        return await CreateInternalAsync(
            tenantId: user.TenantId,
            userId: user.Id,
            alternativeEmailId: alternative.Id,
            email: alternative.Email,
            purpose: EmailConfirmationPurposes.Alternative,
            cancellationToken);
    }

    public async Task<EmailConfirmationVerifyResult> ConfirmAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.InvalidToken, string.Empty, null);
        }

        var hash = CryptoHelper.ComputeSha256Base64(token);
        var confirmation = await db.EmailConfirmations
            .FirstOrDefaultAsync(c => c.TokenHash == hash, cancellationToken)
            .ConfigureAwait(false);

        if (confirmation is null)
        {
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.NotFound, string.Empty, null);
        }

        var now = DateTimeOffset.UtcNow;

        if (confirmation.CancelledAt.HasValue)
        {
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.Cancelled, confirmation.Purpose, confirmation.Email);
        }

        if (confirmation.RedeemedAt.HasValue)
        {
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.AlreadyVerified, confirmation.Purpose, confirmation.Email);
        }

        if (confirmation.ExpiresAt < now)
        {
            confirmation.CancelledAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.Expired, confirmation.Purpose, confirmation.Email);
        }

        if (string.Equals(confirmation.Purpose, EmailConfirmationPurposes.Primary, StringComparison.OrdinalIgnoreCase))
        {
            return await ConfirmPrimaryAsync(confirmation, now, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(confirmation.Purpose, EmailConfirmationPurposes.Alternative, StringComparison.OrdinalIgnoreCase))
        {
            return await ConfirmAlternativeAsync(confirmation, now, cancellationToken).ConfigureAwait(false);
        }

        logger.LogWarning("Unknown email confirmation purpose {Purpose} for confirmation {ConfirmationId}", confirmation.Purpose, confirmation.Id);
        confirmation.CancelledAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.Cancelled, confirmation.Purpose, confirmation.Email);
    }

    private async Task<EmailConfirmationCreateResult> CreateInternalAsync(
        Guid tenantId,
        Guid userId,
        Guid? alternativeEmailId,
        string email,
        string purpose,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var active = await db.EmailConfirmations
            .Where(c => c.UserId == userId && c.Purpose == purpose && c.RedeemedAt == null && c.CancelledAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var existing in active)
        {
            existing.CancelledAt = now;
        }

        var token = GenerateToken();
        var hash = CryptoHelper.ComputeSha256Base64(token);
        var expires = now.AddHours(Math.Max(1, _options.TokenLifetimeHours));

        var entity = new EmailConfirmation
        {
            TenantId = tenantId,
            UserId = userId,
            UserAlternativeEmailId = alternativeEmailId,
            Email = email,
            Purpose = purpose,
            TokenHash = hash,
            CreatedAt = now,
            ExpiresAt = expires
        };

        db.EmailConfirmations.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new EmailConfirmationCreateResult(EmailConfirmationCreateStatus.Created, token, expires);
    }

    private async Task<EmailConfirmationVerifyResult> ConfirmPrimaryAsync(EmailConfirmation confirmation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == confirmation.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            confirmation.CancelledAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.NotFound, confirmation.Purpose, confirmation.Email);
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            confirmation.CancelledAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.EmailMismatch, confirmation.Purpose, confirmation.Email);
        }

        if (!string.Equals(user.Email, confirmation.Email, StringComparison.OrdinalIgnoreCase))
        {
            confirmation.CancelledAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.EmailMismatch, confirmation.Purpose, confirmation.Email);
        }

        if (user.EmailVerified)
        {
            confirmation.RedeemedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.AlreadyVerified, confirmation.Purpose, confirmation.Email);
        }

        user.EmailVerified = true;
        user.EmailVerifiedAt = now;
        confirmation.RedeemedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.Success, confirmation.Purpose, confirmation.Email);
    }

    private async Task<EmailConfirmationVerifyResult> ConfirmAlternativeAsync(EmailConfirmation confirmation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (confirmation.UserAlternativeEmailId is null)
        {
            confirmation.CancelledAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.NotFound, confirmation.Purpose, confirmation.Email);
        }

        var alternative = await db.UserAlternativeEmails.FirstOrDefaultAsync(a => a.Id == confirmation.UserAlternativeEmailId, cancellationToken)
            .ConfigureAwait(false);

        if (alternative is null)
        {
            confirmation.CancelledAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.NotFound, confirmation.Purpose, confirmation.Email);
        }

        if (!string.Equals(alternative.Email, confirmation.Email, StringComparison.OrdinalIgnoreCase))
        {
            confirmation.CancelledAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.EmailMismatch, confirmation.Purpose, confirmation.Email);
        }

        if (alternative.IsVerified)
        {
            confirmation.RedeemedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.AlreadyVerified, confirmation.Purpose, confirmation.Email);
        }

        alternative.IsVerified = true;
        alternative.VerifiedAt = now;
        confirmation.RedeemedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new EmailConfirmationVerifyResult(EmailConfirmationVerifyStatus.Success, confirmation.Purpose, confirmation.Email);
    }

    private static string GenerateToken()
    {
        var buffer = new byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Base64UrlEncoder.Encode(buffer);
    }
}
