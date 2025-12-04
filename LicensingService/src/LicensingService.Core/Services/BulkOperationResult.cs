namespace LicensingService.Core.Services;

/// <summary>
/// Result of a bulk license operation.
/// </summary>
public class BulkOperationResult
{
    /// <summary>Total number of licenses requested for the operation.</summary>
    public int TotalRequested { get; init; }

    /// <summary>Number of successfully processed licenses.</summary>
    public int SuccessCount { get; init; }

    /// <summary>Number of failed operations.</summary>
    public int FailureCount { get; init; }

    /// <summary>Details of successful operations.</summary>
    public IReadOnlyList<BulkOperationSuccess> Successes { get; init; } = [];

    /// <summary>Details of failed operations.</summary>
    public IReadOnlyList<BulkOperationFailure> Failures { get; init; } = [];

    /// <summary>Whether all operations succeeded.</summary>
    public bool AllSucceeded => FailureCount == 0 && SuccessCount == TotalRequested;

    /// <summary>Whether any operations succeeded.</summary>
    public bool PartialSuccess => SuccessCount > 0 && FailureCount > 0;

    /// <summary>
    /// Creates a fully successful result.
    /// </summary>
    public static BulkOperationResult Success(IReadOnlyList<BulkOperationSuccess> successes)
    {
        return new BulkOperationResult
        {
            TotalRequested = successes.Count,
            SuccessCount = successes.Count,
            FailureCount = 0,
            Successes = successes,
            Failures = []
        };
    }

    /// <summary>
    /// Creates a result with mixed success and failure.
    /// </summary>
    public static BulkOperationResult Mixed(
        IReadOnlyList<BulkOperationSuccess> successes,
        IReadOnlyList<BulkOperationFailure> failures)
    {
        return new BulkOperationResult
        {
            TotalRequested = successes.Count + failures.Count,
            SuccessCount = successes.Count,
            FailureCount = failures.Count,
            Successes = successes,
            Failures = failures
        };
    }
}

/// <summary>
/// Details of a successful bulk operation item.
/// </summary>
public class BulkOperationSuccess
{
    /// <summary>Original license ID that was processed.</summary>
    public required Guid OriginalLicenseId { get; init; }

    /// <summary>New license ID (for renewal operations).</summary>
    public Guid? NewLicenseId { get; init; }

    /// <summary>New license token (for renewal operations).</summary>
    public string? NewToken { get; init; }
}

/// <summary>
/// Details of a failed bulk operation item.
/// </summary>
public class BulkOperationFailure
{
    /// <summary>License ID that failed.</summary>
    public required Guid LicenseId { get; init; }

    /// <summary>Error message.</summary>
    public required string Error { get; init; }

    /// <summary>Error code for programmatic handling.</summary>
    public required string ErrorCode { get; init; }
}

/// <summary>
/// Request to bulk renew licenses.
/// </summary>
public class BulkRenewRequest
{
    /// <summary>List of license IDs to renew.</summary>
    public required IReadOnlyList<Guid> LicenseIds { get; init; }

    /// <summary>New validity period end date for all licenses.</summary>
    public required DateTimeOffset NewValidUntil { get; init; }

    /// <summary>Optional option updates to apply to all renewed licenses.</summary>
    public Dictionary<string, object>? OptionUpdates { get; init; }
}

/// <summary>
/// Request to bulk revoke licenses.
/// </summary>
public class BulkRevokeRequest
{
    /// <summary>List of license IDs to revoke.</summary>
    public required IReadOnlyList<Guid> LicenseIds { get; init; }

    /// <summary>Reason for revocation (applies to all).</summary>
    public required string Reason { get; init; }
}
