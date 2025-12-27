namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Determines whether opaque access tokens should be used for a specific request.
/// </summary>
public interface IOpaqueTokenPolicy
{
    /// <summary>
    /// Evaluates if opaque access tokens are enabled for the given audience.
    /// </summary>
    bool ShouldUseOpaqueAccessToken(string? audience);
}
