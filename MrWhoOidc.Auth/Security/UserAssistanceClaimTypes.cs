namespace MrWhoOidc.Auth.Security;

/// <summary>
/// Claim types carried by the requester overlay while a consent-based assistance session is active.
/// They are request-local security metadata: authorization handlers use the effective role ids to enforce the
/// tenant's full-target/intersection choice, and token builders use the session id/mode to mark issued tokens.
/// </summary>
public static class UserAssistanceClaimTypes
{
    public const string SessionId = "urn:mrwho:assistance-session-id";
    public const string Mode = "urn:mrwho:assistance-mode";
    public const string HelperUserId = "urn:mrwho:assistance-helper-user-id";
    public const string HelperUserAccountId = "urn:mrwho:assistance-helper-user-account-id";
    public const string HelperUsername = "urn:mrwho:assistance-helper-username";
    public const string EffectiveRoleIds = "urn:mrwho:assistance-effective-role-ids";
    public const string RequestedMode = "urn:mrwho:assistance-requested-mode";

    public const string ReadOnlyMode = "read_only";
    public const string ReadWriteMode = "read_write";
}

/// <summary>Token claim names exposed to relying parties for consented assistance.</summary>
public static class UserAssistanceConstants
{
    public const string AssistanceId = "assistance_id";
    public const string AssistanceMode = "assistance_mode";
}
