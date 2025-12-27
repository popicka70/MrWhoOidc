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
    public async Task<IResult> RedirectToLoginAsync(HttpContext http, ProviderSelectionResult selection, AuthorizeValidationResult validation, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(selection.AutoRedirectProvider))
        {
            var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
            var url = $"/auth/external/start?provider={Uri.EscapeDataString(selection.AutoRedirectProvider)}&clientId={Uri.EscapeDataString(validation.ClientId!)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
            return Results.Redirect(url);
        }

        if (selection.RequiresSelection)
        {
            var ret = http.Request.Path + http.Request.QueryString.ToUriComponent();
            var url2 = $"/auth/providers/select?client_id={Uri.EscapeDataString(validation.ClientId!)}&ReturnUrl={Uri.EscapeDataString(ret)}";
            // Note: idp_hint is already handled by ProviderSelectionService and reflected in selection.RequiresSelection
            return Results.Redirect(url2);
        }

        // Fallback: local login if allowed
        if (selection.AllowLocal)
        {
            var returnUrl2 = http.Request.Path + http.Request.QueryString.ToUriComponent();
            var ctx = await continuationStore.StoreAsync(returnUrl2, ct).ConfigureAwait(false);
            var loginPath = BuildTenantAwareUrl("/login");
            var loginUrl = QueryHelpers.AddQueryString(loginPath, "ctx", ctx);
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
}
