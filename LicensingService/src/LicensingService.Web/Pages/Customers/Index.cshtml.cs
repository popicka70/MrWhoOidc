using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensingService.Web.Pages.Customers;

public class IndexModel : PageModel
{
    private readonly ICustomerStore _customerStore;
    private readonly ILicenseStore _licenseStore;

    public IndexModel(ICustomerStore customerStore, ILicenseStore licenseStore)
    {
        _customerStore = customerStore;
        _licenseStore = licenseStore;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? StatusFilter { get; set; }

    public IReadOnlyList<Customer> Customers { get; set; } = [];
    public Dictionary<Guid, int> LicenseCounts { get; set; } = new();

    public async Task OnGetAsync()
    {
        var allCustomers = await _customerStore.GetAllAsync();

        // Apply filters
        var filtered = allCustomers.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var searchLower = Search.ToLowerInvariant();
            filtered = filtered.Where(c =>
                c.DisplayName.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ||
                c.Identifier.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ||
                (c.ContactEmail?.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(StatusFilter))
        {
            filtered = filtered.Where(c => c.Status == StatusFilter);
        }

        Customers = filtered.OrderBy(c => c.DisplayName).ToList();

        // Get license counts for each customer
        foreach (var customer in Customers)
        {
            var licenses = await _licenseStore.GetByCustomerIdAsync(customer.Id);
            LicenseCounts[customer.Id] = licenses.Count;
        }
    }
}
