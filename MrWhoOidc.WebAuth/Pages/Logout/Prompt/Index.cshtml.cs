using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Web;

namespace MrWhoOidc.WebAuth.Pages.Logout.Prompt;

public class IndexModel : PageModel
{
    public string ProviderDisplay { get; set; } = "Provider";
    public string ReturnUrl { get; set; } = "/";
    public bool CanFederate { get; set; }

    public void OnGet(string? provider, string? ret)
    {
        if (!string.IsNullOrEmpty(provider)) ProviderDisplay = provider;
        if (!string.IsNullOrEmpty(ret) && Uri.TryCreate(ret, UriKind.Relative, out _)) ReturnUrl = ret;
    }
}
