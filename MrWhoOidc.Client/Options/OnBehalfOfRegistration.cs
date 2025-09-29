using System;
using System.Collections.Generic;

namespace MrWhoOidc.Client.Options;

public sealed class OnBehalfOfRegistration
{
    public string? Audience { get; set; }
    public string? Resource { get; set; }
    public string? Scope { get; set; }
    public string SubjectTokenType { get; set; } = "urn:ietf:params:oauth:token-type:access_token";
    public string? RequestedTokenType { get; set; }
    public TimeSpan? CacheLifetime { get; set; }
    public IDictionary<string, string?> AdditionalParameters { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);
}
