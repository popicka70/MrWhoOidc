using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.MultiTenancy;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.Pages.Account;

public class AccessDeniedModel : PageModel
{
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IOptions<MultiTenancyOptions> _multiTenancyOptions;

    public AccessDeniedModel(
        ITenantAccessor tenantAccessor,
        IOptions<MultiTenancyOptions> multiTenancyOptions)
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
        ReturnUrl = returnUrl;
        IsAuthenticated = User?.Identity?.IsAuthenticated ?? false;

        // Build tenant-aware URLs
        var currentTenant = _tenantAccessor.CurrentTenant;
        var isMultiTenant = _multiTenancyOptions.Value.Enabled && currentTenant != null;

        if (isMultiTenant)
        {
            var tenantPrefix = $"/t/{currentTenant!.Slug}";
            HomeUrl = tenantPrefix;
            LoginUrl = $"{tenantPrefix}/login";
            AccountUrl = $"{tenantPrefix}/Account";
            
            // Check if user has admin access
            if (User?.IsInRole("admin") == true || User?.IsInRole("tenant-admin") == true)
            {
                DashboardUrl = $"{tenantPrefix}/Admin/Users";
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
        }
    }
}
