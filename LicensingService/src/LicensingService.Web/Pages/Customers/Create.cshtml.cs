using System.ComponentModel.DataAnnotations;
using LicensingService.Core;
using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensingService.Web.Pages.Customers;

public class CreateModel : PageModel
{
    private readonly ICustomerStore _customerStore;

    public CreateModel(ICustomerStore customerStore)
    {
        _customerStore = customerStore;
    }

    [BindProperty]
    public CustomerInput Customer { get; set; } = new();

    public class CustomerInput
    {
        [Required]
        [RegularExpression(@"^[a-z0-9-]+$", ErrorMessage = "Identifier must be lowercase alphanumeric with hyphens")]
        [StringLength(100)]
        public string Identifier { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string DisplayName { get; set; } = string.Empty;

        [EmailAddress]
        [StringLength(255)]
        public string? ContactEmail { get; set; }

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
        var existing = await _customerStore.GetByIdentifierAsync(Customer.Identifier);
        if (existing != null)
        {
            ModelState.AddModelError("Customer.Identifier", "A customer with this identifier already exists");
            return Page();
        }

        var customer = new Customer
        {
            Id = GuidHelper.NewId(),
            Identifier = Customer.Identifier.ToLowerInvariant(),
            DisplayName = Customer.DisplayName,
            ContactEmail = Customer.ContactEmail,
            Status = Customer.Status,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _customerStore.CreateAsync(customer);

        TempData["Success"] = $"Customer '{customer.DisplayName}' created successfully.";
        return RedirectToPage("Index");
    }
}
