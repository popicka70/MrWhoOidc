namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Store for metadata associated with an authorization code that is not persisted in the main database.
/// </summary>
public interface IAuthorizationCodeMetadataStore
{
    /// <summary>
    /// Sets the authentication time for a code.
    /// </summary>
    void SetAuthTime(string code, DateTimeOffset authTime);

    /// <summary>
    /// Tries to get the authentication time for a code.
    /// </summary>
    bool TryGetAuthTime(string code, out DateTimeOffset authTime);

    /// <summary>
    /// Sets the resource (audience) for a code.
    /// </summary>
    void SetResource(string code, string resource);

    /// <summary>
    /// Tries to get the resource for a code.
    /// </summary>
    bool TryGetResource(string code, out string? resource);

    /// <summary>
    /// Sets upstream identity provider context for a code.
    /// </summary>
    void SetUpstream(string code, string? idp, string? acr, string? amr);
    bool TryGetUpstream(string code, out string? idp, out string? acr, out string? amr);
    // New: mapped claim propagation
    void SetMappedClaims(string code, IReadOnlyDictionary<string, string> claims);
    bool TryGetMappedClaims(string code, out IReadOnlyDictionary<string, string> claims);
    // New: front-channel logout session id (sid)
    void SetSid(string code, string sid);
    bool TryGetSid(string code, out string? sid);
    void Remove(string code);
}

internal sealed class InMemoryAuthorizationCodeMetadataStore : IAuthorizationCodeMetadataStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _authTimes = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _resources = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? idp, string? acr, string? amr)> _upstream = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.Dictionary<string, string>> _mapped = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sids = new();

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

    public void SetMappedClaims(string code, IReadOnlyDictionary<string, string> claims)
    {
        _mapped[code] = new System.Collections.Generic.Dictionary<string, string>(claims, System.StringComparer.Ordinal);
    }

    public bool TryGetMappedClaims(string code, out IReadOnlyDictionary<string, string> claims)
    {
        if (_mapped.TryGetValue(code, out var dict))
        {
            claims = dict; return true;
        }
        claims = System.Array.Empty<System.Collections.Generic.KeyValuePair<string, string>>().ToDictionary(k => k.Key, v => v.Value);
        return false;
    }

    public void SetSid(string code, string sid) => _sids[code] = sid;

    public bool TryGetSid(string code, out string? sid)
    {
        if (_sids.TryGetValue(code, out var v)) { sid = v; return true; }
        sid = null; return false;
    }

    public void Remove(string code)
    {
        _authTimes.TryRemove(code, out _);
        _resources.TryRemove(code, out _);
        _upstream.TryRemove(code, out _);
        _mapped.TryRemove(code, out _);
        _sids.TryRemove(code, out _);
    }
}
