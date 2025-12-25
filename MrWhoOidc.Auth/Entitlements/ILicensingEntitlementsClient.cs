using MrWhoOidc.Auth.Entitlements.Contracts;

namespace MrWhoOidc.Auth.Entitlements;

public interface ILicensingEntitlementsClient
{
    Task<EffectiveEntitlementsResponse> ResolveEffectiveEntitlementsAsync(
        EffectiveEntitlementsRequest request,
        string issuer,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a signed license token from LicensingService for embedding in access tokens.
    /// </summary>
    /// <param name="request">The signed license token request.</param>
    /// <param name="issuer">The issuer URL for service-to-service authentication.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the signed token or error information.</returns>
    Task<SignedLicenseTokenResult> GetSignedLicenseTokenAsync(
        SignedLicenseTokenRequest request,
        string issuer,
        CancellationToken cancellationToken = default);
}
