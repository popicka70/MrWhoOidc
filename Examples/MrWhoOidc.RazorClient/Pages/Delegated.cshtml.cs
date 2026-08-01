using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.RazorClient.Services;

namespace MrWhoOidc.RazorClient.Pages;

[Authorize]
public sealed class DelegatedModel(DelegatedApiClient delegatedApiClient) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? DelegationId { get; set; }

    public DelegatedDemoResponse? Result { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (DelegationId is null || DelegationId == Guid.Empty)
        {
            Error = "Select an accepted delegated task before continuing.";
            return Page();
        }

        var subjectToken = await HttpContext.GetTokenAsync("access_token").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(subjectToken))
        {
            Error = "The signed-in delegate has no access token.";
            return Page();
        }

        try
        {
            Result = await delegatedApiClient.ExchangeAndCallProfileAsync(
                subjectToken,
                DelegationId.Value,
                HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }

        return Page();
    }
}