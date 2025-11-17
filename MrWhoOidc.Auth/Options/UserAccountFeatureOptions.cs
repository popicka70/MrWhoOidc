namespace MrWhoOidc.Auth.Options;

/// <summary>
/// Feature flags that control the rollout of user-account/tenant decoupling work.
/// Defaults keep legacy behavior unless explicitly enabled via configuration.
/// </summary>
public sealed class UserAccountFeatureOptions
{
    /// <summary>
    /// Enables the new UserAccount + UserTenantMembership dual-write pipeline.
    /// </summary>
    public bool UserAccountDecouplingEnabled { get; set; }

    /// <summary>
    /// Enables tenant picker UX prompts after login (future use).
    /// </summary>
    public bool TenantPickerUxEnabled { get; set; }
}
