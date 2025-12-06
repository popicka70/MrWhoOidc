using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for migrating per-tenant User credentials to global UserAccount.
/// </summary>
/// <remarks>
/// <b>OBSOLETE</b>: This service is no longer needed as per-tenant password fields have been removed from User entity.
/// All password management is now handled via UserAccount.PasswordHash and UserAccountService.
/// This service is retained for backward compatibility but does not perform any actual migration.
/// </remarks>
[Obsolete("Per-tenant password fields have been removed. Password is now managed globally via UserAccount.")]
public interface IPasswordMigrationService
{
    /// <summary>
    /// Migrates credentials from per-tenant User(s) to a UserAccount.
    /// </summary>
    /// <remarks>No longer performs any migration as per-tenant passwords have been removed.</remarks>
    Task<MigrationResult> MigrateUserCredentialsAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>
    /// Gets the overall migration status across all UserAccounts.
    /// </summary>
    Task<MigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Batch migrates all pending UserAccounts.
    /// </summary>
    /// <remarks>No longer performs any migration as per-tenant passwords have been removed.</remarks>
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
/// <remarks>
/// <b>OBSOLETE</b>: Per-tenant password fields have been removed from User entity.
/// This service now reports all accounts as migrated since passwords are managed globally.
/// </remarks>
[Obsolete("Per-tenant password fields have been removed. Password is now managed globally via UserAccount.")]
public sealed class PasswordMigrationService(
    AuthDbContext dbContext) : IPasswordMigrationService
{
    public Task<MigrationResult> MigrateUserCredentialsAsync(Guid userAccountId, CancellationToken ct = default)
    {
        // Migration is complete - per-tenant passwords no longer exist
        return Task.FromResult(MigrationResult.SkippedWithMessage("Migration complete - per-tenant passwords have been removed"));
    }

    public async Task<MigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default)
    {
        // All accounts are considered "migrated" as there's nothing to migrate from
        var total = await dbContext.UserAccounts.CountAsync(ct);
        var withPassword = await dbContext.UserAccounts
            .Where(a => a.PasswordHash != null && a.PasswordHash != string.Empty)
            .CountAsync(ct);

        return new MigrationStatus
        {
            TotalAccounts = total,
            MigratedAccounts = total, // All are "migrated" since per-tenant passwords are gone
            PendingAccounts = 0
        };
    }

    public Task<BatchMigrationResult> MigrateBatchAsync(int batchSize = 100, CancellationToken ct = default)
    {
        // Nothing to migrate - per-tenant passwords have been removed
        return Task.FromResult(new BatchMigrationResult
        {
            ProcessedCount = 0,
            SuccessCount = 0,
            FailureCount = 0,
            SkippedCount = 0,
            Duration = TimeSpan.Zero
        });
    }
}
