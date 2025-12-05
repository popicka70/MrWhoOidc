using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Verifies user credentials against per-tenant User.PasswordHash values.
/// </summary>
/// <remarks>
/// <para>
/// <b>DEPRECATION NOTICE</b>: This service is deprecated in favor of <see cref="IGlobalAuthenticationService"/>
/// which authenticates against the global <c>UserAccount.PasswordHash</c>.
/// </para>
/// <para>
/// This service is retained for:
/// <list type="bullet">
/// <item>Unauthenticated tenant discovery flow (SelectTenant page)</item>
/// <item>Migration period compatibility when users have per-tenant passwords</item>
/// </list>
/// </para>
/// <para>
/// After all users are migrated to global credentials via <see cref="IPasswordMigrationService"/>,
/// this service can be removed and the SelectTenant flow updated to use global auth.
/// </para>
/// </remarks>
[Obsolete("Use IGlobalAuthenticationService for authentication. This service is retained for migration compatibility.")]
public interface ITenantCredentialVerifier
{
    Task<TenantCredentialVerificationResult> VerifyAsync(string email, string password, CancellationToken ct = default);
}

public sealed record TenantCredentialVerificationResult(bool Success, IReadOnlyList<VerifiedTenantUser> VerifiedUsers)
{
    public static readonly TenantCredentialVerificationResult Failed = new(false, Array.Empty<VerifiedTenantUser>());

    public static TenantCredentialVerificationResult Passed(IReadOnlyList<VerifiedTenantUser> users)
        => new(true, users);
}

public sealed record VerifiedTenantUser(Guid TenantId, Guid UserId);

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

            var matchedUsers = new List<VerifiedTenantUser>();

            foreach (var row in credentialRows)
            {
                if (string.IsNullOrWhiteSpace(row.PasswordHash))
                {
                    continue;
                }

                if (VerifyPassword(password, row))
                {
                    matchedUsers.Add(new VerifiedTenantUser(row.TenantId, row.UserId));
                }
            }

            if (matchedUsers.Count > 0)
            {
                logger.LogInformation(
                    "Tenant credential verification succeeded for email hash {EmailHash} across {Count} tenant(s)",
                    HashEmail(email),
                    matchedUsers.Count);
                return TenantCredentialVerificationResult.Passed(matchedUsers);
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
            .Select(u => new CredentialRow(u.Id, u.TenantId, u.PasswordHash, u.HashAlgorithm))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (primaryCredentials.Count > 0)
        {
            return primaryCredentials;
        }

        return await (from alt in dbContext.UserAlternativeEmails.AsNoTracking()
                      where alt.NormalizedEmail == normalizedEmail && alt.IsVerified
                      join user in dbContext.Users.AsNoTracking() on alt.UserId equals user.Id
                      select new CredentialRow(user.Id, user.TenantId, user.PasswordHash, user.HashAlgorithm))
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

    private sealed record CredentialRow(Guid UserId, Guid TenantId, string PasswordHash, string HashAlgorithm);
}
