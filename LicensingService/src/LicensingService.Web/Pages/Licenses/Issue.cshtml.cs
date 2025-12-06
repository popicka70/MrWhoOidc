using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using LicensingService.Core.Entities;
using LicensingService.Core.Services;
using LicensingService.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensingService.Web.Pages.Licenses;

public class IssueModel : PageModel
{
    private readonly ILicenseService _licenseService;
    private readonly ICustomerStore _customerStore;
    private readonly IProductStore _productStore;

    public IssueModel(
        ILicenseService licenseService,
        ICustomerStore customerStore,
        IProductStore productStore)
    {
        _licenseService = licenseService;
        _customerStore = customerStore;
        _productStore = productStore;
    }

    [BindProperty]
    public LicenseInput License { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public Guid? CustomerId { get; set; }

    public IReadOnlyList<Customer> Customers { get; set; } = [];
    public IReadOnlyList<LicensedProduct> Products { get; set; } = [];

    public class LicenseInput
    {
        [Required]
        public Guid CustomerId { get; set; }

        [Required]
        public Guid ProductId { get; set; }

        [Required]
        public string Tier { get; set; } = "Professional";

        public string Scope { get; set; } = "site";

        public DateTimeOffset? ValidFrom { get; set; }

        [Required]
        public DateTimeOffset ValidUntil { get; set; } = DateTimeOffset.UtcNow.AddYears(1);

        public string? OptionsJson { get; set; }
    }

    public async Task OnGetAsync()
    {
        await LoadDataAsync();

        if (CustomerId.HasValue)
        {
            License.CustomerId = CustomerId.Value;
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadDataAsync();
            return Page();
        }

        Dictionary<string, object>? options = null;
        if (!string.IsNullOrWhiteSpace(License.OptionsJson))
        {
            try
            {
                options = JsonSerializer.Deserialize<Dictionary<string, object>>(License.OptionsJson);
            }
            catch (JsonException)
            {
                ModelState.AddModelError("License.OptionsJson", "Invalid JSON format");
                await LoadDataAsync();
                return Page();
            }
        }

        var request = new IssueLicenseRequest
        {
            CustomerId = License.CustomerId,
            ProductId = License.ProductId,
            Tier = License.Tier,
            Scope = License.Scope,
            ValidFrom = License.ValidFrom,
            ValidUntil = License.ValidUntil,
            Options = options
        };

        var result = await _licenseService.IssueLicenseAsync(request, User.Identity?.Name ?? "admin");

        if (!result.Success)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Failed to issue license");
            await LoadDataAsync();
            return Page();
        }

        TempData["Success"] = "License issued successfully.";
        return RedirectToPage("Details", new { id = result.License!.Id });
    }

    private async Task LoadDataAsync()
    {
        var customersResult = await _customerStore.GetAllAsync(status: "Active", pageSize: 1000);
        Customers = customersResult.Items.OrderBy(c => c.DisplayName).ToList();

        Products = (await _productStore.GetAllAsync())
            .Where(p => p.Status == "Active")
            .OrderBy(p => p.DisplayName)
            .ToList();
    }
}
