namespace LicensingService.Core.Services;

/// <summary>
/// Request to generate a license token.
/// </summary>
public class GenerateLicenseTokenRequest
{
    /// <summary>Customer identifier (sub claim).</summary>
    public required string CustomerIdentifier { get; init; }

    /// <summary>Product identifier (aud claim).</summary>
    public required string ProductIdentifier { get; init; }

    /// <summary>License tier.</summary>
    public required string Tier { get; init; }

    /// <summary>License scope.</summary>
    public required string Scope { get; init; }

    /// <summary>Not-before date.</summary>
    public required DateTimeOffset ValidFrom { get; init; }

    /// <summary>Expiration date.</summary>
    public required DateTimeOffset ValidUntil { get; init; }

    /// <summary>Product options as dictionary.</summary>
    public Dictionary<string, object>? Options { get; init; }
}

/// <summary>
/// Result of license token generation.
/// </summary>
public class GenerateLicenseTokenResult
{
    /// <summary>JWT token ID (jti claim).</summary>
    public required string TokenId { get; init; }

    /// <summary>Signed JWT token.</summary>
    public required string Token { get; init; }

    /// <summary>Key ID used for signing.</summary>
    public required string Kid { get; init; }
}

/// <summary>
/// Service for generating signed license tokens.
/// </summary>
public interface ILicenseTokenGenerator
{
    /// <summary>
    /// Generates a signed license JWT token.
    /// </summary>
    Task<GenerateLicenseTokenResult> GenerateAsync(GenerateLicenseTokenRequest request, CancellationToken cancellationToken = default);
}
