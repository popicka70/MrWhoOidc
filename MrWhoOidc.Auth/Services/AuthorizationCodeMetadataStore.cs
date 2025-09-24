namespace MrWhoOidc.Auth.Services;

public interface IAuthorizationCodeMetadataStore
{
    void SetAuthTime(string code, DateTimeOffset authTime);
    bool TryGetAuthTime(string code, out DateTimeOffset authTime);
    void SetResource(string code, string resource);
    bool TryGetResource(string code, out string? resource);
    // New: upstream context propagation
    void SetUpstream(string code, string? idp, string? acr, string? amr);
    bool TryGetUpstream(string code, out string? idp, out string? acr, out string? amr);
    void Remove(string code);
}

internal sealed class InMemoryAuthorizationCodeMetadataStore : IAuthorizationCodeMetadataStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _authTimes = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _resources = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? idp, string? acr, string? amr)> _upstream = new();

    public void SetAuthTime(string code, DateTimeOffset authTime) => _authTimes[code] = authTime;
    public bool TryGetAuthTime(string code, out DateTimeOffset authTime) => _authTimes.TryGetValue(code, out authTime);

    public void SetResource(string code, string resource) => _resources[code] = resource;
    public bool TryGetResource(string code, out string? resource) => _resources.TryGetValue(code, out resource);

    public void SetUpstream(string code, string? idp, string? acr, string? amr) => _upstream[code] = (idp, acr, amr);
    public bool TryGetUpstream(string code, out string? idp, out string? acr, out string? amr)
    {
        if (_upstream.TryGetValue(code, out var t))
        {
            idp = t.idp; acr = t.acr; amr = t.amr; return true;
        }
        idp = null; acr = null; amr = null; return false;
    }

    public void Remove(string code)
    {
        _authTimes.TryRemove(code, out _);
        _resources.TryRemove(code, out _);
        _upstream.TryRemove(code, out _);
    }
}
