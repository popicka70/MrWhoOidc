using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Account;

public class AccessDeniedModel : PageModel
{
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMultiTenancyOptions _multiTenancyOptions;

    public AccessDeniedModel(
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions)
    {
        _tenantAccessor = tenantAccessor;
        _multiTenancyOptions = multiTenancyOptions;
    }

    public string? ReturnUrl { get; set; }
    public bool IsAuthenticated { get; set; }
    public string HomeUrl { get; set; } = "/";
    public string? DashboardUrl { get; set; }
    public string? AccountUrl { get; set; }
    public string LoginUrl { get; set; } = "/login";

    public void OnGet(string? returnUrl = null)
    {
        // Without an explicit returnUrl the denied URL is the request that produced this
        // response; showing it (rather than nothing) keeps the block informative.
        ReturnUrl = returnUrl ?? (HttpContext.Request.Path + HttpContext.Request.QueryString).TrimEnd('?');
        IsAuthenticated = User?.Identity?.IsAuthenticated ?? false;

        // Build tenant-aware URLs
        var currentTenant = _tenantAccessor.CurrentTenant;
        var isMultiTenant = _multiTenancyOptions.Enabled && currentTenant != null;

        if (isMultiTenant)
        {
            var tenantPrefix = $"/t/{currentTenant!.Slug}";
            HomeUrl = tenantPrefix;
            LoginUrl = $"{tenantPrefix}/login";
            AccountUrl = $"{tenantPrefix}/Account";

            // Admins get the admin area; regular users get their account dashboard.
            if (User?.IsInRole("admin") == true || User?.IsInRole("tenant-admin") == true)
            {
                DashboardUrl = $"{tenantPrefix}/Admin/Users";
            }
            else if (IsAuthenticated)
            {
                DashboardUrl = $"{tenantPrefix}/Account";
            }
        }
        else
        {
            HomeUrl = "/";
            LoginUrl = "/login";
            AccountUrl = "/Account";

            if (User?.IsInRole("admin") == true)
            {
                DashboardUrl = "/Admin/Users";
            }
            else if (IsAuthenticated)
            {
                DashboardUrl = "/Account";
            }
        }
    }
}
