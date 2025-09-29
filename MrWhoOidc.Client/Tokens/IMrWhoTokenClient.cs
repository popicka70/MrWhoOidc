namespace MrWhoOidc.Client.Tokens;

public interface IMrWhoTokenClient
{
    ValueTask<TokenResult> ExchangeCodeAsync(string code, Uri redirectUri, string? codeVerifier = null, CancellationToken cancellationToken = default);
    ValueTask<TokenResult> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
    ValueTask<TokenResult> ClientCredentialsAsync(IEnumerable<string>? scopes = null, CancellationToken cancellationToken = default);
    ValueTask<TokenResult> ClientCredentialsAsync(ClientCredentialsRequest request, CancellationToken cancellationToken = default);
    ValueTask<TokenResult> TokenExchangeAsync(TokenExchangeRequest request, CancellationToken cancellationToken = default);
}
