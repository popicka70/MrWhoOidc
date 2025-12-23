using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Entitlements.Contracts;

public sealed class EffectiveEntitlementsRequest
{
    [JsonPropertyName("products")]
    public required string[] Products { get; init; }

    [JsonPropertyName("subject")]
    public required SubjectContext Subject { get; init; }

    [JsonPropertyName("tenant")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TenantContext? Tenant { get; init; }
}

public sealed class SubjectContext
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

public sealed class TenantContext
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

public sealed class EffectiveEntitlementsResponse
{
    [JsonPropertyName("entitlements")]
    public required Dictionary<string, Entitlement> Entitlements { get; init; }
}

public sealed class Entitlement
{
    [JsonPropertyName("tier")]
    public int Tier { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("licenseId")]
    public required string LicenseId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}
