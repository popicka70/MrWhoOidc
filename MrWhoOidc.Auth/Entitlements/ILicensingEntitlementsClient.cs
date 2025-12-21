using MrWhoOidc.Auth.Entitlements.Contracts;

namespace MrWhoOidc.Auth.Entitlements;

public interface ILicensingEntitlementsClient
{
    Task<EffectiveEntitlementsResponse> ResolveEffectiveEntitlementsAsync(
        EffectiveEntitlementsRequest request,
        string issuer,
        CancellationToken cancellationToken = default);
}
