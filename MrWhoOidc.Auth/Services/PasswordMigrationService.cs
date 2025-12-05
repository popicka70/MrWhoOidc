using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for migrating per-tenant User credentials to global UserAccount.
/// </summary>
public interface IPasswordMigrationService
{
    /// <summary>
    /// Migrates credentials from per-tenant User(s) to a UserAccount.
    /// Uses the most recently created User's password.
    /// </summary>
    /// <param name="userAccountId">The UserAccount to migrate credentials to</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Migration result with success/failure details</returns>
    Task<MigrationResult> MigrateUserCredentialsAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>
    /// Gets the overall migration status across all UserAccounts.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Status with counts of migrated/pending accounts</returns>
    Task<MigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Batch migrates all pending UserAccounts.
    /// </summary>
    /// <param name="batchSize">Number of accounts to process per batch</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Batch result with processed/success/failure counts</returns>
    Task<BatchMigrationResult> MigrateBatchAsync(int batchSize = 100, CancellationToken ct = default);
}

/// <summary>
/// Result of a single account migration.
/// </summary>
public sealed record MigrationResult
{
    public bool Success { get; init; }
    public bool Skipped { get; init; }
    public string? Message { get; init; }
    public int AffectedTenants { get; init; }

    public static MigrationResult Succeeded(int affectedTenants)
        => new() { Success = true, AffectedTenants = affectedTenants };

    public static MigrationResult SkippedWithMessage(string message)
        => new() { Success = true, Skipped = true, Message = message };

    public static MigrationResult Failed(string message)
        => new() { Success = false, Message = message };
}

/// <summary>
/// Overall migration status.
/// </summary>
public sealed record MigrationStatus
{
    public int TotalAccounts { get; init; }
    public int MigratedAccounts { get; init; }
    public int PendingAccounts { get; init; }
    public double PercentComplete => TotalAccounts > 0 ? (double)MigratedAccounts / TotalAccounts * 100 : 100;
}

/// <summary>
/// Result of batch migration.
/// </summary>
public sealed record BatchMigrationResult
{
    public int ProcessedCount { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public int SkippedCount { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Implementation of password migration from per-tenant User to global UserAccount.
/// </summary>
public sealed class PasswordMigrationService(
    AuthDbContext dbContext) : IPasswordMigrationService
{
    public async Task<MigrationResult> MigrateUserCredentialsAsync(Guid userAccountId, CancellationToken ct = default)
    {
        var account = await dbContext.UserAccounts.FindAsync([userAccountId], ct);
        if (account is null)
        {
            return MigrationResult.Failed($"UserAccount {userAccountId} not found");
        }

        // Skip if account already has a password
        if (!string.IsNullOrEmpty(account.PasswordHash))
        {
            return MigrationResult.SkippedWithMessage("Account already has password");
        }

        // Find all linked tenants
        var memberships = await dbContext.UserTenantMemberships
            .Where(m => m.UserAccountId == userAccountId)
            .ToListAsync(ct);

        if (memberships.Count == 0)
        {
            return MigrationResult.SkippedWithMessage("No linked users found to migrate from");
        }

        // Find per-tenant Users that match this account by email
        // Select the most recently created one (as proxy for most recent password)
        var normalizedEmail = account.NormalizedEmail;
        var tenantIds = memberships.Select(m => m.TenantId).ToList();

        var mostRecentUser = await dbContext.Users
            .Where(u => tenantIds.Contains(u.TenantId) &&
                       (u.NormalizedEmail == normalizedEmail || u.Username == account.Username))
            .OrderByDescending(u => u.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (mostRecentUser is null)
        {
            return MigrationResult.SkippedWithMessage("No linked users found to migrate from");
        }

        // Migrate password
        account.PasswordHash = mostRecentUser.PasswordHash;
        account.PasswordSalt = mostRecentUser.PasswordSalt;
        account.HashAlgorithm = mostRecentUser.HashAlgorithm;
        account.PasswordUpdatedAt = DateTimeOffset.UtcNow;

        // Migrate MFA if not already set and source has it
        if (!account.TotpEnabled && mostRecentUser.TotpEnabled)
        {
            account.TotpEnabled = mostRecentUser.TotpEnabled;
            account.TotpSecret = mostRecentUser.TotpSecret;
        }

        await dbContext.SaveChangesAsync(ct);

        return MigrationResult.Succeeded(memberships.Count);
    }

    public async Task<MigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default)
    {
        var total = await dbContext.UserAccounts.CountAsync(ct);
        var migrated = await dbContext.UserAccounts
            .Where(a => a.PasswordHash != null && a.PasswordHash != string.Empty)
            .CountAsync(ct);

        return new MigrationStatus
        {
            TotalAccounts = total,
            MigratedAccounts = migrated,
            PendingAccounts = total - migrated
        };
    }

    public async Task<BatchMigrationResult> MigrateBatchAsync(int batchSize = 100, CancellationToken ct = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var processed = 0;
        var success = 0;
        var failed = 0;
        var skipped = 0;

        // Get accounts without passwords
        var pendingAccountIds = await dbContext.UserAccounts
            .Where(a => a.PasswordHash == null || a.PasswordHash == string.Empty)
            .Select(a => a.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var accountId in pendingAccountIds)
        {
            processed++;
            var result = await MigrateUserCredentialsAsync(accountId, ct);

            if (result.Success)
            {
                if (result.Skipped)
                    skipped++;
                else
                    success++;
            }
            else
            {
                failed++;
            }
        }

        return new BatchMigrationResult
        {
            ProcessedCount = processed,
            SuccessCount = success,
            FailureCount = failed,
            SkippedCount = skipped,
            Duration = DateTimeOffset.UtcNow - startTime
        };
    }
}
