namespace MrWhoOidc.Client.Tokens;

public interface IMrWhoOnBehalfOfManager
{
    ValueTask<TokenResult> AcquireTokenAsync(string registrationName, string subjectAccessToken, CancellationToken cancellationToken = default, bool forceRefresh = false);
}
