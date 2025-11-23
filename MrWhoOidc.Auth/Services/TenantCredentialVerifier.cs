using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Services;

public interface ITenantCredentialVerifier
{
    Task<TenantCredentialVerificationResult> VerifyAsync(string email, string password, CancellationToken ct = default);
}

public sealed record TenantCredentialVerificationResult(bool Success)
{
    public static TenantCredentialVerificationResult Failed { get; } = new(false);
    public static TenantCredentialVerificationResult Passed { get; } = new(true);
}

internal sealed class TenantCredentialVerifier(
    AuthDbContext dbContext,
    IPasswordHasher passwordHasher,
    ILogger<TenantCredentialVerifier> logger) : ITenantCredentialVerifier
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    public async Task<TenantCredentialVerificationResult> VerifyAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return TenantCredentialVerificationResult.Failed;
        }

        var normalizedEmail = EmailNormalizer.NormalizeForLookup(email);
        if (string.IsNullOrEmpty(normalizedEmail))
        {
            return TenantCredentialVerificationResult.Failed;
        }

        var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tokenSource.CancelAfter(QueryTimeout);

        try
        {
            var credentialRows = await QueryCredentialRowsAsync(normalizedEmail, tokenSource.Token).ConfigureAwait(false);
            if (credentialRows.Count == 0)
            {
                logger.LogInformation("Tenant credential verification failed (no account) for email hash {EmailHash}", HashEmail(email));
                return TenantCredentialVerificationResult.Failed;
            }

            foreach (var row in credentialRows)
            {
                if (string.IsNullOrWhiteSpace(row.PasswordHash))
                {
                    continue;
                }

                if (VerifyPassword(password, row))
                {
                    logger.LogInformation("Tenant credential verification succeeded for email hash {EmailHash}", HashEmail(email));
                    return TenantCredentialVerificationResult.Passed;
                }
            }

            logger.LogWarning("Tenant credential verification failed (invalid password) for email hash {EmailHash}", HashEmail(email));
            return TenantCredentialVerificationResult.Failed;
        }
        catch (OperationCanceledException) when (tokenSource.IsCancellationRequested)
        {
            logger.LogWarning("Tenant credential verification timed out for email hash {EmailHash}", HashEmail(email));
            return TenantCredentialVerificationResult.Failed;
        }
    }

    private async Task<List<CredentialRow>> QueryCredentialRowsAsync(string normalizedEmail, CancellationToken ct)
    {
        var primaryCredentials = await dbContext.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .Select(u => new CredentialRow(u.PasswordHash, u.HashAlgorithm))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (primaryCredentials.Count > 0)
        {
            return primaryCredentials;
        }

        return await (from alt in dbContext.UserAlternativeEmails.AsNoTracking()
                      where alt.NormalizedEmail == normalizedEmail && alt.IsVerified
                      join user in dbContext.Users.AsNoTracking() on alt.UserId equals user.Id
                      select new CredentialRow(user.PasswordHash, user.HashAlgorithm))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private bool VerifyPassword(string password, CredentialRow row)
    {
        return row.HashAlgorithm switch
        {
            SecurityConstants.HashAlgorithms.BCrypt => BCrypt.Net.BCrypt.Verify(password, row.PasswordHash),
            SecurityConstants.HashAlgorithms.Argon2id or _ => passwordHasher.Verify(password, row.PasswordHash)
        };
    }

    private static string HashEmail(string email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return "empty";
        }

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
    }

    private sealed record CredentialRow(string PasswordHash, string HashAlgorithm);
}
