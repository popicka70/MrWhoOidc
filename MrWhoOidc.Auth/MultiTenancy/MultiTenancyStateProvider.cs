using System;

namespace MrWhoOidc.Auth.MultiTenancy;

public interface IMultiTenancyStateProvider
{
    bool IsEnabled { get; }
    string DefaultTenantSlug { get; }
    void UpdateState(bool enabled);
}

public class MultiTenancyStateProvider : IMultiTenancyStateProvider, IMultiTenancyOptions
{
    private volatile bool _enabled;
    private readonly string _defaultTenantSlug;

    public MultiTenancyStateProvider(string defaultTenantSlug, bool initialEnabled)
    {
        _defaultTenantSlug = defaultTenantSlug;
        _enabled = initialEnabled;
    }

    public bool IsEnabled => _enabled;
    public bool Enabled => _enabled;
    public string DefaultTenantSlug => _defaultTenantSlug;

    public void UpdateState(bool enabled)
    {
        _enabled = enabled;
    }
}
