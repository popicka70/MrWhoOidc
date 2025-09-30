using System;
using System.Collections.Generic;
using System.Linq;

namespace MrWhoOidc.Client.Options;

public sealed class ClientCredentialsRegistration
{
    private List<string> _scopes = new();

    public string? Audience { get; set; }
    public string? Resource { get; set; }

    public IReadOnlyList<string> Scopes
    {
        get => _scopes;
        set => _scopes = value?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
    }

    public TimeSpan? CacheLifetime { get; set; }

    public IDictionary<string, string?> AdditionalParameters { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);
}
