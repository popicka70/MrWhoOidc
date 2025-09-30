using System;
using System.Collections.Generic;

namespace MrWhoOidc.Client.Tokens;

public sealed class ClientCredentialsRequest
{
    public IReadOnlyList<string>? Scopes { get; set; }
    public string? Audience { get; set; }
    public string? Resource { get; set; }
    public IDictionary<string, string?> AdditionalParameters { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);
}
