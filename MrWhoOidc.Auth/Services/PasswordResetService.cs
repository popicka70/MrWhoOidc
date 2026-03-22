using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Result of a password reset token creation.
/// </summary>
public sealed record PasswordResetTokenResult(
    bool Succeeded,
    string? Token,
    string? ErrorMessage,
    UserAccount? Account = null);

/// <summary>
/// Result of validating a password reset token.
/// </summary>
public sealed record PasswordResetValidationResult(
    bool IsValid,
    string? ErrorMessage,
    UserAccount? Account = null);

/// <summary>
/// Service for managing password reset tokens tied to global UserAccount.
/// </summary>
public interface IPasswordResetService
{
    /// <summary>
    /// Creates a password reset token for the given email.
    /// </summary>
    /// <param name="email">Email address to reset password for</param>
    /// <param name="requestedFromIp">IP address of the requester</param>
    /// <param name="expirationMinutes">Token expiration in minutes (default 60)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Result containing the raw token (to be sent via email) or error</returns>
    Task<PasswordResetTokenResult> CreateResetTokenAsync(
        string email,
        string? requestedFromIp = null,
        int expirationMinutes = 60,
        CancellationToken ct = default);

    /// <summary>
    /// Validates a password reset token and returns the associated account.
    /// </summary>
    /// <param name="token">The raw token from the reset link</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result with account if valid</returns>
    Task<PasswordResetValidationResult> ValidateTokenAsync(
        string token,
        CancellationToken ct = default);

    /// <summary>
    /// Redeems a password reset token and updates the password.
    /// </summary>
    /// <param name="token">The raw token from the reset link</param>
    /// <param name="newPassword">The new password (plain text)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Result indicating success or failure</returns>
    Task<PasswordResetValidationResult> RedeemTokenAsync(
        string token,
        string newPassword,
        CancellationToken ct = default);

    /// <summary>
    /// Cleans up expired tokens from the database.
    /// </summary>
    Task CleanupExpiredTokensAsync(CancellationToken ct = default);
}

internal sealed class PasswordResetService(
    AuthDbContext dbContext,
    IUserAccountService userAccountService,
    IPasswordHasher passwordHasher,
    ILogger<PasswordResetService> logger) : IPasswordResetService
{
    public async Task<PasswordResetTokenResult> CreateResetTokenAsync(
        string email,
        string? requestedFromIp = null,
        int expirationMinutes = 60,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return new PasswordResetTokenResult(false, null, "Email is required.");
        }

        // Find the UserAccount by email
        var account = await userAccountService.FindByEmailAsync(email, ct).ConfigureAwait(false);
        if (account is null)
        {
            // Don't reveal whether the email exists - return success but no token
            // (In production, you'd still send a "no account found" email or similar)
            logger.LogDebug("Password reset requested for non-existent email {EmailHash}",
                HashForLog(email));
            return new PasswordResetTokenResult(true, null, null);
        }

        // Generate a secure random token
        var rawToken = GenerateSecureToken();
        var tokenHash = HashToken(rawToken);

        // Invalidate any existing unused tokens for this account
        var existingTokens = await dbContext.PasswordResetTokens
            .Where(t => t.UserAccountId == account.Id && !t.IsUsed)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var existing in existingTokens)
        {
            existing.IsUsed = true;
            existing.UsedAt = DateTimeOffset.UtcNow;
        }

        // Create new token
        var resetToken = new PasswordResetToken
        {
            UserAccountId = account.Id,
            TokenHash = tokenHash,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expirationMinutes),
            RequestedFromIp = requestedFromIp
        };

        dbContext.PasswordResetTokens.Add(resetToken);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Password reset token created for account {AccountId}, expires at {ExpiresAt}",
            account.Id, resetToken.ExpiresAt);

        return new PasswordResetTokenResult(true, rawToken, null, account);
    }

    public async Task<PasswordResetValidationResult> ValidateTokenAsync(
        string token,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new PasswordResetValidationResult(false, "Token is required.");
        }

        var tokenHash = HashToken(token);
        var resetToken = await dbContext.PasswordResetTokens
            .Include(t => t.UserAccount)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
            .ConfigureAwait(false);

        if (resetToken is null)
        {
            logger.LogDebug("Password reset token not found");
            return new PasswordResetValidationResult(false, "Invalid or expired reset link.");
        }

        if (resetToken.IsUsed)
        {
            logger.LogDebug("Password reset token already used for account {AccountId}", resetToken.UserAccountId);
            return new PasswordResetValidationResult(false, "This reset link has already been used.");
        }

        if (resetToken.ExpiresAt < DateTimeOffset.UtcNow)
        {
            logger.LogDebug("Password reset token expired for account {AccountId}", resetToken.UserAccountId);
            return new PasswordResetValidationResult(false, "This reset link has expired. Please request a new one.");
        }

        return new PasswordResetValidationResult(true, null, resetToken.UserAccount);
    }

    public async Task<PasswordResetValidationResult> RedeemTokenAsync(
        string token,
        string newPassword,
        CancellationToken ct = default)
    {
        var validation = await ValidateTokenAsync(token, ct).ConfigureAwait(false);
        if (!validation.IsValid || validation.Account is null)
        {
            return validation;
        }

        var tokenHash = HashToken(token);
        var resetToken = await dbContext.PasswordResetTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
            .ConfigureAwait(false);

        if (resetToken is null)
        {
            return new PasswordResetValidationResult(false, "Invalid or expired reset link.");
        }

        // Mark token as used
        resetToken.IsUsed = true;
        resetToken.UsedAt = DateTimeOffset.UtcNow;

        // Update the password on UserAccount (clears lockout automatically)
        var newHash = passwordHasher.Hash(newPassword);
        await userAccountService.UpdatePasswordAsync(
            validation.Account.Id,
            newHash,
            null,
            "argon2id",
            ct).ConfigureAwait(false);

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Password reset completed for account {AccountId}", validation.Account.Id);

        return new PasswordResetValidationResult(true, null, validation.Account);
    }

    public async Task CleanupExpiredTokensAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7); // Keep for 7 days for audit

        // ⚡ Bolt Performance Optimization:
        // Replaced .ToListAsync() + .RemoveRange() with .ExecuteDeleteAsync()
        // Impact: Eliminates N+1 memory allocation for expired entities during background task execution.
        var expiredCount = await dbContext.PasswordResetTokens
            .Where(t => t.ExpiresAt < cutoff || (t.IsUsed && t.UsedAt < cutoff))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        if (expiredCount > 0)
        {
            logger.LogInformation("Cleaned up {Count} expired password reset tokens", expiredCount);
        }
    }

    /// <summary>
    /// Generates a cryptographically secure random token.
    /// </summary>
    private static string GenerateSecureToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    /// <summary>
    /// Hashes a token for storage (SHA256).
    /// </summary>
    private static string HashToken(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Hashes a value for safe logging.
    /// </summary>
    private static string HashForLog(string value)
    {
        if (string.IsNullOrEmpty(value)) return "[empty]";
        var hash = value.GetHashCode(StringComparison.OrdinalIgnoreCase);
        return $"[hash:{hash:X8}]";
    }
}
