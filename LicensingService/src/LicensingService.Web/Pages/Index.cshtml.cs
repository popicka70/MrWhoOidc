using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensingService.Web.Pages;

public class IndexModel : PageModel
{
    private readonly ICustomerStore _customerStore;
    private readonly IProductStore _productStore;
    private readonly ILicenseStore _licenseStore;

    public IndexModel(
        ICustomerStore customerStore,
        IProductStore productStore,
        ILicenseStore licenseStore)
    {
        _customerStore = customerStore;
        _productStore = productStore;
        _licenseStore = licenseStore;
    }

    public int CustomerCount { get; set; }
    public int ProductCount { get; set; }
    public int ActiveLicenseCount { get; set; }
    public IReadOnlyList<License> ExpiringLicenses { get; set; } = [];

    public async Task OnGetAsync()
    {
        var customers = await _customerStore.GetAllAsync();
        CustomerCount = customers.Count;

        var products = await _productStore.GetAllAsync();
        ProductCount = products.Count;

        var activeLicenses = await _licenseStore.SearchAsync(
            status: LicenseStatus.Active,
            skip: 0,
            take: 1);
        ActiveLicenseCount = activeLicenses.TotalCount;

        ExpiringLicenses = await _licenseStore.GetExpiringLicensesAsync(30);
    }
}
