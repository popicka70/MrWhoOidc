using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.KeyGen.Persistence;

namespace MrWhoOidc.KeyGen.Api;

/// <summary>
/// API endpoints for downloading license tokens.
/// </summary>
public static class LicenseDownloadEndpoints
{
    public static void MapLicenseDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/licenses")
            .WithTags("License Downloads");

        group.MapGet("/{tokenId}/download", GetLicenseToken)
            .WithName("GetLicenseToken");
    }

    private static async Task<IResult> GetLicenseToken(
        string tokenId,
        [FromServices] KeyGenDbContext dbContext,
        [FromServices] ILogger<Program> logger)
    {
        try
        {
            // Validate tokenId format
            if (!Guid.TryParse(tokenId, out _))
            {
                logger.LogWarning("Invalid tokenId format: {TokenId}", tokenId);
                return Results.BadRequest(new { error = "Invalid token ID format" });
            }

            // Fetch license token metadata
            var metadata = await dbContext.LicenseTokenMetadata
                .FirstOrDefaultAsync(l => l.TokenId == tokenId);

            if (metadata == null)
            {
                logger.LogWarning("License token not found: {TokenId}", tokenId);
                return Results.NotFound(new { error = "License token not found" });
            }

            // Note: In a real implementation, you would either:
            // 1. Store the JWT token in the database (not recommended for security)
            // 2. Regenerate the token using the stored metadata (requires licensing private key)
            // 
            // For this implementation, we'll return a message indicating that tokens
            // should be downloaded immediately after generation via the page.
            // This follows the same security pattern as private keys.

            logger.LogWarning(
                "Download endpoint called for license token {TokenId} - tokens should be downloaded during generation",
                tokenId);

            return Results.Problem(
                detail: "License tokens are not stored on the server and cannot be retrieved after generation. Please generate a new license token if needed.",
                statusCode: StatusCodes.Status410Gone,
                title: "License Token Not Available");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving license token {TokenId}", tokenId);
            return Results.Problem(
                detail: "An internal error occurred while processing your request.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Internal Server Error");
        }
    }
}
