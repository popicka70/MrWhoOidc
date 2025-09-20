namespace MrWhoOidc.Auth.Services;

public interface IAuthorizationCodeMetadataStore
{
    void SetAuthTime(string code, DateTimeOffset authTime);
    bool TryGetAuthTime(string code, out DateTimeOffset authTime);
    void Remove(string code);
}

internal sealed class InMemoryAuthorizationCodeMetadataStore : IAuthorizationCodeMetadataStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _authTimes = new();

    public void SetAuthTime(string code, DateTimeOffset authTime) => _authTimes[code] = authTime;

    public bool TryGetAuthTime(string code, out DateTimeOffset authTime) => _authTimes.TryGetValue(code, out authTime);

    public void Remove(string code) => _authTimes.TryRemove(code, out _);
}
