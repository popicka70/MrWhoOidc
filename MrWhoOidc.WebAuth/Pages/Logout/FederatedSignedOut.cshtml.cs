using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.WebAuth.Pages.Logout;

public class FederatedSignedOutModel : PageModel
{
    public string? Style { get; set; }
    public void OnGet(string? style)
    {
        Style = style;
    }
}
