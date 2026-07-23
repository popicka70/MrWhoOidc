using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.RazorClient.Services;

namespace MrWhoOidc.RazorClient.Pages;

[Authorize]
public sealed class DelegatedModel(DelegatedApiClient delegatedApiClient) : PageModel
{
    [BindProperty]
    public Guid DelegationId { get; set; }

    public TestApiClient.TestApiResponse? Result { get; private set; }
    public string? Error { get; private set; }

    public async Task OnPostAsync()
    {
        var subjectToken = await HttpContext.GetTokenAsync("access_token").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(subjectToken))
        {
            Error = "The signed-in delegate has no access token.";
            return;
        }

        try
        {
            Result = await delegatedApiClient.ExchangeAndCallAsync(
                subjectToken,
                DelegationId,
                HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
    }
}