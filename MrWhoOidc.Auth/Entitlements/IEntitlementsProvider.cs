using MrWhoOidc.Auth.Entitlements.Contracts;

namespace MrWhoOidc.Auth.Entitlements;

public interface IEntitlementsProvider
{
    Task<IReadOnlyDictionary<string, Entitlement>> GetEffectiveEntitlementsAsync(
        string subjectId,
        string? tenantId,
        IReadOnlyCollection<string> productKeys,
        string issuer,
        CancellationToken cancellationToken = default);
}

public sealed class NoopEntitlementsProvider : IEntitlementsProvider
{
    public Task<IReadOnlyDictionary<string, Entitlement>> GetEffectiveEntitlementsAsync(
        string subjectId,
        string? tenantId,
        IReadOnlyCollection<string> productKeys,
        string issuer,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, Entitlement> empty = new Dictionary<string, Entitlement>(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(empty);
    }
}
