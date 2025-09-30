using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.RazorClient.Services;

namespace MrWhoOidc.RazorClient.Pages;

[Authorize]
public class SecureModel : PageModel
{
    private readonly TestApiClient _apiClient;
    private readonly ILogger<SecureModel> _logger;

    public SecureModel(TestApiClient apiClient, ILogger<SecureModel> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public TestApiClient.TestApiResponse? Profile { get; private set; }
    public bool ApiCallSucceeded { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync()
    {
        try
        {
            Profile = await _apiClient.GetProfileAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            ApiCallSucceeded = Profile is not null;
            if (!ApiCallSucceeded)
            {
                ErrorMessage = "Downstream API call failed or returned no data.";
            }
        }
        catch (Exception ex)
        {
            ApiCallSucceeded = false;
            ErrorMessage = ex.Message;
            _logger.LogWarning(ex, "Error while invoking the OBO-protected API.");
        }
    }
}
