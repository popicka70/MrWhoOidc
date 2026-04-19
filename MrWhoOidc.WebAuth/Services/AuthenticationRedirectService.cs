using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.WebAuth.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.Services;

public sealed class AuthenticationRedirectService(
    ITenantAccessor tenantAccessor,
    ILoginContinuationStore continuationStore,
    IAuthorizeResponseGenerator responseGenerator) : IAuthenticationRedirectService
{
    public async Task<IResult> RedirectToLoginAsync(HttpContext http, ProviderSelectionResult selection, AuthorizeValidationResult validation, string? display = null, CancellationToken ct = default)
    {
        var returnUrl = await AuthorizeReturnUrlHelper.GetOrCreateLocalAuthorizeReturnUrlAsync(http).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(selection.AutoRedirectProvider))
        {
            var url = $"/auth/external/start?provider={Uri.EscapeDataString(selection.AutoRedirectProvider)}&clientId={Uri.EscapeDataString(validation.ClientId!)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
            return Results.Redirect(url);
        }

        if (selection.RequiresSelection)
        {
            var url2 = $"/auth/providers/select?client_id={Uri.EscapeDataString(validation.ClientId!)}&ReturnUrl={Uri.EscapeDataString(returnUrl)}";
            // Note: idp_hint is already handled by ProviderSelectionService and reflected in selection.RequiresSelection
            return Results.Redirect(url2);
        }

        // Fallback: local login if allowed
        if (selection.AllowLocal)
        {
            var ctx = await continuationStore.StoreAsync(returnUrl, ct).ConfigureAwait(false);
            var loginPath = BuildTenantAwareUrl("/login");
            var loginUrl = QueryHelpers.AddQueryString(loginPath, "ctx", ctx);

            // OIDC display parameter: propagate only known safe values.
            // If display=popup, the login UI uses a tighter layout without footer.
            var normalizedDisplay = NormalizeDisplay(display);
            if (!string.IsNullOrEmpty(normalizedDisplay))
            {
                loginUrl = QueryHelpers.AddQueryString(loginUrl, "display", normalizedDisplay);
            }
            return Results.Redirect(loginUrl);
        }

        // If local login not allowed and no external/QR path chosen, return access_denied
        return responseGenerator.CreateErrorResponse(http, validation with { Error = "access_denied", ErrorDescription = "No permitted login methods for this client" }, "system");
    }

    private string BuildTenantAwareUrl(string path)
    {
        var currentTenant = tenantAccessor.CurrentTenant;
        if (!path.StartsWith('/')) path = "/" + path;
        if (currentTenant != null && currentTenant.IsMultiTenantMode)
        {
            return $"/t/{currentTenant.Slug}{path}";
        }
        return path;
    }

    private static string? NormalizeDisplay(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return null;
        if (string.Equals(display, "popup", StringComparison.OrdinalIgnoreCase)) return "popup";
        return null;
    }
}
