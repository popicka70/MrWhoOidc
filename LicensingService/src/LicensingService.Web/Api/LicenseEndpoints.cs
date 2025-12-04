using System.Text.Json;
using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using LicensingService.Web.Models;
using LicensingService.Web.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace LicensingService.Web.Api;

/// <summary>
/// License management API endpoints.
/// </summary>
public static class LicenseEndpoints
{
    public static void MapLicenseEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/licenses")
            .WithTags("Licenses")
            .RequireAuthorization();

        // Issue new license
        group.MapPost("/", IssueLicenseAsync)
            .WithName("IssueLicense")
            .WithSummary("Issues a new license for a customer and product");

        // Get license by ID
        group.MapGet("/{id:guid}", GetLicenseByIdAsync)
            .WithName("GetLicenseById")
            .WithSummary("Gets a license by ID");

        // Get license by token ID
        group.MapGet("/by-token/{tokenId}", GetLicenseByTokenIdAsync)
            .WithName("GetLicenseByTokenId")
            .WithSummary("Gets a license by its JWT token ID (jti)");

        // Search licenses
        group.MapPost("/search", SearchLicensesAsync)
            .WithName("SearchLicenses")
            .WithSummary("Searches licenses with filters and pagination");

        // Get licenses for customer
        group.MapGet("/customer/{customerId:guid}", GetLicensesByCustomerAsync)
            .WithName("GetLicensesByCustomer")
            .WithSummary("Gets all licenses for a customer");

        // Get licenses for product
        group.MapGet("/product/{productId:guid}", GetLicensesByProductAsync)
            .WithName("GetLicensesByProduct")
            .WithSummary("Gets all licenses for a product");

        // Get expiring licenses
        group.MapGet("/expiring", GetExpiringLicensesAsync)
            .WithName("GetExpiringLicenses")
            .WithSummary("Gets licenses expiring within a specified number of days");

        // Renew license
        group.MapPost("/{id:guid}/renew", RenewLicenseAsync)
            .WithName("RenewLicense")
            .WithSummary("Renews an existing license with optional overlap period");

        // Revoke license
        group.MapPost("/{id:guid}/revoke", RevokeLicenseAsync)
            .WithName("RevokeLicense")
            .WithSummary("Revokes a license");

        // Get license events/history
        group.MapGet("/{id:guid}/events", GetLicenseEventsAsync)
            .WithName("GetLicenseEvents")
            .WithSummary("Gets the event history for a license");

