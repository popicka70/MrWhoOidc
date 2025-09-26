using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;

namespace MrWhoOidc.WebAuth.Pages.Auth.External;

public class ErrorModel : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Cid { get; set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public string? ClientId { get; set; }

    public string? CorrelationId => Cid;
    public string? Message => Msg;

    public void OnGet() { }
}
