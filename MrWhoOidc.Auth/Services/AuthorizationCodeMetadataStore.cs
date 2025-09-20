namespace MrWhoOidc.Auth.Services;

public interface IAuthorizationCodeMetadataStore
{
    void SetAuthTime(string code, DateTimeOffset authTime);
    bool TryGetAuthTime(string code, out DateTimeOffset authTime);
    void SetResource(string code, string resource);
    bool TryGetResource(string code, out string resource);
    void Remove(string code);
}

internal sealed class InMemoryAuthorizationCodeMetadataStore : IAuthorizationCodeMetadataStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _authTimes = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _resources = new();

    public void SetAuthTime(string code, DateTimeOffset authTime) => _authTimes[code] = authTime;
    public bool TryGetAuthTime(string code, out DateTimeOffset authTime) => _authTimes.TryGetValue(code, out authTime);

    public void SetResource(string code, string resource) => _resources[code] = resource;
    public bool TryGetResource(string code, out string resource) => _resources.TryGetValue(code, out resource);

    public void Remove(string code)
    {
        _authTimes.TryRemove(code, out _);
        _resources.TryRemove(code, out _);
    }
}
