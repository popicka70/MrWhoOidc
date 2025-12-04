using System.ComponentModel.DataAnnotations;
using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensingService.Web.Pages.Customers;

public class EditModel : PageModel
{
    private readonly ICustomerStore _customerStore;

    public EditModel(ICustomerStore customerStore)
    {
        _customerStore = customerStore;
    }

    [BindProperty]
    public CustomerInput Customer { get; set; } = new();

    public class CustomerInput
    {
        public Guid Id { get; set; }

        [Required]
        public string Identifier { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string DisplayName { get; set; } = string.Empty;

        [EmailAddress]
        [StringLength(255)]
        public string? ContactEmail { get; set; }

        public string Status { get; set; } = "Active";

        public string? Notes { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var customer = await _customerStore.GetByIdAsync(id);
        if (customer == null)
        {
            return NotFound();
        }

        Customer = new CustomerInput
        {
            Id = customer.Id,
            Identifier = customer.Identifier,
            DisplayName = customer.DisplayName,
            ContactEmail = customer.ContactEmail,
            Status = customer.Status,
            Notes = customer.Notes
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var customer = await _customerStore.GetByIdAsync(Customer.Id);
        if (customer == null)
        {
            return NotFound();
        }

        customer.DisplayName = Customer.DisplayName;
        customer.ContactEmail = Customer.ContactEmail;
        customer.Status = Customer.Status;
        customer.Notes = Customer.Notes;

        await _customerStore.UpdateAsync(customer);

        TempData["Success"] = $"Customer '{customer.DisplayName}' updated successfully.";
        return RedirectToPage("Details", new { id = customer.Id });
    }
}
