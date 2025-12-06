using LicensingService.Core.Crypto;
using Microsoft.AspNetCore.Mvc;

namespace LicensingService.Web.Api;

/// <summary>
/// JWKS endpoint for public key discovery.
/// </summary>
public static class JwksEndpoints
{
    public static void MapJwksEndpoints(this IEndpointRouteBuilder routes)
    {
        // JWKS endpoint - allows anyone to validate license tokens
        routes.MapGet("/.well-known/jwks.json", GetJwksAsync)
            .WithName("GetJwks")
            .WithTags("Keys")
            .WithSummary("Returns the JSON Web Key Set for validating license tokens")
            .AllowAnonymous()
            .Produces<JwksResponse>(StatusCodes.Status200OK, "application/json");

        // Discovery document
        routes.MapGet("/.well-known/licensing-configuration", GetConfigurationAsync)
            .WithName("GetLicensingConfiguration")
            .WithTags("Keys")
            .WithSummary("Returns the licensing service configuration")
            .AllowAnonymous()
            .Produces<LicensingConfigurationResponse>(StatusCodes.Status200OK, "application/json");
    }

    private static async Task<IResult> GetJwksAsync(
        ISigningKeyService signingKeyService,
        CancellationToken cancellationToken)
    {
        var publicKeys = await signingKeyService.GetPublicKeysAsync(cancellationToken);
        var jwksJson = JwkSerializer.SerializeToJwks(publicKeys);
        return Results.Text(jwksJson, contentType: "application/json");
    }

    private static IResult GetConfigurationAsync(
        [FromServices] IConfiguration configuration,
        HttpContext httpContext)
    {
        var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        var issuer = configuration["Licensing:Issuer"] ?? "LicensingService";

        var response = new LicensingConfigurationResponse
        {
            Issuer = issuer,
            JwksUri = $"{baseUrl}/.well-known/jwks.json",
            LicenseIssuanceEndpoint = $"{baseUrl}/api/licenses",
            TokenValidationEndpoint = $"{baseUrl}/api/licenses/validate",
            SupportedAlgorithms = ["ES256"],
            SupportedClaims = ["jti", "iss", "sub", "aud", "nbf", "iat", "exp", "tier", "scope", "options"]
        };

        return Results.Json(response, contentType: "application/json");
    }
}

/// <summary>
/// JWKS response format.
/// </summary>
public class JwksResponse
{
    public required IReadOnlyList<object> Keys { get; init; }
}

/// <summary>
/// Licensing configuration response.
/// </summary>
public class LicensingConfigurationResponse
{
    public required string Issuer { get; init; }
    public required string JwksUri { get; init; }
    public required string LicenseIssuanceEndpoint { get; init; }
    public required string TokenValidationEndpoint { get; init; }
    public required IReadOnlyList<string> SupportedAlgorithms { get; init; }
    public required IReadOnlyList<string> SupportedClaims { get; init; }
}
