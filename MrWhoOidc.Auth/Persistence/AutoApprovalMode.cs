namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Defines auto-approval behavior for new user registrations at the client level.
/// </summary>
public enum AutoApprovalMode
{
    /// <summary>
    /// Auto-approval is disabled. All registrations require manual admin approval.
    /// </summary>
    No = 0,

    /// <summary>
    /// Auto-approve only registrations that come from external identity providers.
    /// Local registrations still require manual approval.
    /// </summary>
    OnlyExternalIdp = 1,

    /// <summary>
    /// Auto-approve all new registrations regardless of their source (local or external IdP).
    /// </summary>
    All = 2
}
