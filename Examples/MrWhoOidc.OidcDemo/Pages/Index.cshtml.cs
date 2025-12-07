using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.OidcDemo.Pages;

public class IndexModel : PageModel
{
    private readonly IConfiguration _configuration;

    public IndexModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string Authority => _configuration["OidcSettings:Authority"] ?? "Not configured";
    public string ClientId => _configuration["OidcSettings:ClientId"] ?? "Not configured";

    public void OnGet()
    {
    }
}
