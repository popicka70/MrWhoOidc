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
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _authTimes = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _resources = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? idp, string? acr, string? amr)> _upstream = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.Dictionary<string, string>> _mapped = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sids = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _expiresAt = new();
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _utcNow;
    private long _nextCleanupTicks;

    public InMemoryAuthorizationCodeMetadataStore()
        : this(DefaultTtl, static () => DateTimeOffset.UtcNow)
    {
    }

    internal InMemoryAuthorizationCodeMetadataStore(TimeSpan ttl, Func<DateTimeOffset> utcNow)
    {
        _ttl = ttl <= TimeSpan.Zero ? DefaultTtl : ttl;
        _utcNow = utcNow;
        _nextCleanupTicks = utcNow().UtcTicks;
    }

    public void SetAuthTime(string code, DateTimeOffset authTime)
    {
        CleanupIfDue();
        _authTimes[code] = authTime;
        Touch(code);
    }

    public bool TryGetAuthTime(string code, out DateTimeOffset authTime)
    {
        CleanupIfDue();
        if (IsExpired(code))
        {
            authTime = default;
            return false;
        }

        return _authTimes.TryGetValue(code, out authTime);
    }

    public void SetResource(string code, string resource)
    {
        CleanupIfDue();
        _resources[code] = resource;
        Touch(code);
    }

    public bool TryGetResource(string code, out string? resource)
    {
        CleanupIfDue();
        if (IsExpired(code))
        {
            resource = null;
            return false;
        }

        return _resources.TryGetValue(code, out resource);
    }

    public void SetUpstream(string code, string? idp, string? acr, string? amr)
    {
        CleanupIfDue();
        _upstream[code] = (idp, acr, amr);
        Touch(code);
    }

    public bool TryGetUpstream(string code, out string? idp, out string? acr, out string? amr)
    {
        CleanupIfDue();
        if (IsExpired(code))
        {
            idp = null;
            acr = null;
            amr = null;
            return false;
        }

        if (_upstream.TryGetValue(code, out var t))
        {
            idp = t.idp; acr = t.acr; amr = t.amr; return true;
        }
        idp = null; acr = null; amr = null; return false;
    }

    public void SetMappedClaims(string code, IReadOnlyDictionary<string, string> claims)
    {
        CleanupIfDue();
        _mapped[code] = new System.Collections.Generic.Dictionary<string, string>(claims, System.StringComparer.Ordinal);
        Touch(code);
    }

    public bool TryGetMappedClaims(string code, out IReadOnlyDictionary<string, string> claims)
    {
        CleanupIfDue();
        if (IsExpired(code))
        {
            claims = System.Array.Empty<System.Collections.Generic.KeyValuePair<string, string>>().ToDictionary(k => k.Key, v => v.Value);
            return false;
        }

        if (_mapped.TryGetValue(code, out var dict))
        {
            claims = dict; return true;
        }
        claims = System.Array.Empty<System.Collections.Generic.KeyValuePair<string, string>>().ToDictionary(k => k.Key, v => v.Value);
        return false;
    }

    public void SetSid(string code, string sid)
    {
        CleanupIfDue();
        _sids[code] = sid;
        Touch(code);
    }

    public bool TryGetSid(string code, out string? sid)
    {
        CleanupIfDue();
        if (IsExpired(code))
        {
            sid = null;
            return false;
        }

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
        _expiresAt.TryRemove(code, out _);
    }

    private void Touch(string code)
    {
        _expiresAt[code] = _utcNow().Add(_ttl);
    }

    private bool IsExpired(string code)
    {
        if (_expiresAt.TryGetValue(code, out var expiresAt) && expiresAt > _utcNow())
        {
            return false;
        }

        Remove(code);
        return true;
    }

    private void CleanupIfDue()
    {
        var now = _utcNow();
        var scheduledTicks = System.Threading.Interlocked.Read(ref _nextCleanupTicks);
        if (scheduledTicks > now.UtcTicks)
        {
            return;
        }

        var nextCleanupTicks = now.Add(CleanupInterval).UtcTicks;
        if (System.Threading.Interlocked.CompareExchange(ref _nextCleanupTicks, nextCleanupTicks, scheduledTicks) != scheduledTicks)
        {
            return;
        }

        foreach (var kv in _expiresAt)
        {
            if (kv.Value <= now)
            {
                Remove(kv.Key);
            }
        }
    }
}
