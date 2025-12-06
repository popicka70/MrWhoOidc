using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensingService.Web.Pages.Products;

public class IndexModel : PageModel
{
    private readonly IProductStore _productStore;
    private readonly ILicenseStore _licenseStore;

    public IndexModel(IProductStore productStore, ILicenseStore licenseStore)
    {
        _productStore = productStore;
        _licenseStore = licenseStore;
    }

    public IReadOnlyList<LicensedProduct> Products { get; set; } = [];
    public Dictionary<Guid, int> LicenseCounts { get; set; } = new();

    public async Task OnGetAsync()
    {
        Products = await _productStore.GetAllAsync();

        foreach (var product in Products)
        {
            var licenses = await _licenseStore.GetByProductIdAsync(product.Id);
            LicenseCounts[product.Id] = licenses.Count;
        }
    }
}
