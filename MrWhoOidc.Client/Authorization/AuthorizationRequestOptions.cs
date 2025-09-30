namespace MrWhoOidc.Client.Authorization;

public sealed class AuthorizationRequestOptions
{
    public string? LoginHint { get; set; }
    public string? Prompt { get; set; }
    public IDictionary<string, string?> AdditionalParameters { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);
    public string? ResponseMode { get; set; }
    public bool? UseJar { get; set; }
    public bool? UseJarm { get; set; }
}
