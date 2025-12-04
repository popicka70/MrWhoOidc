using System.ComponentModel.DataAnnotations;
using LicensingService.Core;
using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensingService.Web.Pages.Products;

public class CreateModel : PageModel
{
    private readonly IProductStore _productStore;

    public CreateModel(IProductStore productStore)
    {
        _productStore = productStore;
    }

    [BindProperty]
    public ProductInput Product { get; set; } = new();

    public class ProductInput
    {
        [Required]
        [RegularExpression(@"^[a-z0-9-]+$", ErrorMessage = "Identifier must be lowercase alphanumeric with hyphens")]
        [StringLength(100)]
        public string Identifier { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(1000)]
        public string? Description { get; set; }

        public string Status { get; set; } = "Active";
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Check for duplicate identifier
        var existing = await _productStore.GetByIdentifierAsync(Product.Identifier);
        if (existing != null)
        {
            ModelState.AddModelError("Product.Identifier", "A product with this identifier already exists");
            return Page();
        }

        var product = new LicensedProduct
        {
            Id = GuidHelper.NewId(),
            Identifier = Product.Identifier.ToLowerInvariant(),
            DisplayName = Product.DisplayName,
            Description = Product.Description,
            Status = Product.Status,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _productStore.CreateAsync(product);

        TempData["Success"] = $"Product '{product.DisplayName}' created successfully.";
        return RedirectToPage("Edit", new { id = product.Id });
    }
}