        // Download license token
        group.MapGet("/{id:guid}/token", DownloadLicenseTokenAsync)
            .WithName("DownloadLicenseToken")
            .WithSummary("Downloads the signed license token");
    }

    private static async Task<Results<Created<LicenseWithTokenResponse>, BadRequest<string>, NotFound<string>>> IssueLicenseAsync(
        [FromBody] IssueLicenseRequest request,
        ILicenseStore licenseStore,
        ICustomerStore customerStore,
        IProductStore productStore,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        // Validate customer exists
        var customer = await customerStore.GetByIdAsync(request.CustomerId, cancellationToken);
        if (customer == null)
        {
            return TypedResults.NotFound($"Customer {request.CustomerId} not found");
        }

        // Validate product exists
        var product = await productStore.GetByIdAsync(request.ProductId, cancellationToken);
        if (product == null)
        {
            return TypedResults.NotFound($"Product {request.ProductId} not found");
        }

        // Validate tier
        if (string.IsNullOrWhiteSpace(request.Tier))
        {
            return TypedResults.BadRequest("Tier is required");
        }

        var validFrom = request.ValidFrom ?? DateTimeOffset.UtcNow;
        var validUntil = request.ValidUntil ?? validFrom.AddYears(1);

        if (validUntil <= validFrom)
        {
            return TypedResults.BadRequest("ValidUntil must be after ValidFrom");
        }

        var license = new License
        {
            CustomerId = customer.Id,
            Customer = customer,
            ProductId = product.Id,
            Product = product,
            Tier = request.Tier,
            Scope = request.Scope,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Options = request.Options != null ? JsonSerializer.Serialize(request.Options) : null
        };

        var createdLicense = await licenseStore.CreateAsync(license, currentUser.UserId ?? "system", cancellationToken);

        var response = MapToWithTokenResponse(createdLicense);
        return TypedResults.Created($"/api/licenses/{createdLicense.Id}", response);
    }

    private static async Task<Results<Ok<LicenseResponse>, NotFound>> GetLicenseByIdAsync(
        Guid id,
        ILicenseStore licenseStore,
        CancellationToken cancellationToken)
    {
        var license = await licenseStore.GetByIdAsync(id, cancellationToken);
        if (license == null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(MapToResponse(license));
    }

    private static async Task<Results<Ok<LicenseResponse>, NotFound>> GetLicenseByTokenIdAsync(
        string tokenId,
        ILicenseStore licenseStore,
        CancellationToken cancellationToken)
    {
        var license = await licenseStore.GetByTokenIdAsync(tokenId, cancellationToken);
        if (license == null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(MapToResponse(license));
    }

    private static async Task<Ok<LicenseSearchResponse>> SearchLicensesAsync(
        [FromBody] LicenseSearchRequest request,
        ILicenseStore licenseStore,
        CancellationToken cancellationToken)
    {
        LicenseStatus? status = null;
        if (!string.IsNullOrEmpty(request.Status) && Enum.TryParse<LicenseStatus>(request.Status, true, out var parsedStatus))
        {
            status = parsedStatus;
        }

        var criteria = new LicenseSearchCriteria
        {
            CustomerId = request.CustomerId,
            ProductId = request.ProductId,
            Status = status,
            Tier = request.Tier,
            ValidAt = request.ValidAt,
            SearchText = request.SearchText
        };

        var (items, totalCount) = await licenseStore.SearchAsync(
            criteria,
            request.Skip,
            Math.Min(request.Take, 100),
            cancellationToken);

        var response = new LicenseSearchResponse
        {
            Items = items.Select(MapToResponse).ToList(),
            TotalCount = totalCount,
            Skip = request.Skip,
            Take = request.Take
        };

        return TypedResults.Ok(response);
    }

    private static async Task<Ok<IReadOnlyList<LicenseResponse>>> GetLicensesByCustomerAsync(
        Guid customerId,
        [FromQuery] string? status,
        ILicenseStore licenseStore,
        CancellationToken cancellationToken)
    {
        LicenseStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<LicenseStatus>(status, true, out var parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        var licenses = await licenseStore.GetByCustomerIdAsync(customerId, statusFilter, cancellationToken);
        return TypedResults.Ok<IReadOnlyList<LicenseResponse>>(licenses.Select(MapToResponse).ToList());
    }

    private static async Task<Ok<IReadOnlyList<LicenseResponse>>> GetLicensesByProductAsync(
        Guid productId,
        [FromQuery] string? status,
        ILicenseStore licenseStore,
        CancellationToken cancellationToken)
    {
        LicenseStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<LicenseStatus>(status, true, out var parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        var licenses = await licenseStore.GetByProductIdAsync(productId, statusFilter, cancellationToken);
        return TypedResults.Ok<IReadOnlyList<LicenseResponse>>(licenses.Select(MapToResponse).ToList());
    }

    private static async Task<Ok<IReadOnlyList<LicenseResponse>>> GetExpiringLicensesAsync(
        [FromQuery] int days,
        ILicenseStore licenseStore,
        CancellationToken cancellationToken)
    {
        var daysToCheck = days > 0 ? days : 30;
        var licenses = await licenseStore.GetExpiringLicensesAsync(daysToCheck, cancellationToken);
        return TypedResults.Ok<IReadOnlyList<LicenseResponse>>(licenses.Select(MapToResponse).ToList());
    }

    private static async Task<Results<Ok<LicenseWithTokenResponse>, NotFound, BadRequest<string>>> RenewLicenseAsync(
        Guid id,
        [FromBody] RenewLicenseRequest request,
        Core.Services.ILicenseService licenseService,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var renewRequest = new Core.Services.RenewLicenseRequest
        {
            LicenseId = id,
            NewValidUntil = request.ValidUntil,
            OptionUpdates = request.OptionUpdates
        };

        var result = await licenseService.RenewLicenseAsync(
            renewRequest,
            currentUser.UserId ?? "system",
            cancellationToken);

        if (!result.Success)
        {
            if (result.ErrorCode == "license_not_found")
            {
                return TypedResults.NotFound();
            }
            return TypedResults.BadRequest(result.Error ?? "Renewal failed");
        }

        return TypedResults.Ok(MapToWithTokenResponse(result.License!));
    }

    private static async Task<Results<Ok<LicenseResponse>, NotFound, BadRequest<string>>> RevokeLicenseAsync(
        Guid id,
        [FromBody] RevokeLicenseRequest request,
        Core.Services.ILicenseService licenseService,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var revokeRequest = new Core.Services.RevokeLicenseRequest
        {
            LicenseId = id,
            Reason = request.Reason
        };

        var result = await licenseService.RevokeLicenseAsync(
            revokeRequest,
            currentUser.UserId ?? "system",
            cancellationToken);

        if (!result.Success)
        {
            if (result.ErrorCode == "license_not_found")
            {
                return TypedResults.NotFound();
            }
            return TypedResults.BadRequest(result.Error ?? "Revocation failed");
        }

        return TypedResults.Ok(MapToResponse(result.License!));
    }

    private static async Task<Results<Ok<IReadOnlyList<LicenseEventResponse>>, NotFound>> GetLicenseEventsAsync(
        Guid id,
        ILicenseStore licenseStore,
        CancellationToken cancellationToken)
    {
        var license = await licenseStore.GetByIdAsync(id, cancellationToken);
        if (license == null)
        {
            return TypedResults.NotFound();
        }

        var events = await licenseStore.GetEventsAsync(id, cancellationToken);
        var response = events.Select(e => new LicenseEventResponse
        {
            Id = e.Id,
            LicenseId = e.LicenseId,
            EventType = e.EventType.ToString(),
            PerformedBy = e.Actor,
            PerformedAt = e.Timestamp,
            Details = e.Details
        }).ToList();

        return TypedResults.Ok<IReadOnlyList<LicenseEventResponse>>(response);
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> DownloadLicenseTokenAsync(
        Guid id,
        ILicenseStore licenseStore,
        CancellationToken cancellationToken)
    {
        var license = await licenseStore.GetByIdAsync(id, cancellationToken);
        if (license == null)
        {
            return TypedResults.NotFound();
        }

        var tokenBytes = System.Text.Encoding.UTF8.GetBytes(license.SignedToken);
        return TypedResults.File(tokenBytes, "application/jwt", $"license-{license.TokenId}.jwt");
    }

    private static LicenseResponse MapToResponse(License license)
    {
        Dictionary<string, object>? options = null;
        if (!string.IsNullOrEmpty(license.Options))
        {
            options = JsonSerializer.Deserialize<Dictionary<string, object>>(license.Options);
        }

        return new LicenseResponse
        {
            Id = license.Id,
            TokenId = license.TokenId,
            CustomerId = license.CustomerId,
            CustomerName = license.Customer?.DisplayName,
            CustomerIdentifier = license.Customer?.Identifier,
            ProductId = license.ProductId,
            ProductName = license.Product?.DisplayName,
            ProductIdentifier = license.Product?.Identifier,
            Tier = license.Tier,
            Scope = license.Scope,
            Status = license.Status.ToString(),
            ValidFrom = license.ValidFrom,
            ValidUntil = license.ValidUntil,
            Options = options,
            CreatedAt = license.CreatedAt,
            RevokedAt = license.RevokedAt,
            RevocationReason = license.RevocationReason,
            RenewedFromId = license.ParentLicenseId
        };
    }

    private static LicenseWithTokenResponse MapToWithTokenResponse(License license)
    {
        Dictionary<string, object>? options = null;
        if (!string.IsNullOrEmpty(license.Options))
        {
            options = JsonSerializer.Deserialize<Dictionary<string, object>>(license.Options);
        }

        return new LicenseWithTokenResponse
        {
            Id = license.Id,
            TokenId = license.TokenId,
            CustomerId = license.CustomerId,
            CustomerName = license.Customer?.DisplayName,
            CustomerIdentifier = license.Customer?.Identifier,
            ProductId = license.ProductId,
            ProductName = license.Product?.DisplayName,
            ProductIdentifier = license.Product?.Identifier,
            Tier = license.Tier,
            Scope = license.Scope,
            Status = license.Status.ToString(),
            ValidFrom = license.ValidFrom,
            ValidUntil = license.ValidUntil,
            Options = options,
            CreatedAt = license.CreatedAt,
            RevokedAt = license.RevokedAt,
            RevocationReason = license.RevocationReason,
            RenewedFromId = license.ParentLicenseId,
            SignedToken = license.SignedToken
        };
    }
}
