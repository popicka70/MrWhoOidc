namespace MrWhoOidc.Client.Tokens;

public interface IMrWhoClientCredentialsManager
{
    ValueTask<TokenResult> AcquireTokenAsync(string registrationName, CancellationToken cancellationToken = default, bool forceRefresh = false);
}
