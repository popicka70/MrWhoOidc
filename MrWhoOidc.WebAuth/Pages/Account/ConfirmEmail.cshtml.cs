using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Account;

[AllowAnonymous]
public class ConfirmEmailModel(IEmailConfirmationService confirmationService) : PageModel
{
    public EmailConfirmationVerifyResult? Result { get; private set; }
    public string? Token { get; private set; }

    public async Task OnGetAsync(string? token)
    {
        Token = token;
        Result = await confirmationService.ConfirmAsync(token, HttpContext.RequestAborted);
    }
}
