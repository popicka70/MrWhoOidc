using System.ComponentModel.DataAnnotations;
using LicensingService.Core;
using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensingService.Web.Pages.Products;

public class EditModel : PageModel
{
    private readonly IProductStore _productStore;

    public EditModel(IProductStore productStore)
    {
        _productStore = productStore;
    }

    [BindProperty]
    public ProductInput Product { get; set; } = new();

    public IReadOnlyList<ProductOptionDefinition> Options { get; set; } = [];

    public class ProductInput
    {
        public Guid Id { get; set; }

        [Required]
        public string Identifier { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(1000)]
        public string? Description { get; set; }

        public string Status { get; set; } = "Active";
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var product = await _productStore.GetByIdAsync(id);
        if (product == null)
        {
            return NotFound();
        }

        Product = new ProductInput
        {
            Id = product.Id,
            Identifier = product.Identifier,
            DisplayName = product.DisplayName,
            Description = product.Description,
            Status = product.Status
        };

        Options = product.OptionDefinitions?.ToList() ?? [];

        return Page();
    }

    public async Task<IActionResult> OnPostUpdateProductAsync()
    {
        if (!ModelState.IsValid)
        {
            var product = await _productStore.GetByIdAsync(Product.Id);
            Options = product?.OptionDefinitions?.ToList() ?? [];
            return Page();
        }

        var existingProduct = await _productStore.GetByIdAsync(Product.Id);
        if (existingProduct == null)
        {
            return NotFound();
        }

        existingProduct.DisplayName = Product.DisplayName;
        existingProduct.Description = Product.Description;
        existingProduct.Status = Product.Status;

        await _productStore.UpdateAsync(existingProduct);

        TempData["Success"] = $"Product '{existingProduct.DisplayName}' updated successfully.";
        return RedirectToPage(new { id = Product.Id });
    }

    public async Task<IActionResult> OnPostAddOptionAsync(
        string optionKey,
        string displayName,
        string dataType,
        string? defaultValue,
        string? description)
    {
        var product = await _productStore.GetByIdAsync(Product.Id);
        if (product == null)
        {
            return NotFound();
        }

        if (!Enum.TryParse<OptionDataType>(dataType, out var parsedDataType))
        {
            parsedDataType = OptionDataType.String;
        }

        var option = new ProductOptionDefinition
        {
            Id = GuidHelper.NewId(),
            ProductId = product.Id,
            OptionKey = optionKey.ToLowerInvariant(),
            DisplayName = displayName,
            DataType = parsedDataType,
            DefaultValue = defaultValue,
            Description = description,
            SortOrder = (product.OptionDefinitions?.Count ?? 0) + 1
        };

        await _productStore.AddOptionDefinitionAsync(option);

        TempData["Success"] = $"Option '{displayName}' added successfully.";
        return RedirectToPage(new { id = Product.Id });
    }

    public async Task<IActionResult> OnPostDeleteOptionAsync(Guid optionId)
    {
        var product = await _productStore.GetByIdAsync(Product.Id);
        if (product == null)
        {
            return NotFound();
        }

        await _productStore.RemoveOptionDefinitionAsync(product.Id, optionId);

        TempData["Success"] = "Option removed successfully.";
        return RedirectToPage(new { id = Product.Id });
    }
}
