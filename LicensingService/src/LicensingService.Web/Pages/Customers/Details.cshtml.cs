using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensingService.Web.Pages.Customers;

public class DetailsModel : PageModel
{
    private readonly ICustomerStore _customerStore;
    private readonly ILicenseStore _licenseStore;

    public DetailsModel(ICustomerStore customerStore, ILicenseStore licenseStore)
    {
        _customerStore = customerStore;
        _licenseStore = licenseStore;
    }

    public Customer Customer { get; set; } = null!;
    public IReadOnlyList<License> Licenses { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var customer = await _customerStore.GetByIdAsync(id);
        if (customer == null)
        {
            return NotFound();
        }

        Customer = customer;
        Licenses = await _licenseStore.GetByCustomerIdAsync(id);

        return Page();
    }
}
