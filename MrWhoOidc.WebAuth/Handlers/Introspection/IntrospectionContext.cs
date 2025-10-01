using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Context for an introspection operation.
/// </summary>
public sealed class IntrospectionContext
{
    public required IntrospectionRequest Request { get; init; }
    public required Client Client { get; init; }
    public required string Issuer { get; init; }
    public required string Endpoint { get; init; }
    public required HttpContext HttpContext { get; init; }
    public required string ClientBucket { get; init; }
    public required KeyValuePair<string, object?>[] MetricTags { get; init; }
}
