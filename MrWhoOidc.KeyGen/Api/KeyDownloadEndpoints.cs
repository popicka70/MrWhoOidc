using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.KeyGen.Domain.Cryptography;
using MrWhoOidc.KeyGen.Domain.Models;
using MrWhoOidc.KeyGen.Persistence;

namespace MrWhoOidc.KeyGen.Api;

/// <summary>
/// API endpoints for downloading key pairs.
/// </summary>
public static class KeyDownloadEndpoints
{
    /// <summary>
    /// Registers key download endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapKeyDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/keys");

        group.MapGet("/{kid}/private", GetPrivateKey)
            .WithName("GetPrivateKey")
            .WithDescription("Download private key in JWK format")
            .Produces<string>(StatusCodes.Status200OK, "application/json")
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{kid}/public", GetPublicKey)
            .WithName("GetPublicKey")
            .WithDescription("Download public key in JWKS format")
            .Produces<string>(StatusCodes.Status200OK, "application/json")
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetPrivateKey(
        string kid,
        [FromServices] KeyGenDbContext context,
        HttpContext httpContext)
    {
        // SECURITY: Private keys are not stored and cannot be retrieved after generation.
        // This endpoint exists for the immediate post-generation download only.
        // In a production system, you would use a short-lived token or session-based approach.
        
        return Results.Problem(
            detail: "Private keys are not stored and cannot be retrieved after generation. Please generate a new key pair if you need a private key.",
            statusCode: StatusCodes.Status410Gone,
            title: "Private Key Not Available");
    }

    private static async Task<IResult> GetPublicKey(
        string kid,
        [FromServices] KeyGenDbContext context,
        HttpContext httpContext)
    {
        try
        {
            // Fetch key metadata
            var metadata = await context.KeyPairMetadata
                .FirstOrDefaultAsync(k => k.Kid == kid);

            if (metadata == null)
            {
                return Results.NotFound(new { error = "key_not_found", message = $"Key with kid '{kid}' not found" });
            }

            // Check if key is revoked
            if (metadata.Status == "Revoked")
            {
                return Results.Problem(
                    detail: $"Key '{kid}' has been revoked and can no longer be downloaded.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Key Revoked");
            }

            // Record download
            var downloadRecord = new KeyDownloadRecord
            {
                Id = GuidHelper.NewId(),
                KeyPairMetadataId = metadata.Id,
                DownloadType = "PublicKey",
                DownloadedAt = DateTimeOffset.UtcNow,
                DownloadedBy = null, // TODO: Add authentication
                IpAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = httpContext.Request.Headers.UserAgent.ToString()
            };

            context.KeyDownloadRecords.Add(downloadRecord);
            await context.SaveChangesAsync();

            // Return public key with download header
            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(metadata.PublicKeyJwks),
                contentType: "application/json",
                fileDownloadName: $"public-key-{kid}.json");
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Internal Server Error");
        }
    }
}
