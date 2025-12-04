using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using LicensingService.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LicensingService.Web.Api;

/// <summary>
/// API endpoints for product management.
/// </summary>
public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/products")
            .WithTags("Products")
            .RequireAuthorization();

        // GET /products
        group.MapGet("/", async (
            [FromQuery] string? status,
            [FromServices] IProductStore productStore,
            CancellationToken cancellationToken) =>
        {
            var products = await productStore.GetAllAsync(status, cancellationToken);
            return Results.Ok(new ProductListResponse
            {
                Items = products.Select(ProductResponse.FromEntity).ToList(),
                TotalCount = products.Count
            });
        })
        .WithName("ListProducts")
        .WithSummary("List licensed products");

        // GET /products/{id}
        group.MapGet("/{id:guid}", async (
            Guid id,
            [FromServices] IProductStore productStore,
            CancellationToken cancellationToken) =>
        {
            var product = await productStore.GetByIdAsync(id, cancellationToken);
            if (product == null)
            {
                return Results.NotFound(new { Error = "Product not found" });
            }

            return Results.Ok(ProductWithOptionsResponse.FromEntity(product));
        })
        .WithName("GetProduct")
        .WithSummary("Get product details with option definitions");

        // POST /products
        group.MapPost("/", async (
            [FromBody] CreateProductRequest request,
            [FromServices] IProductStore productStore,
            CancellationToken cancellationToken) =>
        {
            // Check for duplicate identifier
            var existing = await productStore.GetByIdentifierAsync(request.Identifier, cancellationToken);
            if (existing != null)
            {
                return Results.Conflict(new { Error = "Product with this identifier already exists" });
            }

            var product = new LicensedProduct
            {
                Identifier = request.Identifier,
                DisplayName = request.DisplayName,
                Description = request.Description
            };

            var created = await productStore.CreateAsync(product, cancellationToken);
            return Results.Created($"/api/v1/products/{created.Id}", ProductResponse.FromEntity(created));
        })
        .WithName("CreateProduct")
        .WithSummary("Create licensed product");

        // PUT /products/{id}
        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateProductRequest request,
            [FromServices] IProductStore productStore,
            CancellationToken cancellationToken) =>
        {
            var product = await productStore.GetByIdAsync(id, cancellationToken);
            if (product == null)
            {
                return Results.NotFound(new { Error = "Product not found" });
            }

            product.DisplayName = request.DisplayName;
            product.Description = request.Description;

            var updated = await productStore.UpdateAsync(product, cancellationToken);
            return Results.Ok(ProductResponse.FromEntity(updated));
        })
        .WithName("UpdateProduct")
        .WithSummary("Update product");

        // DELETE /products/{id}
        group.MapDelete("/{id:guid}", async (
            Guid id,
            [FromServices] IProductStore productStore,
            CancellationToken cancellationToken) =>
        {
            var product = await productStore.GetByIdAsync(id, cancellationToken);
            if (product == null)
            {
                return Results.NotFound(new { Error = "Product not found" });
            }

            var success = await productStore.SoftDeleteAsync(id, cancellationToken);
            if (!success)
            {
                return Results.BadRequest(new { Error = "Cannot delete product with existing licenses" });
            }

            return Results.NoContent();
        })
        .WithName("DeleteProduct")
        .WithSummary("Soft-delete product");

        // Option definition endpoints
        MapProductOptionEndpoints(group);
    }

    private static void MapProductOptionEndpoints(RouteGroupBuilder group)
    {
        // POST /products/{id}/options
        group.MapPost("/{productId:guid}/options", async (
            Guid productId,
            [FromBody] CreateOptionDefinitionRequest request,
            [FromServices] IProductStore productStore,
            CancellationToken cancellationToken) =>
        {
            var product = await productStore.GetByIdAsync(productId, cancellationToken);
            if (product == null)
            {
                return Results.NotFound(new { Error = "Product not found" });
            }

            // Check for duplicate option key
            if (product.OptionDefinitions.Any(o => o.OptionKey == request.OptionKey))
            {
                return Results.Conflict(new { Error = "Option with this key already exists" });
            }

            var optionDef = new ProductOptionDefinition
            {
                ProductId = productId,
                OptionKey = request.OptionKey,
                DisplayName = request.DisplayName,
                DataType = request.DataType,
                DefaultValue = request.DefaultValue,
                Description = request.Description,
                SortOrder = request.SortOrder
            };

            var created = await productStore.AddOptionDefinitionAsync(optionDef, cancellationToken);
            return Results.Created(
                $"/api/v1/products/{productId}/options/{created.Id}",
                OptionDefinitionResponse.FromEntity(created));
        })
        .WithName("CreateOptionDefinition")
        .WithSummary("Add option definition to product");

        // PUT /products/{productId}/options/{optionId}
        group.MapPut("/{productId:guid}/options/{optionId:guid}", async (
            Guid productId,
            Guid optionId,
            [FromBody] UpdateOptionDefinitionRequest request,
            [FromServices] IProductStore productStore,
            [FromServices] LicensingService.Core.Persistence.LicensingDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var optionDef = await dbContext.ProductOptionDefinitions
                .FirstOrDefaultAsync(o => o.Id == optionId && o.ProductId == productId, cancellationToken);

            if (optionDef == null)
            {
                return Results.NotFound(new { Error = "Option definition not found" });
            }

            optionDef.DisplayName = request.DisplayName;
            optionDef.DefaultValue = request.DefaultValue;
            optionDef.Description = request.Description;
            optionDef.SortOrder = request.SortOrder;

            var updated = await productStore.UpdateOptionDefinitionAsync(optionDef, cancellationToken);
            return Results.Ok(OptionDefinitionResponse.FromEntity(updated));
        })
        .WithName("UpdateOptionDefinition")
        .WithSummary("Update option definition");

        // DELETE /products/{productId}/options/{optionId}
        group.MapDelete("/{productId:guid}/options/{optionId:guid}", async (
            Guid productId,
            Guid optionId,
            [FromServices] IProductStore productStore,
            CancellationToken cancellationToken) =>
        {
            var success = await productStore.RemoveOptionDefinitionAsync(productId, optionId, cancellationToken);
            if (!success)
            {
                return Results.BadRequest(new { Error = "Cannot delete option: option not found or is in use" });
            }

            return Results.NoContent();
        })
        .WithName("DeleteOptionDefinition")
        .WithSummary("Remove option definition");
    }
}
