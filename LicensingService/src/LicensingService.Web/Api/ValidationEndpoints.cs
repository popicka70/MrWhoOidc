using LicensingService.Core.Services;
using LicensingService.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace LicensingService.Web.Api;

/// <summary>
/// License validation endpoints.
/// </summary>
public static class ValidationEndpoints
{
    public static void MapValidationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/validate")
            .WithTags("Validation")
            .WithOpenApi();

        // POST /api/validate - Validate a license token
        group.MapPost("/", ValidateLicenseAsync)
            .WithName("ValidateLicense")
            .WithSummary("Validate a license token")
            .WithDescription("Validates a license token's signature, expiry, and optionally checks database for revocation status.");

        // POST /api/validate/product/{productId} - Validate for specific product
        group.MapPost("/product/{productId}", ValidateForProductAsync)
            .WithName("ValidateLicenseForProduct")
            .WithSummary("Validate a license token for a specific product")
            .WithDescription("Validates a license token and verifies it's intended for the specified product.");

        // Introspection endpoint (RFC 7662 style)
        var introspect = routes.MapGroup("")
            .WithTags("Validation")
            .WithOpenApi();

        introspect.MapPost("/introspect", IntrospectAsync)
            .WithName("IntrospectLicense")
            .WithSummary("Introspect a license token (RFC 7662)")
            .WithDescription("Returns token metadata in RFC 7662 format. Always checks database for revocation.");
    }

    private static async Task<IResult> ValidateLicenseAsync(
        [FromBody] ValidateLicenseRequest request,
        [FromServices] ILicenseValidationService validationService,
        CancellationToken cancellationToken)
    {
        var result = await validationService.ValidateAsync(request.Token, request.CheckDatabase, cancellationToken);

        return Results.Ok(MapToResponse(result));
    }

    private static async Task<IResult> ValidateForProductAsync(
        string productId,
        [FromBody] ValidateLicenseRequest request,
        [FromServices] ILicenseValidationService validationService,
        CancellationToken cancellationToken)
    {
        var result = await validationService.ValidateForProductAsync(
            request.Token,
            productId,
            request.CheckDatabase,
            cancellationToken);

        return Results.Ok(MapToResponse(result));
    }

    private static async Task<IResult> IntrospectAsync(
        [FromBody] IntrospectRequest request,
        [FromServices] ILicenseValidationService validationService,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Introspection always checks database
        var result = await validationService.ValidateAsync(request.Token, checkDatabase: true, cancellationToken);

        if (!result.IsValid || result.IsRevoked)
        {
            // Per RFC 7662: if token is invalid or revoked, return { "active": false }
            return Results.Ok(new IntrospectResponse { Active = false });
        }

        var issuer = configuration["Licensing:Issuer"] ?? "LicensingService";

        return Results.Ok(new IntrospectResponse
        {
            Active = result.IsActive,
            Sub = result.CustomerIdentifier,
            Aud = result.ProductIdentifier,
            Iss = issuer,
            Jti = result.TokenId,
            Exp = result.ValidUntil?.ToUnixTimeSeconds() ?? 0,
            Nbf = result.ValidFrom?.ToUnixTimeSeconds() ?? 0,
            Iat = result.ValidFrom?.ToUnixTimeSeconds() ?? 0, // Using nbf as iat
            TokenType = "license_token",
            Tier = result.Tier,
            Scope = result.Scope
        });
    }

    private static ValidateLicenseResponse MapToResponse(LicenseValidationResult result)
    {
        if (!result.IsValid)
        {
            return new ValidateLicenseResponse
            {
                IsValid = false,
                IsActive = false,
                Error = result.Error,
                ErrorCode = result.ErrorCode
            };
        }

        return new ValidateLicenseResponse
        {
            IsValid = true,
            IsActive = result.IsActive,
            License = new ValidatedLicenseInfo
            {
                TokenId = result.TokenId!,
                CustomerIdentifier = result.CustomerIdentifier!,
                ProductIdentifier = result.ProductIdentifier!,
                Tier = result.Tier!,
                Scope = result.Scope!,
                ValidFrom = result.ValidFrom!.Value,
                ValidUntil = result.ValidUntil!.Value,
                DaysUntilExpiry = result.DaysUntilExpiry!.Value,
                Options = result.Options,
                DatabaseStatus = result.DatabaseStatus,
                IsRevoked = result.IsRevoked
            }
        };
    }
}
