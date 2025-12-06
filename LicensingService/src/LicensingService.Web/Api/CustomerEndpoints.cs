using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using LicensingService.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace LicensingService.Web.Api;

/// <summary>
/// API endpoints for customer management.
/// </summary>
public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/customers")
            .WithTags("Customers")
            .RequireAuthorization();

        // GET /customers
        group.MapGet("/", async (
            [FromServices] ICustomerStore customerStore,
            [FromQuery] string? search,
            [FromQuery] string? status,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            CancellationToken cancellationToken) =>
        {
            // Clamp page size
            var actualPageSize = Math.Clamp(pageSize ?? 20, 1, 100);
            var actualPage = Math.Max(1, page ?? 1);

            var (customers, totalCount) = await customerStore.GetAllAsync(search, status, actualPage, actualPageSize, cancellationToken);
            
            return Results.Ok(new CustomerListResponse
            {
                Items = customers.Select(CustomerResponse.FromEntity).ToList(),
                TotalCount = totalCount,
                Page = actualPage,
                PageSize = actualPageSize
            });
        })
        .WithName("ListCustomers")
        .WithSummary("List customers");

        // GET /customers/{id}
        group.MapGet("/{id:guid}", async (
            Guid id,
            [FromServices] ICustomerStore customerStore,
            CancellationToken cancellationToken) =>
        {
            var customer = await customerStore.GetByIdAsync(id, cancellationToken);
            if (customer == null)
            {
                return Results.NotFound(new { Error = "Customer not found" });
            }

            var licenseCount = await customerStore.GetLicenseCountAsync(id, cancellationToken);

            return Results.Ok(new CustomerWithLicenseCountResponse
            {
                Id = customer.Id,
                Identifier = customer.Identifier,
                DisplayName = customer.DisplayName,
                ContactEmail = customer.ContactEmail,
                ContactName = customer.ContactName,
                Status = customer.Status,
                CreatedAt = customer.CreatedAt,
                UpdatedAt = customer.UpdatedAt,
                LicenseCount = licenseCount
            });
        })
        .WithName("GetCustomer")
        .WithSummary("Get customer details");

        // POST /customers
        group.MapPost("/", async (
            [FromBody] CreateCustomerRequest request,
            [FromServices] ICustomerStore customerStore,
            CancellationToken cancellationToken) =>
        {
            // Check for duplicate identifier
            var existing = await customerStore.GetByIdentifierAsync(request.Identifier, cancellationToken);
            if (existing != null)
            {
                return Results.Conflict(new { Error = "Customer with this identifier already exists" });
            }

            var customer = new Customer
            {
                Identifier = request.Identifier,
                DisplayName = request.DisplayName,
                ContactEmail = request.ContactEmail,
                ContactName = request.ContactName
            };

            var created = await customerStore.CreateAsync(customer, cancellationToken);
            return Results.Created($"/api/v1/customers/{created.Id}", CustomerResponse.FromEntity(created));
        })
        .WithName("CreateCustomer")
        .WithSummary("Create customer");

        // PUT /customers/{id}
        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateCustomerRequest request,
            [FromServices] ICustomerStore customerStore,
            CancellationToken cancellationToken) =>
        {
            var customer = await customerStore.GetByIdAsync(id, cancellationToken);
            if (customer == null)
            {
                return Results.NotFound(new { Error = "Customer not found" });
            }

            customer.DisplayName = request.DisplayName;
            customer.ContactEmail = request.ContactEmail;
            customer.ContactName = request.ContactName;

            var updated = await customerStore.UpdateAsync(customer, cancellationToken);
            return Results.Ok(CustomerResponse.FromEntity(updated));
        })
        .WithName("UpdateCustomer")
        .WithSummary("Update customer");

        // DELETE /customers/{id}
        group.MapDelete("/{id:guid}", async (
            Guid id,
            [FromServices] ICustomerStore customerStore,
            CancellationToken cancellationToken) =>
        {
            var customer = await customerStore.GetByIdAsync(id, cancellationToken);
            if (customer == null)
            {
                return Results.NotFound(new { Error = "Customer not found" });
            }

            var success = await customerStore.SoftDeleteAsync(id, cancellationToken);
            if (!success)
            {
                return Results.BadRequest(new { Error = "Cannot delete customer with active licenses" });
            }

            return Results.NoContent();
        })
        .WithName("DeleteCustomer")
        .WithSummary("Soft-delete customer");
    }
}
