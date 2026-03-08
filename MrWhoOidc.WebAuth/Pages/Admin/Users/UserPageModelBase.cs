using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users;

/// <summary>
/// Base class for user admin Razor Pages providing a consistent user heading (username + optional friendly name).
/// Inherits from TenantAwarePageModel to provide tenant-aware redirects and enforce read-only mode during impersonation.
/// </summary>
public abstract class UserPageModelBase : TenantAwarePageModel
{
    protected UserPageModelBase(ITenantAccessor tenantAccessor, IMultiTenancyOptions multiTenancyOptions)
        : base(tenantAccessor, multiTenancyOptions)
    {
    }

    public string UserHeading { get; private set; } = string.Empty;

    protected void SetHeading(string username, string? name)
    {
        UserHeading = string.IsNullOrWhiteSpace(name) || string.Equals(username, name, StringComparison.OrdinalIgnoreCase)
            ? username
            : $"{username} ({name})";
        ViewData["UserHeading"] = UserHeading;
    }
}
