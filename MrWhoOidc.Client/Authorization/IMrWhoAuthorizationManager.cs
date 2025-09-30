namespace MrWhoOidc.Client.Authorization;

public interface IMrWhoAuthorizationManager
{
    ValueTask<AuthorizationRequestContext> BuildAuthorizeRequestAsync(Uri redirectUri, Action<AuthorizationRequestOptions>? configure = null, CancellationToken cancellationToken = default);
    ValueTask<AuthorizationCallbackResult> ValidateCallbackAsync(string state, string? code, string? error, string? response = null, CancellationToken cancellationToken = default);
}
