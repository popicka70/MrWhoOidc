using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// The effective set of JWS algorithms this OP accepts for signed request objects. One source of truth
/// for discovery (<c>request_object_signing_alg_values_supported</c>), dynamic client registration
/// validation of <c>request_object_signing_alg</c>, and request object signature validation.
/// </summary>
public static class RequestObjectAlgorithms
{
    public static string[] Effective(AuthOptions options)
    {
        return (options.RequestObjectAllowedAlgorithms is { Length: > 0 }
                ? options.RequestObjectAllowedAlgorithms
                : new[]
                {
                    SecurityConstants.JwtAlgorithms.RS256,
                    SecurityConstants.JwtAlgorithms.PS256,
                    SecurityConstants.JwtAlgorithms.ES256,
                    SecurityConstants.JwtAlgorithms.ES384,
                    SecurityConstants.JwtAlgorithms.ES512
                })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a)
            .ToArray();
    }
}
