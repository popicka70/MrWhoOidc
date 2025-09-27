using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.WebAuth.Pages.Logout;

public class FederatedCallbackErrorModel : PageModel
{
    public string? Reason { get; set; }
    public string? Style { get; set; }
    public void OnGet(string? reason, string? style)
    {
        Reason = reason;
        Style = style;
    }
}
