using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Registrations;

[AllowAnonymous]
public class AcceptedModel(
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : PageModel
{
    public string Title { get; private set; } = "Registration accepted";

    public string Message { get; private set; } = "Registration submitted. We'll notify you when it's approved.";

    public string Detail { get; private set; } = "An administrator must approve the request before confirmation email is sent.";

    public string HomeUrl { get; private set; } = "/";

    public string LoginUrl { get; private set; } = "/Login";

    public string NewRegistrationUrl { get; private set; } = "/Registrations";

    public void OnGet(string? status = null, string? returnUrl = null, string? tenantName = null)
    {
        var requestPath = HttpContext.Request.Path.Value ?? string.Empty;
        var isTenantPath = multiTenancyOptions.Enabled
            && requestPath.StartsWith("/t/", StringComparison.OrdinalIgnoreCase);
        var tenantSlug = tenantAccessor.CurrentTenant?.Slug;
        var tenantPrefix = isTenantPath && !string.IsNullOrWhiteSpace(tenantSlug)
            ? $"/t/{Uri.EscapeDataString(tenantSlug)}"
            : string.Empty;

        HomeUrl = string.IsNullOrEmpty(tenantPrefix) ? "/" : $"{tenantPrefix}/";
        LoginUrl = BuildLoginUrl(tenantPrefix, returnUrl);
        NewRegistrationUrl = $"{tenantPrefix}/Registrations";

        switch (NormalizeStatus(status))
        {
            case "approved":
                Title = "Registration successful";
                Message = "Registration successful. Your account is ready for sign-in after email confirmation.";
                Detail = "Check your inbox for confirmation instructions.";
                break;
            case "domain-approved":
                Title = "Registration successful";
                Message = string.IsNullOrWhiteSpace(tenantName)
                    ? "Registration successful. Your account has been added to the matching tenant."
                    : $"Registration successful. Your account has been added to {tenantName}.";
                Detail = "Check your inbox for confirmation instructions.";
                break;
            case "tenant-created":
                Title = "Registration successful";
                Message = string.IsNullOrWhiteSpace(tenantName)
                    ? "Registration successful. You've been approved as the tenant admin."
                    : $"Registration successful. You've been approved as the tenant admin for {tenantName}.";
                Detail = "Check your inbox for confirmation instructions.";
                break;
            case "pending-existing":
                Title = "Registration already pending";
                Message = "A registration request for this email is already pending approval.";
                Detail = "You'll be notified when it's reviewed.";
                break;
            default:
                Title = "Registration accepted";
                Message = "Registration submitted. We'll notify you when it's approved.";
                Detail = "An administrator must approve the request before confirmation email is sent.";
                break;
        }
    }

    private static string NormalizeStatus(string? status)
        => string.IsNullOrWhiteSpace(status) ? "pending" : status.Trim().ToLowerInvariant();

    private static string BuildLoginUrl(string tenantPrefix, string? returnUrl)
    {
        var loginUrl = string.IsNullOrEmpty(tenantPrefix) ? "/Login" : $"{tenantPrefix}/login";
        return string.IsNullOrWhiteSpace(returnUrl)
            ? loginUrl
            : $"{loginUrl}?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }
}