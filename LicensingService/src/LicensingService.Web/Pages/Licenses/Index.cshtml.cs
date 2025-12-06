using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using static LicensingService.Core.Stores.ILicenseStore;

namespace LicensingService.Web.Pages.Licenses;

public class IndexModel : PageModel
{
    private readonly ILicenseStore _licenseStore;
    private readonly ICustomerStore _customerStore;
    private readonly IProductStore _productStore;

    public IndexModel(
        ILicenseStore licenseStore,
        ICustomerStore customerStore,
        IProductStore productStore)
    {
        _licenseStore = licenseStore;
        _customerStore = customerStore;
        _productStore = productStore;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? CustomerId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? ProductId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tier { get; set; }

    public IReadOnlyList<License> Licenses { get; set; } = [];
    public IReadOnlyList<Customer> Customers { get; set; } = [];
    public IReadOnlyList<LicensedProduct> Products { get; set; } = [];
    public int TotalCount { get; set; }

    public async Task OnGetAsync()
    {
        var customersResult = await _customerStore.GetAllAsync(pageSize: 1000);
        Customers = customersResult.Items;
        Products = await _productStore.GetAllAsync();

        LicenseStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(Status) && Enum.TryParse<LicenseStatus>(Status, true, out var parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        var criteria = new LicenseSearchCriteria
        {
            CustomerId = CustomerId,
            ProductId = ProductId,
            Status = statusFilter,
            Tier = Tier
        };

        var result = await _licenseStore.SearchAsync(criteria, skip: 0, take: 100);

        Licenses = result.Items;
        TotalCount = result.TotalCount;
    }
}
