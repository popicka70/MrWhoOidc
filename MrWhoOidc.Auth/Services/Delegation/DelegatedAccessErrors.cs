using System;

namespace MrWhoOidc.Auth.Services.Delegation;

/// <summary>
/// Standard error types for delegated access grant operations.
/// Each maps to a stable error code from Section 7.4.
/// </summary>

// --- Validation / Input Errors ---

/// <summary>
/// Raised when an argument is invalid or missing.
/// Maps to general input validation errors.
/// </summary>
public sealed class ArgumentError : Exception
{
    public ArgumentError(string message) : base(message) { }
}

/// <summary>
/// Raised when a requested entity is not found.
/// Maps to delegation_not_found.
/// </summary>
public sealed class NotFoundError : Exception
{
    public NotFoundError(string message) : base(message) { }
}

/// <summary>
/// Raised when a conflict prevents the operation (e.g., already consumed, terminal state).
/// Maps to delegation_conflict.
/// </summary>
public sealed class ConflictError : Exception
{
    public ConflictError(string message) : base(message) { }
}

/// <summary>
/// Raised when an entity has expired and cannot be used.
/// Maps to delegation_expired.
/// </summary>
public sealed class ExpiredError : Exception
{
    public ExpiredError(string message) : base(message) { }
}

/// <summary>
/// Raised when the delegate ID does not match the grant's expected delegate.
/// Maps to delegate_mismatch.
/// </summary>
public sealed class MismatchError : Exception
{
    public MismatchError(string message) : base(message) { }
}

/// <summary>
/// Raised when the actor lacks authorization for the operation.
/// Maps to general authorization errors.
/// </summary>
public sealed class AuthorizationError : Exception
{
    public AuthorizationError(string message) : base(message) { }
}

// --- Authorization-specific errors ---

/// <summary>
/// Raised when the grant is not in an active state.
/// Maps to delegation_not_active.
/// </summary>
public sealed class StatusError : Exception
{
    public StatusError(string message) : base(message) { }
}

/// <summary>
/// Raised when a capability is not delegable or unknown.
/// Maps to capability_not_delegable.
/// </summary>
public sealed class CapabilityError : Exception
{
    public CapabilityError(string message) : base(message) { }
}

/// <summary>
/// Raised when a resource does not match grant constraints.
/// Maps to resource_not_granted.
/// </summary>
public sealed class ResourceError : Exception
{
    public ResourceError(string message) : base(message) { }
}

/// <summary>
/// Raised when the target tenant is not active.
/// Maps to tenant-level errors.
/// </summary>
public sealed class TenantError : Exception
{
    public TenantError(string message) : base(message) { }
}

/// <summary>
/// Raised when a party's tenant membership is inactive or missing.
/// Maps to membership_inactive.
/// </summary>
public sealed class MembershipError : Exception
{
    public MembershipError(string message) : base(message) { }
}

/// <summary>
/// Raised when a membership has expired.
/// Maps to membership_inactive.
/// </summary>
public sealed class ExpiredMembershipError : Exception
{
    public ExpiredMembershipError(string message) : base(message) { }
}
