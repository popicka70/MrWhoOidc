using System;

namespace MrWhoOidc.Auth.Models.Delegation;

/// <summary>
/// Immutable request-level context for authorization evaluation.
/// Implements AD-1: Keep actor and subject distinct.
/// Normal requests have actor equal subject.
/// Delegated Access has actor as delegate and subject as delegator.
/// Tenant Support Access has actor as platform administrator with no user subject.
/// </summary>
public sealed record EffectiveAccessContext(
    Guid ActorUserAccountId,
    Guid SubjectUserAccountId,
    Guid TenantId,
    AccessContextKind Kind,
    Guid? SupportAccessSessionId,
    Guid? DelegatedAccessGrantId);

/// <summary>
/// Distinguishes the kind of access context in use.
/// Exactly one elevated context may be active at a time.
/// </summary>
public enum AccessContextKind
{
    /// <summary>Standard request where actor equals subject.</summary>
    Normal = 0,

    /// <summary>Tenant Support Access — actor is platform administrator, no user subject.</summary>
    TenantSupportAccess = 1,

    /// <summary>Delegated Access — actor is delegate, subject is delegator.</summary>
    DelegatedAccess = 2
}
